module Client.WorkQueueSchedulerHostPreview

open System
open System.IO
open System.Text.Json
open Jarvis.WorkQueues
open Jarvis.WorkQueues.Contracts
open Jarvis.WorkQueues.WorkQueueProducers
open Client.LedgerStore
open Client.WorkQueueGitLedger
open Client.WorkQueueRoleAuthority
open Client.AccountCapacityLedger
open Client.OrchestrationApi

// An explicit read-only view over actual, independently trusted project/account
// Git authorities. No timer, live Scheduler, capacity reservation or browser
// submission is installed by this module.
type PreviewIssue =
    { Code: string
      Record: string
      NextAction: string }

type PreviewCandidate =
    { WorkId: string
      WorkGeneration: int
      QueueId: string
      RoleId: string
      RoleVersion: int
      SourceHead: string option
      SourceRef: string option
      SourceCommit: string option
      SourceOperationId: string option
      SourceRequestHash: string option
      OriginBlobOid: string option }

type PreviewLaunch =
    { LaunchId: string
      RoleId: string
      RoleVersion: int
      Credit: string option
      State: string
      AdmissionOperationId: string option
      AdmissionKind: string option
      OriginalBrowserOperationId: string option
      OriginalBrowserOutcome: string
      OriginalBrowserReceipt: string option }

type SchedulerHostPreviewReport =
    { Mode: string
      CanDispatch: bool
      AccountHead: string option
      AccountMigration: string option
      MaxConcurrentAgents: int option
      OccupiedAgents: int option
      AvailableSlots: int option
      ProjectHead: string option
      ProjectVersion: int option
      RoleCatalogRevision: int option
      RoleId: string
      SelectedRoleVersion: int option
      EligibleWork: string list
      SkippedWork: string list
      WouldLaunch: PreviewCandidate option
      ExistingLaunches: PreviewLaunch list
      Issues: PreviewIssue list }

// Injectable read seams keep preview deterministic and permit two-client tests.
// The production binding below only reads verified Git state and never writes.
// ReadOriginal is intentionally unbound until a WQ03 original browser journal
// has an audited, client-owned read adapter. Missing journal is not no-effect.
type SchedulerHostPreviewPorts =
    { ReadAccount: unit -> Result<AccountSnapshot, string>
      ReadProject: unit -> Result<LedgerSnapshot * PinnedRoleView, string>
      ReadOrigin: LedgerSnapshot -> string -> Result<(CanonicalWorkOrigin * string) option, string>
      ReadAdmission: LedgerSnapshot -> string -> Result<(ProjectAdmissionRecord * string) option, string>
      ReadOriginal: string -> Result<OriginalBrowserJournalEntry option, string> }

