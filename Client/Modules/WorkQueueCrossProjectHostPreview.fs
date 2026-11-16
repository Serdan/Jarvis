module Client.WorkQueueCrossProjectHostPreview

open System
open System.IO
open System.Text.Json
open Jarvis.WorkQueues
open Jarvis.WorkQueues.WorkQueueProducers
open Client.LedgerStore
open Client.WorkQueueGitLedger
open Client.WorkQueueRoleAuthority
open Client.AccountCapacityLedger
open Client.AccountAdmissionAdapter
open Client.OriginalBrowserJournal
open Client.OrchestrationApi
open Client.WorkQueueCrossProjectPlanner

// WQ08 host seam: the caller explicitly supplies the entire approved project
// set. All ports below READ previously verified authority; no source or account
// CAS writer, producer, browser submitter, timer, or launcher is reachable.
type CrossProjectHostProjectPorts =
    { Label: string
      ProjectAuthoritySha256: string
      ReadProject: unit -> Result<LedgerSnapshot * PinnedRoleView, string>
      ReadOrigin: LedgerSnapshot -> string -> Result<(CanonicalWorkOrigin * string) option, string>
      ReadOriginal: string -> Result<OriginalBrowserJournalEntry option, string> }

type CrossProjectHostPorts =
    { CompleteProjectSet: bool
      ReadAccount: unit -> Result<AccountSnapshot, string>
      Projects: CrossProjectHostProjectPorts list }

// This is a demand projection, not a PlannerCandidate launch intent. In
// particular it contains NO generated launch or browser operation IDs.
type CrossProjectProjectedDemand =
    { ProjectId: string
      ProjectHead: string
      ProjectVersion: int
      CatalogBlobOid: string
      RoleId: string
      RoleVersion: int
      WorkId: string
      WorkGeneration: int
      QueueId: string
      BlocksOtherWork: bool
      AvailableAt: int64
      AvailableSequence: int64
      SourceHead: string
      SourceOperationId: string
      SourceRequestHash: string
      SourceRef: string option
      SourceCommit: string option }

type CrossProjectObservedLaunch =
    { LaunchId: string
      ProjectId: string
      RoleId: string
      RoleVersion: int
      Phase: string
      AccountAdmissionOperationId: string
      OriginalBrowserOperationId: string option
      OriginalBrowserStatus: string }

type CrossProjectPinnedProject =
    { ProjectId: string
      Label: string
      Head: string option
      ProjectVersion: int option
      CatalogRevision: int option
      CatalogBlobOid: string option
      CoverageComplete: bool
      ObservedLaunches: CrossProjectObservedLaunch list }

type CrossProjectHostReport =
    { Mode: string
      CanDispatch: bool
      ProjectSetComplete: bool
      AccountHead: string option
      MaxConcurrentAgents: int option
      OccupiedAgents: int option
      AvailableSlots: int option
      SimulatedWinner: CrossProjectProjectedDemand option
      Projects: CrossProjectPinnedProject list
      Planner: PlannerReport
      Issues: PlannerIssue list }

