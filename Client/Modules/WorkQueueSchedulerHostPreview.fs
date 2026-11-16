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
open Client.OriginalBrowserJournal

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
      AccountTokenStatus: string
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
// The WQ03 original browser journal is default-off. Only an explicitly opened,
// trusted, client-local store can supply original evidence. Missing journal is
// never proof of no effect.
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
                            | Ok (Some(origin, blob))
                                when origin.ProjectId = pinned.Proof.ProjectId
                                     && origin.WorkId = demand.WorkId
                                     && not (String.IsNullOrWhiteSpace origin.SourceOperationId)
                                     && not (String.IsNullOrWhiteSpace origin.SourceRequestHash) ->
                                Some origin.SourceHead,
                                (if String.IsNullOrWhiteSpace origin.SourceRef then None else Some origin.SourceRef),
                                (if String.IsNullOrWhiteSpace origin.SourceCommit then None else Some origin.SourceCommit),
                                Some origin.SourceOperationId, Some origin.SourceRequestHash, Some blob
                            | Ok (Some(origin, _)) ->
                                add "SourceReceiptIdentityConflict" demand.WorkId
                                    ("WQ04 origin for " + demand.WorkId + " at " + snapshot.Commit
                                     + " names project/work " + origin.ProjectId + "/" + origin.WorkId
                                     + "; inspect immutable source record and original IDs before launch.")
                                None, None, None, None, None, None
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

            let roleLaunches =
                pinned.Project.Launches
                |> Map.toList
                |> List.map snd
                |> List.filter (fun launch -> launch.RoleId = roleId)
            // Historical terminal launches remain in Git. In a bounded
            // preview, their lexical IDs must not hide unresolved sessions.
            // Terminal project state alone is insufficient: only a matching
            // released account token makes it settled history.
            let tokensByLaunch =
                match account with
                | Error _ -> Map.empty
                | Ok snapshot ->
                    snapshot.State.Tokens
                    |> Array.groupBy (fun token -> token.LaunchId)
                    |> Map.ofArray
            let settledHistory (launch: Launch) =
                match launch.State, tokensByLaunch.TryFind launch.Id with
                | LaunchState.TerminalPendingRelease _, Some [| token |] ->
                    token.Status = "Released"
                    && token.ProjectId = launch.ProjectId
                    && token.ProjectLedgerId = pinned.Proof.LedgerId
                    && token.ProjectLedgerEpoch = pinned.Proof.Epoch
                    && token.RoleId = launch.RoleId
                    && token.RoleConfigVersion = int64 launch.RoleVersion
                    && token.AdmissionOperationId = launch.AccountTokenId
                | _ -> false
            let launchPriority launch =
                let settled = if settledHistory launch then 1 else 0
                let stateRank =
                    match launch.State with
                    | LaunchState.SubmittedUnregistered DispatchState.OutcomeUnknown -> 0
                    | LaunchState.DemandInvalidatedPendingTerminal _ -> 1
                    | LaunchState.TerminalPendingRelease _ -> 2
                    | LaunchState.Registered _ -> 3
                    | LaunchState.SubmittedUnregistered _ -> 4
                    | LaunchState.ReservedUnregistered -> 5
                settled, stateRank, launch.Id
            launches <-
                roleLaunches
                |> List.sortBy launchPriority
                |> List.truncate 64
                |> List.map (fun launch ->
                    let accountTokenStatus =
                        match account with
                        | Error _ -> "Unknown"
                        | Ok snapshot ->
                            match tokensByLaunch.TryFind launch.Id with
                            | None ->
                                add "AccountTokenMissing" launch.Id
                                    ("No account-global token for this project launch at account head "
                                     + snapshot.Head + "; inspect original reservation before release or retry.")
                                "Missing"
                            | Some [| token |] when token.ProjectId = launch.ProjectId
                                               && token.ProjectLedgerId = pinned.Proof.LedgerId
                                               && token.ProjectLedgerEpoch = pinned.Proof.Epoch
                                               && token.RoleId = launch.RoleId
                                               && token.RoleConfigVersion = int64 launch.RoleVersion
                                               && token.AdmissionOperationId = launch.AccountTokenId ->
                                match launch.State, token.Status with
                                | LaunchState.ReservedUnregistered, "Released"
                                | LaunchState.SubmittedUnregistered _, "Released"
                                | LaunchState.Registered _, "Released"
                                | LaunchState.DemandInvalidatedPendingTerminal _, "Released" ->
                                    add "AccountTokenReleasedWhileActive" launch.Id
                                        "Project launch is not terminal but account capacity was released; inspect original heads and receipts."
                                | _ -> ()
                                token.Status
                            | Some [| _ |] ->
                                add "AccountTokenIdentityConflict" launch.Id
                                    "Account token project/ledger, role/version or admission ID differs; preserve both original records."
                                "IdentityConflict"
                            | Some _ ->
                                add "AccountTokenDuplicate" launch.Id
                                    "Multiple account-global tokens claim this launch ID; inspect original account Git records."
                                "Duplicate"
                    let admission =
                        try ports.ReadAdmission snapshot launch.Id
                        with ex -> Error ex.Message
                    let admissionId, kind =
                        match admission with
                        | Ok(Some(record, _))
                            when record.ProjectId = launch.ProjectId
                                 && record.LaunchId = launch.Id
                                 && record.AccountTokenId = launch.AccountTokenId ->
                            Some record.AdmissionOperationId, Some record.Kind
                        | Ok(Some(record, _)) ->
                            add "AdmissionReceiptIdentityConflict" launch.Id
                                ("Stored admission witness names project/launch "
                                 + record.ProjectId + "/" + record.LaunchId
                                 + " and token " + record.AccountTokenId
                                 + "; preserve original admission " + record.AdmissionOperationId
                                 + " and inspect Git witness before reconciliation.")
                            Some record.AdmissionOperationId, Some("IdentityConflict: " + record.Kind)
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
                        | Ok(Some entry) when entry.Intent.LaunchId = launch.Id
                                              && entry.Intent.ProjectId = launch.ProjectId
                                              && entry.Intent.RoleId = launch.RoleId
                                              && entry.Intent.RoleVersion = launch.RoleVersion ->
                            let status, receipt = browserOutcome entry.Outcome
                            Some entry.Intent.OperationId, status, receipt
                        | Ok(Some entry) ->
                            add "BrowserIdentityConflict" launch.Id
                                ("Original browser operation " + entry.Intent.OperationId
                                 + " names project/launch " + entry.Intent.ProjectId + "/" + entry.Intent.LaunchId
                                 + " and role/version " + entry.Intent.RoleId + "/" + string entry.Intent.RoleVersion
                                 + "; expected " + launch.ProjectId + "/" + launch.Id
                                 + " and " + launch.RoleId + "/" + string launch.RoleVersion
                                 + ". Preserve its journal and inspect the mismatched identity before retry.")
                            Some entry.Intent.OperationId, "IdentityConflict", None
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
                      AccountTokenStatus = accountTokenStatus
                      AdmissionOperationId = admissionId
                      AdmissionKind = kind
                      OriginalBrowserOperationId = browserId
                      OriginalBrowserOutcome = outcome
                      OriginalBrowserReceipt = receipt })
            if roleLaunches.Length > 64 then
                add "LaunchListTruncated" snapshot.Commit
                    ("Showing 64 of " + string roleLaunches.Length
                     + " launches for this role (unresolved/active first). "
                     + "Inspect the full project ledger for any omitted launches, including unresolved overflow.")

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

    let realReadPortsWithOriginal (account: AccountBinding) (project: LedgerBinding)
                                  (original: OriginalBrowserJournalStore option) =
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
          ReadOriginal = fun launchId ->
              match original with
              | None -> Error "WQ03 original browser journal binding is not provisioned or approved."
              | Some store -> OriginalBrowserJournal.findByLaunch store launchId }

    // Existing callers, including the CLI dry-run, stay fail-closed until the
    // operator explicitly supplies an opened local journal binding.
    let realReadPorts (account: AccountBinding) (project: LedgerBinding) =
        realReadPortsWithOriginal account project None

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

    let accountBinding (path: string) : Result<AccountBinding, string> =
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