module WorkQueueSchedulerHostPreview =
    let private issue code record action =
        { Code = code; Record = record; NextAction = action }

    let private browserOutcome = function
        | None -> "MarkerPresentOutcomeUnknown", None
        | Some(OriginalBrowserOutcome.Accepted receipt) -> "Accepted", Some receipt
        | Some(OriginalBrowserOutcome.DefinitiveNoEffect reason) ->
            "DefinitiveNoEffect: " + reason, None
        | Some(OriginalBrowserOutcome.Unknown reason) -> "Unknown: " + reason, None

    // Do not expose a provider session key or registered UUID in a preview.
    let private publicLaunchState = function
        | LaunchState.ReservedUnregistered -> "ReservedUnregistered"
        | LaunchState.SubmittedUnregistered status -> sprintf "SubmittedUnregistered(%A)" status
        | LaunchState.Registered _ -> "Registered"
        | LaunchState.DemandInvalidatedPendingTerminal _ -> "DemandInvalidatedPendingTerminal"
        | LaunchState.TerminalPendingRelease _ -> "TerminalPendingRelease"

    // This result is information, never an executable admission grant.
    // A candidate is eligible only inside the selected project's pinned role
    // view; global cross-project priority and actual dispatch remain disabled.
    let previewWith (ports: SchedulerHostPreviewPorts) (roleId: string) =
        let issues = ResizeArray<PreviewIssue>()
        let add code record action = issues.Add(issue code record action)
        let account =
            try ports.ReadAccount()
            with ex -> Error ex.Message
        let project =
            try ports.ReadProject()
            with ex -> Error ex.Message

        let accountHead, migration, cap, occupied, available =
            match account with
            | Error error ->
                add "AccountUnknown" "Account capacity Git authority"
                    ("Inspect approved account binding and remote Git head: " + error)
                None, None, None, None, None
            | Ok snapshot ->
                let held = AccountCapacityLedger.occupied snapshot.State
                let free = max 0 (snapshot.State.MaxConcurrentAgents - held)
                if snapshot.State.MigrationPhase <> "Activated" then
                    add "AccountNotActivated" snapshot.Head
                        ("Account migration is " + snapshot.State.MigrationPhase
                         + "; do not activate Scheduler without operator approval.")
                if free = 0 then
                    add "CapacityFull" snapshot.Head
                        "All account-global slots are held; wait for verified terminal releases."
                Some snapshot.Head, Some snapshot.State.MigrationPhase,
                Some snapshot.State.MaxConcurrentAgents, Some held, Some free

        let mutable projectHead = None
        let mutable projectVersion = None
        let mutable roleRevision = None
        let mutable roleVersion = None
        let mutable candidates = []
        let mutable skipped = []
        let mutable selected = None
        let mutable launches = []

        match project with
        | Error error ->
            add "ProjectUnknown" "Canonical project Git authority"
                ("Inspect approved project trust binding, source and Git head: " + error)
        | Ok (snapshot, pinned) ->
            projectHead <- Some snapshot.Commit
            projectVersion <- Some pinned.Project.Version
            roleRevision <- Some pinned.Catalog.Revision
            if pinned.Proof.Head <> snapshot.Commit then
                add "ProjectHeadConflict" snapshot.Commit
                    "Read the versioned role catalog and project record at one verified head."
            let currentRole =
                pinned.Catalog.Current.TryFind roleId
                |> Option.bind (fun version ->
                    pinned.Catalog.Versions.TryFind roleId
                    |> Option.bind (fun versions -> versions.TryFind version))
            match currentRole with
            | None ->
                add "RoleUnknown" (roleId + " @ " + snapshot.Commit)
                    "Configure or inspect the exact WQ04 role version in the canonical project catalog."
            | Some role when not role.Enabled ->
                roleVersion <- Some role.Version
                add "RoleDisabled" (roleId + "/" + string role.Version)
                    "An authorized operator must enable the role before any future launch."
            | Some role ->
                roleVersion <- Some role.Version
                match Contracts.orderAvailable pinned.Project with
                | Error error ->
                    add "QueueUnknown" snapshot.Commit
                        ("Repair project queue coverage/ordering before previewing demand: " + string error)
                | Ok ordered ->
                    candidates <-
                        ordered
                        |> List.filter (fun work -> role.QueueSubscriptions.Contains work.QueueId)
                        |> List.map (fun work -> work.Id)
                match selectRoleDemandPractical pinned.Catalog pinned.Project roleId with
                | Error error ->
                    add "DemandUnknown" (roleId + " @ " + snapshot.Commit)
                        ("Inspect WQ04 versioned role/demand and source coverage: " + string error)
                | Ok selection ->
                    skipped <- selection.MatchingSkips
                    match selection.Chosen with
                    | None -> ()
                    | Some demand ->
                        let source =
                            try ports.ReadOrigin snapshot demand.WorkId
                            with ex -> Error ex.Message
                        let head, sourceRef, commit, operation, requestHash, oid =
                            match source with
                            | Ok (Some(origin, blob)) ->
                                Some origin.SourceHead,
                                (if String.IsNullOrWhiteSpace origin.SourceRef then None else Some origin.SourceRef),
                                (if String.IsNullOrWhiteSpace origin.SourceCommit then None else Some origin.SourceCommit),
                                Some origin.SourceOperationId, Some origin.SourceRequestHash, Some blob
                            | Ok None ->
                                add "SourceReceiptMissing" demand.WorkId
                                    ("Read WQ04 origin for work " + demand.WorkId
                                     + " at project head " + snapshot.Commit
                                     + "; do not invent a source SHA or receipt.")
                                None, None, None, None, None, None
                            | Error error ->
                                add "SourceReceiptUnknown" demand.WorkId
                                    ("Inspect immutable WQ04 source receipt: " + error)
                                None, None, None, None, None, None
                        selected <- Some
                            { WorkId = demand.WorkId
                              WorkGeneration = demand.StageGeneration
                              QueueId = demand.QueueId
                              RoleId = demand.RoleId
                              RoleVersion = demand.RoleVersion
                              SourceHead = head
                              SourceRef = sourceRef
                              SourceCommit = commit
                              SourceOperationId = operation
                              SourceRequestHash = requestHash
                              OriginBlobOid = oid }

            launches <-
                pinned.Project.Launches
                |> Map.toList
                |> List.map snd
                |> List.filter (fun launch -> launch.RoleId = roleId)
                |> List.truncate 64
                |> List.map (fun launch ->
                    let admission =
                        try ports.ReadAdmission snapshot launch.Id
                        with ex -> Error ex.Message
                    let admissionId, kind =
                        match admission with
                        | Ok(Some(record, _)) ->
                            Some record.AdmissionOperationId, Some record.Kind
                        | Ok None ->
                            add "AdmissionReceiptMissing" launch.Id
                                "Inspect original project admission or C01 tombstone at its verified Git head."
                            None, None
                        | Error error ->
                            add "AdmissionReceiptUnknown" launch.Id
                                ("Inspect immutable admission record: " + error)
                            None, None
                    let browser =
                        try ports.ReadOriginal launch.Id
                        with ex -> Error ex.Message
                    let browserId, outcome, receipt =
                        match browser with
                        | Ok(Some entry) when entry.Intent.LaunchId = launch.Id ->
                            let status, receipt = browserOutcome entry.Outcome
                            Some entry.Intent.OperationId, status, receipt
                        | Ok(Some _) ->
                            add "BrowserIdentityConflict" launch.Id
                                "Preserve original browser journal; investigate mismatched launch ID before any retry."
                            None, "IdentityConflict", None
                        | Ok None ->
                            add "BrowserJournalMissing" launch.Id
                                "Locate original WQ03 one-shot marker and receipt; absence is not proof of no effect."
                            None, "NotRecorded", None
                        | Error error ->
                            add "BrowserJournalUnknown" launch.Id
                                ("Read original WQ03 one-shot ID/outcome before any resend: " + error)
                            None, "Unknown", None
                    { LaunchId = launch.Id
                      RoleId = launch.RoleId
                      RoleVersion = launch.RoleVersion
                      Credit = launch.Credit
                      State = publicLaunchState launch.State
                      AdmissionOperationId = admissionId
                      AdmissionKind = kind
                      OriginalBrowserOperationId = browserId
                      OriginalBrowserOutcome = outcome
                      OriginalBrowserReceipt = receipt })
            if pinned.Project.Launches.Count > 64 then
                add "LaunchListTruncated" snapshot.Commit
                    "Inspect project ledger directly for remaining launch states."

        // Even when this preview can identify a candidate and free capacity,
        // no executable launch ID/one-shot operation is fabricated.
        add "DispatchDisabled" (projectHead |> Option.defaultValue "(project head unavailable)")
            "Production canDispatch remains OFF. Operator must separately approve cutover."
        { Mode = "ReadOnlyDryRun"
          CanDispatch = false
          AccountHead = accountHead
          AccountMigration = migration
          MaxConcurrentAgents = cap
          OccupiedAgents = occupied
          AvailableSlots = available
          ProjectHead = projectHead
          ProjectVersion = projectVersion
          RoleCatalogRevision = roleRevision
          RoleId = roleId
          SelectedRoleVersion = roleVersion
          EligibleWork = candidates
          SkippedWork = skipped
          WouldLaunch = selected
          ExistingLaunches = launches
          Issues = List.ofSeq issues }

    let realReadPorts (account: AccountBinding) (project: LedgerBinding) =
        { ReadAccount = fun () ->
              AccountCapacityLedger.fetch account |> Result.mapError (sprintf "%A")
          ReadProject = fun () ->
              LedgerStore.fetch project
              |> Result.bind (fun snapshot ->
                  WorkQueueRoleAuthority.loadAt project snapshot
                  |> Result.map (fun pinned -> snapshot, pinned))
              |> Result.mapError (sprintf "%A")
          ReadOrigin = fun snapshot workId ->
              WorkQueueGitLedger.readOriginAt project snapshot workId
              |> Result.mapError (sprintf "%A")
          ReadAdmission = fun snapshot launchId ->
              WorkQueueGitLedger.readAdmissionAt project snapshot launchId
              |> Result.mapError (sprintf "%A")
          ReadOriginal = fun _ ->
              Error "WQ03 original browser one-shot journal has no approved read adapter yet." }

    let preview (account: AccountBinding) (project: LedgerBinding) roleId =
        previewWith (realReadPorts account project) roleId

    [<CLIMutable>]
    type AccountPreviewBindingFile =
        { RepositoryPath: string
          RemoteName: string
          ApprovedFetchUrl: string
          ApprovedPushUrl: string
          AccountId: string
          LedgerId: string
          Epoch: int }

    let private accountBinding (path: string) : Result<AccountBinding, string> =
        try
            let record =
                JsonSerializer.Deserialize<AccountPreviewBindingFile>(
                    File.ReadAllText path, JsonSerializerOptions(PropertyNameCaseInsensitive = true))
            if isNull (box record) then Error "Empty account binding JSON."
            else
                let validAccount, accountId = Guid.TryParse record.AccountId
                let validLedger, ledgerId = Guid.TryParse record.LedgerId
                if not validAccount || not validLedger || accountId = Guid.Empty
                   || ledgerId = Guid.Empty || record.Epoch < 1 then
                    Error "Account binding must specify nonempty account/ledger UUIDs and positive epoch."
                else
                    Ok { RepositoryPath = record.RepositoryPath
                         RemoteName = record.RemoteName
                         ApprovedFetchUrl = record.ApprovedFetchUrl
                         ApprovedPushUrl = record.ApprovedPushUrl
                         AccountId = accountId
                         LedgerId = ledgerId
                         Epoch = record.Epoch }
        with ex -> Error ex.Message

    let run argv =
        match argv with
        | [ "scheduler"; "preview"; root; accountFile; roleId ]
            when not (String.IsNullOrWhiteSpace roleId) ->
            try
                match LedgerBindingConfig.loadAtRootWithSnapshotUsing
                          LedgerStore.fetch (Path.GetFullPath root) with
                | Error error ->
                    eprintfn "Project trust binding unavailable: %A" error
                    2
                | Ok (project, _) ->
                    match accountBinding accountFile with
                    | Error error -> eprintfn "Account binding unavailable: %s" error; 2
                    | Ok account ->
                        let report = preview account project roleId
                        printfn "%s" (JsonSerializer.Serialize(
                            report, JsonSerializerOptions(WriteIndented = true)))
                        if report.AccountHead.IsNone || report.ProjectHead.IsNone then 3 else 0
            with ex ->
                eprintfn "Scheduler dry-run failed: %s" ex.Message
                2
        | _ ->
            eprintfn "Usage: JarvisClient scheduler preview PROJECT_ROOT ACCOUNT_BINDING_JSON ROLE_ID"
            eprintfn "Read-only, one-project preview; no reservations, browser submits or scheduler activation."
            2