module WorkQueueCrossProjectHostPreview =
    let private nonBlank (value: string) = not (String.IsNullOrWhiteSpace value)

    // WQ04's immutable CreateTask operation can originate planning Work without
    // a source branch. This is only a read-only projection; source-writing Work
    // still requires approved immutable source lineage, and any binder must
    // independently recheck the original Git operation and origin blob.
    let private projectedSourceEligible (work: WorkStage) (origin: CanonicalWorkOrigin) =
        if work.Kind = WorkKind.Planning then
            work.QueueId = "planning"
            && origin.SourceOperationKind = "CreateTask"
            && String.IsNullOrWhiteSpace origin.SourceRef
            && String.IsNullOrWhiteSpace origin.SourceCommit
            && origin.SourceOperationSequence > 0L
            && origin.SourceHighWater >= origin.SourceOperationSequence
            && nonBlank origin.SourceOriginHead
            && nonBlank origin.SourceEntityPath
            && nonBlank origin.SourceEntityBlobOid
            && nonBlank origin.SourceEntitySha256
            && origin.AccountCreditStatus = "NotReserved"
            && String.IsNullOrWhiteSpace origin.RoleId
            && origin.RoleVersion = 0
        else
            nonBlank origin.SourceRef && nonBlank origin.SourceCommit

    let private issue code record action : PlannerIssue =
        { Code = code; Record = record; NextAction = action }

    let private phaseName = function
        | LaunchState.ReservedUnregistered -> "ReservedUnregistered"
        | LaunchState.SubmittedUnregistered _ -> "SubmittedUnregistered"
        | LaunchState.Registered _ -> "Registered"
        | LaunchState.DemandInvalidatedPendingTerminal _ -> "DemandInvalidatedPendingTerminal"
        | LaunchState.TerminalPendingRelease _ -> "TerminalPendingRelease"

    let private phase = function
        | LaunchState.ReservedUnregistered -> PlannerLaunchPhase.ReservedUnregistered
        | LaunchState.SubmittedUnregistered _ -> PlannerLaunchPhase.SubmittedUnregistered
        | LaunchState.Registered _ -> PlannerLaunchPhase.Registered
        | LaunchState.DemandInvalidatedPendingTerminal _ -> PlannerLaunchPhase.DemandInvalidatedPendingTerminal
        | LaunchState.TerminalPendingRelease _ -> PlannerLaunchPhase.TerminalPendingRelease

    let private projected (candidate: PlannerCandidate) : CrossProjectProjectedDemand =
        { ProjectId = candidate.ProjectId
          ProjectHead = candidate.ProjectHead
          ProjectVersion = candidate.ProjectVersion
          CatalogBlobOid = candidate.CatalogBlobOid
          RoleId = candidate.RoleId
          RoleVersion = candidate.RoleVersion
          WorkId = candidate.WorkId
          WorkGeneration = candidate.WorkGeneration
          QueueId = candidate.QueueId
          BlocksOtherWork = candidate.BlocksOtherWork
          AvailableAt = candidate.AvailableAt
          AvailableSequence = candidate.AvailableSequence
          SourceHead = candidate.Source.Head
          SourceOperationId = candidate.Source.OperationId
          SourceRequestHash = candidate.Source.RequestHash
          SourceRef = candidate.Source.SourceRef
          SourceCommit = candidate.Source.SourceCommit }

    let previewWith (ports: CrossProjectHostPorts) : CrossProjectHostReport =
        let issues = ResizeArray<PlannerIssue>()
        let add code record action = issues.Add(issue code record action)
        let projects = ResizeArray<PlannerProject>()
        let pinnedProjects = ResizeArray<CrossProjectPinnedProject>()
        let roles = ResizeArray<PlannerRole>()
        let candidates = ResizeArray<PlannerCandidate>()
        let existing = ResizeArray<PlannerExistingLaunch>()
        let mutable projectionGap = false

        let account =
            try ports.ReadAccount()
            with ex -> Error ex.Message
        match account with
        | Error error ->
            projectionGap <- true
            add "AccountReadUnknown" "Account capacity"
                ("Inspect approved account Git authority: " + error)
        | Ok snapshot ->
            if not (isNull (box snapshot.State))
               && not (isNull snapshot.State.Tokens) then
                let held = snapshot.State.Tokens
                           |> Array.filter (fun token -> token.Status <> "Released")
                           |> Array.length
                if snapshot.State.MaxConcurrentAgents > 0
                   && held >= snapshot.State.MaxConcurrentAgents then
                    add "CapacityFull" snapshot.Head
                        "Account-wide cap is full; wait for verified terminal release before any further launch."

        if List.length ports.Projects < 2 then
            projectionGap <- true
            add "InsufficientProjects" "Project set"
                "Provide two or more independently approved project trust bindings."
        if not ports.CompleteProjectSet then
            projectionGap <- true
            add "ProjectSetUnconfirmed" "Project set"
                "Explicitly attest the approved account/project set is complete; no global selection is authoritative otherwise."

        for port in ports.Projects do
            let data =
                try port.ReadProject()
                with ex -> Error ex.Message
            match data with
            | Error error ->
                projectionGap <- true
                add "ProjectReadUnknown" port.Label
                    ("Inspect original approved project binding and verified Git head: " + error)
                pinnedProjects.Add
                    { ProjectId = ""
                      Label = port.Label
                      Head = None
                      ProjectVersion = None
                      CatalogRevision = None
                      CatalogBlobOid = None
                      CoverageComplete = false
                      ObservedLaunches = [] }
            | Ok (snapshot, pinned) ->
                let proof = pinned.Proof
                let coverage =
                    proof.Head = snapshot.Commit
                    && proof.ProjectVersion = pinned.Project.Version
                    && proof.CatalogRevision = pinned.Catalog.Revision
                    && pinned.Project.QueueCovered
                    && pinned.Project.GraphCovered
                    && pinned.Project.SourceCovered
                    && pinned.Catalog.Covered
                    && nonBlank port.ProjectAuthoritySha256
                if not coverage then
                    projectionGap <- true
                    add "ProjectCoverageUnknown" proof.ProjectId
                        "Re-pin project, source, queue and WQ04 catalog at one independently verified Git head."

                let plannerProject =
                    { ProjectId = proof.ProjectId
                      LedgerId = proof.LedgerId
                      LedgerEpoch = proof.Epoch
                      AuthoritySha256 = port.ProjectAuthoritySha256
                      Head = snapshot.Commit
                      Version = pinned.Project.Version
                      CatalogRevision = pinned.Catalog.Revision
                      CatalogBlobOid = proof.CatalogBlobOid
                      CoverageComplete = coverage }
                projects.Add plannerProject

                for KeyValue(roleId, roleVersion) in pinned.Catalog.Current do
                    match pinned.Catalog.Versions.TryFind roleId
                          |> Option.bind (fun history -> history.TryFind roleVersion) with
                    | None ->
                        projectionGap <- true
                        add "PinnedRoleUnknown" (proof.ProjectId + "/" + roleId)
                            "Read the exact current WQ04 role version from its pinned catalog."
                    | Some role ->
                        roles.Add
                            { ProjectId = proof.ProjectId
                              ProjectHead = snapshot.Commit
                              CatalogBlobOid = proof.CatalogBlobOid
                              RoleId = role.RoleId
                              Version = role.Version
                              Enabled = role.Enabled
                              Queues = role.QueueSubscriptions }
                        if role.Enabled && role.RoleId <> "Manager" then
                            let demand =
                                try selectRoleDemandPractical pinned.Catalog pinned.Project roleId
                                with ex -> Error(Rejection.IncompleteCoverage ex.Message)
                            match demand with
                            | Error failure ->
                                projectionGap <- true
                                add "DemandUnknown" (proof.ProjectId + "/" + roleId)
                                    ("Inspect pinned WQ04 queue and graph: " + string failure)
                            | Ok selected ->
                                match selected.Chosen with
                                | None -> ()
                                | Some chosen ->
                                    let source =
                                        try port.ReadOrigin snapshot chosen.WorkId
                                        with ex -> Error ex.Message
                                    let origin =
                                        match source with
                                        | Ok(Some(value, _))
                                            when value.SchemaVersion = 1
                                                 && value.ProjectId = proof.ProjectId
                                                 && value.WorkId = chosen.WorkId
                                                 && nonBlank value.SourceHead
                                                 && nonBlank value.SourceOperationId
                                                 && nonBlank value.SourceRequestHash
                                                 && (pinned.Project.Works.TryFind chosen.WorkId
                                                     |> Option.exists (fun stage ->
                                                         projectedSourceEligible stage value)) ->
                                            { Head = value.SourceHead
                                              OperationId = value.SourceOperationId
                                              RequestHash = value.SourceRequestHash
                                              SourceRef = if nonBlank value.SourceRef then Some value.SourceRef else None
                                              SourceCommit = if nonBlank value.SourceCommit then Some value.SourceCommit else None }
                                        | _ ->
                                            projectionGap <- true
                                            let reason =
                                                match source with
                                                | Error error -> error
                                                | Ok None -> "Immutable WQ04 origin missing"
                                                | Ok(Some _) -> "Origin identity, original request or source lineage is inconsistent"
                                            add "SourceLineageUnknown" (proof.ProjectId + "/" + chosen.WorkId)
                                                ("Inspect original WQ04 source receipt at pinned head " + snapshot.Commit + ": " + reason)
                                            { Head = ""; OperationId = ""; RequestHash = ""
                                              SourceRef = None; SourceCommit = None }
                                    candidates.Add
                                        { ProjectId = proof.ProjectId
                                          ProjectHead = snapshot.Commit
                                          ProjectVersion = pinned.Project.Version
                                          WorkId = chosen.WorkId
                                          WorkGeneration = chosen.StageGeneration
                                          QueueId = chosen.QueueId
                                          AvailableAt = chosen.AvailableAt
                                          AvailableSequence = chosen.AvailableSequence
                                          BlocksOtherWork = chosen.BlocksUnfinishedStage
                                          RoleId = chosen.RoleId
                                          RoleVersion = chosen.RoleVersion
                                          CatalogBlobOid = proof.CatalogBlobOid
                                          Source = origin
                                          // No actual pre-send intent exists for projected new work.
                                          // The strict WQ08 planner must not mistake these for IDs.
                                          LaunchId = ""
                                          OriginalBrowserOperationId = "" }

                let launches = ResizeArray<CrossProjectObservedLaunch>()
                for KeyValue(_, launch) in pinned.Project.Launches do
                    let original =
                        try port.ReadOriginal launch.Id
                        with ex -> Error ex.Message
                    let originalId, status, evidence =
                        match original with
                        | Error error ->
                            add "OriginalBrowserReadUnknown" (proof.ProjectId + "/" + launch.Id)
                                ("Inspect original WQ03 journal without resending: " + error)
                            None, "Unknown", PlannerBrowserEvidence.Unknown error
                        | Ok None ->
                            add "OriginalBrowserMissing" (proof.ProjectId + "/" + launch.Id)
                                "Locate the original WQ03 journal marker; absence is not definitive no effect."
                            None, "NotRecorded", PlannerBrowserEvidence.NotRecorded
                        | Ok(Some entry) when entry.Intent.LaunchId = launch.Id
                                             && entry.Intent.ProjectId = proof.ProjectId
                                             && entry.Intent.RoleId = launch.RoleId
                                             && entry.Intent.RoleVersion = launch.RoleVersion ->
                            let status, evidence =
                                match entry.Outcome with
                                | None -> "MarkerPresentOutcomeUnknown",
                                          PlannerBrowserEvidence.Unknown "Original marker has no terminal outcome"
                                | Some(OriginalBrowserOutcome.Accepted receipt) ->
                                    "Accepted", PlannerBrowserEvidence.Accepted receipt
                                | Some(OriginalBrowserOutcome.DefinitiveNoEffect reason) ->
                                    "DefinitiveNoEffect", PlannerBrowserEvidence.DefinitiveNoEffect reason
                                | Some(OriginalBrowserOutcome.Unknown reason) ->
                                    "Unknown", PlannerBrowserEvidence.Unknown reason
                            Some entry.Intent.OperationId, status, evidence
                        | Ok(Some entry) ->
                            add "OriginalBrowserIdentityConflict" (proof.ProjectId + "/" + launch.Id)
                                ("Preserve and inspect WQ03 original " + entry.Intent.OperationId + " before any recovery.")
                            Some entry.Intent.OperationId, "IdentityConflict",
                                PlannerBrowserEvidence.Unknown "Original journal identity conflict"

                    launches.Add
                        { LaunchId = launch.Id
                          ProjectId = proof.ProjectId
                          RoleId = launch.RoleId
                          RoleVersion = launch.RoleVersion
                          Phase = phaseName launch.State
                          AccountAdmissionOperationId = launch.AccountTokenId
                          OriginalBrowserOperationId = originalId
                          OriginalBrowserStatus = status }
                    existing.Add
                        { LaunchId = launch.Id
                          ProjectId = proof.ProjectId
                          RoleId = launch.RoleId
                          RoleVersion = launch.RoleVersion
                          AccountAdmissionOperationId = launch.AccountTokenId
                          Phase = phase launch.State
                          // WQ03 exposes the original string separately above.
                          // Its stable UUID mapping is an identity of that string,
                          // not a minted retry/replacement browser operation.
                          OriginalBrowserOperationId =
                              originalId |> Option.map OriginalBrowserJournal.operationUuid
                          OriginalBrowser = evidence }

                pinnedProjects.Add
                    { ProjectId = proof.ProjectId
                      Label = port.Label
                      Head = Some snapshot.Commit
                      ProjectVersion = Some pinned.Project.Version
                      CatalogRevision = Some pinned.Catalog.Revision
                      CatalogBlobOid = Some proof.CatalogBlobOid
                      CoverageComplete = coverage
                      ObservedLaunches = List.ofSeq launches }

        let distinctProjects = projects |> Seq.map _.ProjectId |> Set.ofSeq |> Set.count
        let allProjectsUnique = distinctProjects = projects.Count
        if not allProjectsUnique then
            projectionGap <- true
            add "DuplicateProject" "Project set"
                "Remove duplicate or contradictory project IDs before comparing global demand."

        // Only unsettled account reservations require a currently approved
        // project snapshot. Released tokens are retained audit history and do
        // not imply that a retired project remains in the active project set.
        match account with
        | Ok snapshot ->
            let known = projects |> Seq.map _.ProjectId |> Set.ofSeq
            for token in snapshot.State.Tokens do
                if token.Status <> "Released" && not (known.Contains token.ProjectId) then
                    projectionGap <- true
                    add "AccountTokenProjectMissing" token.LaunchId
                        "Add the original approved project for this account token; do not infer release."
        | Error _ -> ()

        let complete =
            ports.CompleteProjectSet && ports.Projects.Length >= 2
            && allProjectsUnique && projects.Count = ports.Projects.Length
            && not projectionGap

        // Use the accepted WQ08 planner on exactly the real pinned WQ04
        // observations, leaving every prospective launch/browser ID absent.
        // Its identity gate must remain closed. A projected winner is a
        // different, explicitly non-runnable read-only data product.
        let planner =
            WorkQueueCrossProjectPlanner.planFakeOnly
                { ReadAccount = fun () -> account
                  ReadProjects = fun () -> Ok { CompleteProjectSet = complete; Projects = List.ofSeq projects }
                  ReadRoles = fun () -> Ok(List.ofSeq roles)
                  ReadCandidates = fun () -> Ok(List.ofSeq candidates)
                  ReadExistingLaunches = fun () -> Ok(List.ofSeq existing) }
        if candidates.Count > 0 then
            add "NoDurablePreSendIntent" "Projected candidates"
                "WQ04 demand alone has no original launch/browser UUID. This is a simulation, not a runnable reservation or send."

        // The fake-only planner deliberately rejects candidate launch/browser
        // identities absent from this read-only preview. Those issues must not
        // erase a simulated demand winner. Contradictory or incomplete
        // account/project/launch authority makes a global projection invalid.
        let accountAuthorityInconsistent =
            planner.Issues
            |> List.exists (fun value ->
                match value.Code with
                | "AccountTokenIdentityInvalid"
                | "AccountTokenProjectUnknown"
                | "AccountTokenLaunchUnknown"
                | "AccountProjectIdentityMismatch"
                | "AccountTokenStatusUnknown"
                | "DuplicateAccountToken"
                | "LaunchAccountTokenMissing"
                | "LaunchProjectUnknown"
                | "ActiveLaunchTokenReleased"
                | "DuplicateExistingLaunch"
                | "ExistingLaunchIdentityInvalid" -> true
                | _ -> false)
        if accountAuthorityInconsistent then
            add "AccountAuthorityInconsistent" "Global selection"
                "Reconcile original account token, project authority and launch identity issues before trusting a global selection."
        let selectionComplete = complete && not accountAuthorityInconsistent
        let winner =
            if selectionComplete && account.IsOk then
                candidates
                |> Seq.toList
                |> WorkQueueCrossProjectPlanner.rankCandidates
                |> List.tryHead
                |> Option.map projected
            else None
        { Mode = "RealCrossProjectReadOnlyDefaultOff"
          CanDispatch = false
          ProjectSetComplete = selectionComplete
          AccountHead = planner.AccountHead
          MaxConcurrentAgents = planner.MaxConcurrentAgents
          OccupiedAgents = planner.OccupiedAgents
          AvailableSlots =
              match planner.MaxConcurrentAgents, planner.OccupiedAgents with
              | Some limit, Some held -> Some(max 0 (limit - held))
              | _ -> None
          SimulatedWinner = winner
          Projects = pinnedProjects |> Seq.sortBy _.ProjectId |> Seq.toList
          Planner = planner
          Issues =
              issues
              |> Seq.sortBy (fun value -> value.Code, value.Record, value.NextAction)
              |> Seq.toList }

    [<CLIMutable>]
    type ManifestProject =
        { ProjectRoot: string
          OriginalJournalRoot: string }

    [<CLIMutable>]
    type PreviewManifest =
        { CompleteProjectSet: bool
          Projects: ManifestProject array }

    let private realProject (item: ManifestProject) =
        let root = item.ProjectRoot
        let loaded =
            try
                LedgerBindingConfig.loadAtRootWithSnapshotUsing
                    LedgerStore.fetch (Path.GetFullPath root)
                |> Result.mapError (sprintf "%A")
            with ex -> Error ex.Message
        match loaded with
        | Error error ->
            { Label = root
              ProjectAuthoritySha256 = ""
              ReadProject = fun () -> Error error
              ReadOrigin = fun _ _ -> Error error
              ReadOriginal = fun _ -> Error error }
        | Ok (binding, snapshot) ->
            // Missing journal stays default-off. An explicit path can only
            // open an existing anchored client-local journal, never create one.
            let journal =
                if nonBlank item.OriginalJournalRoot then
                    OriginalBrowserJournal.openExisting item.OriginalJournalRoot
                    |> Result.map Some
                else Error "No explicitly approved WQ03 original journal root."
            { Label = root
              ProjectAuthoritySha256 =
                  AccountAdmissionAdapter.projectAuthority binding
                  |> AccountCapacityLedger.projectAuthoritySha256
              ReadProject = fun () ->
                  WorkQueueRoleAuthority.loadAt binding snapshot
                  |> Result.map (fun pinned -> snapshot, pinned)
                  |> Result.mapError (sprintf "%A")
              ReadOrigin = fun snap workId ->
                  WorkQueueGitLedger.readOriginAt binding snap workId
                  |> Result.mapError (sprintf "%A")
              ReadOriginal = fun launchId ->
                  match journal with
                  | Ok(Some store) -> OriginalBrowserJournal.findByLaunch store launchId
                  | Ok None -> Error "No WQ03 original journal root."
                  | Error error -> Error error }

    let run argv =
        match argv with
        | ["scheduler"; "preview-global"; accountFile; manifestFile] ->
            try
                let options = JsonSerializerOptions(PropertyNameCaseInsensitive = true)
                let manifest = JsonSerializer.Deserialize<PreviewManifest>(File.ReadAllText manifestFile, options)
                if isNull (box manifest) || isNull manifest.Projects
                   || manifest.Projects.Length < 2
                   || manifest.Projects |> Array.exists (fun item ->
                       isNull (box item) || not (nonBlank item.ProjectRoot)) then
                    eprintfn "Manifest requires two or more explicitly approved project roots."
                    2
                else
                    match WorkQueueSchedulerHostPreview.WorkQueueSchedulerHostPreview.accountBinding accountFile with
                    | Error error -> eprintfn "Account binding unavailable: %s" error; 2
                    | Ok account ->
                        let ports =
                            { CompleteProjectSet = manifest.CompleteProjectSet
                              ReadAccount = fun () ->
                                  AccountCapacityLedger.fetch account |> Result.mapError (sprintf "%A")
                              Projects = manifest.Projects |> Array.map realProject |> Array.toList }
                        let report = previewWith ports
                        printfn "%s" (JsonSerializer.Serialize(report, JsonSerializerOptions(WriteIndented = true)))
                        if report.ProjectSetComplete && report.AccountHead.IsSome then 0 else 3
            with ex ->
                eprintfn "Cross-project read-only preview failed: %s" ex.Message
                2
        | _ ->
            eprintfn "Usage: JarvisClient scheduler preview-global ACCOUNT_BINDING_JSON PROJECTS_MANIFEST_JSON"
            eprintfn "Read-only only: no account/project CAS, browser submission, reservations or scheduler activation."
            2
