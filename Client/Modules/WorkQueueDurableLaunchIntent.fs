module Client.WorkQueueDurableLaunchIntent

open System
open System.Security.Cryptography
open System.Text
open FsToolkit.ErrorHandling
open Jarvis.WorkQueues
open Jarvis.WorkQueues.WorkQueueProducers
open Client.LedgerStore
open Client.WorkQueueGitLedger
open Client.WorkQueueRoleAuthority
open Client.WorkQueueCrossProjectHostPreview
open Client.WorkQueueCrossProjectPlanner
open Client.WorkCompositionGitLedger
open Client.AccountCapacityLedger
open Client.AccountAdmissionAdapter
open Client.WorkQueuePracticalAdmission
open Client.OriginalBrowserJournal

// A default-off preparation seam, NOT a scheduler permission or browser send.
// Only the winning project Git CAS owns these UUIDs. WQ07 independently owns
// global capacity, and the existing WQ04 admission rechecks the original Work.
type DurablePlanningLaunchIntent =
    { SchemaVersion: int
      AccountId: string
      AccountLedgerId: string
      AccountEpoch: int
      AccountHead: string
      ProjectId: string
      ProjectLedgerId: string
      ProjectEpoch: int
      ProjectAuthoritySha256: string
      ProjectHead: string
      ProjectVersion: int
      CatalogBlobOid: string
      RoleId: string
      RoleVersion: int
      WorkId: string
      WorkGeneration: int
      OriginBlobOid: string
      SourceHead: string
      SourceOperationId: string
      SourceRequestHash: string
      SourceOriginHead: string
      SourceOperationKind: string
      SourceRef: string
      SourceCommit: string
      LaunchId: string
      PrepareOperationId: string
      OriginalBrowserOperationId: string
      AdmissionOperationId: string
      AdmissionActor: string
      AbortOperationId: string
      ReleaseOperationId: string
      DispatchOperationId: string
      FirstConnectOperationId: string }

type DurableIntentProject =
    { Binding: LedgerBinding
      OriginalJournal: OriginalBrowserJournalStore option }

type DurableIntentContext =
    { Account: AccountBinding
      CompleteProjectSet: bool
      Projects: DurableIntentProject list }

[<RequireQualifiedAccess>]
type DurableIntentOutcome =
    | Disabled
    | Prepared of intent: DurablePlanningLaunchIntent * canonicalHead: string * alreadyRecorded: bool
    | NeedsAttention of reason: string
    | Unknown of reason: string

module WorkQueueDurableLaunchIntent =
    let private explain error = sprintf "%A" error
    let private nonBlank (value: string) = not (String.IsNullOrWhiteSpace value)
    let private codec = CanonicalCodec.encode
    let private invalid reason = InvalidBinding("WQ12 durable planning intent: " + reason)

    let private accountId (b: AccountBinding) = b.AccountId.ToString("D")
    let private projectId (b: LedgerBinding) = b.ProjectId.ToString("D")

    // Path is keyed by the canonical work generation, not a transient client
    // actor or chosen role. Competing roles/clients cannot mint another intent
    // for the same Work generation by changing the label or operation UUID.
    let intentPath (projectId: string) (workId: string) generation =
        let key = projectId + "/" + workId + "/" + string generation
        "records/work-queue/launch-intents/" + CanonicalCodec.digest key + ".json"

    let private parseUuid (value: string) =
        match Guid.TryParse value with
        | true, id when id <> Guid.Empty -> Ok id
        | _ -> Error(invalid "Canonical intent contains an invalid UUID.")

    let private allDistinct (value: DurablePlanningLaunchIntent) =
        [value.PrepareOperationId; value.LaunchId; value.OriginalBrowserOperationId; value.AdmissionOperationId
         value.AbortOperationId; value.ReleaseOperationId
         value.DispatchOperationId; value.FirstConnectOperationId]
        |> List.map Guid.TryParse
        |> List.forall (function true, id when id <> Guid.Empty -> true | _ -> false)
        &&
        ([value.PrepareOperationId; value.LaunchId; value.OriginalBrowserOperationId; value.AdmissionOperationId
          value.AbortOperationId; value.ReleaseOperationId
          value.DispatchOperationId; value.FirstConnectOperationId]
         |> List.map (fun id -> Guid.Parse id)
         |> Set.ofList |> Set.count) = 8

    let private readAt (binding: LedgerBinding) (snapshot: LedgerSnapshot)
                       workId generation = result {
        let path = intentPath (projectId binding) workId generation
        let! raw, blobOid = LedgerStore.readAt binding snapshot path
        match raw, blobOid with
        | None, None -> return None
        | Some content, Some currentOid ->
            let parsed =
                try
                    let intent = CanonicalCodec.decode<DurablePlanningLaunchIntent> content
                    if intent.SchemaVersion <> 2
                       || intent.ProjectId <> projectId binding
                       || intent.ProjectLedgerId <> binding.LedgerId.ToString("D")
                       || intent.ProjectEpoch <> binding.Epoch
                       || intent.WorkId <> workId
                       || intent.WorkGeneration <> generation
                       || intent.AdmissionActor <> "WQ12.Prepared:" + intent.PrepareOperationId
                       || codec intent <> content
                       || not (allDistinct intent) then
                        Error(invalid "Stored intent identity, canonical bytes or UUIDs conflict.")
                    else Ok intent
                with ex ->
                    Error(invalid ("Stored canonical launch intent is unreadable: " + ex.Message))
            let! intent = parsed
            let! prepareId = parseUuid intent.PrepareOperationId
            let! original = LedgerStore.getOperationAt binding snapshot prepareId
            match original with
            | Some operation when operation.OperationId = intent.PrepareOperationId
                                  && operation.Type = "WQ12.PreparePlanningLaunch"
                                  && operation.Paths = [|path|]
                                  && nonBlank operation.Actor ->
                let! originHead = LedgerStore.getOperationOriginAt binding snapshot operation
                let! originalHead = LedgerStore.verifiedHistoricalSnapshot binding originHead
                let! originalBody, originalOid = LedgerStore.readAt binding originalHead path
                if originalBody <> Some content || originalOid <> Some currentOid then
                    return! Error(invalid "Canonical intent has been replaced after its original Git operation.")
                return Some intent
            | _ ->
                return! Error(invalid "Canonical intent lacks its exact immutable creating Git operation.")
        | _ -> return! Error(invalid "Canonical intent record is partially missing.")
    }

    let private sameContext (context: DurableIntentContext)
                            (binding: LedgerBinding)
                            (intent: DurablePlanningLaunchIntent) =
        intent.AccountId = accountId context.Account
        && intent.AccountLedgerId = context.Account.LedgerId.ToString("D")
        && intent.AccountEpoch = context.Account.Epoch
        && intent.ProjectId = projectId binding
        && intent.ProjectLedgerId = binding.LedgerId.ToString("D")
        && intent.ProjectEpoch = binding.Epoch
        && intent.ProjectAuthoritySha256 =
            (AccountAdmissionAdapter.projectAuthority binding
             |> AccountCapacityLedger.projectAuthoritySha256)

    // Verifies actual original WQ04 source operation, immutable origin OID,
    // historical Git-origin commit and entity content. Project source-ref
    // absence is only allowed for CreateTask -> Planning, never implementations.
    let private verifiedPlanning (binding: LedgerBinding) (snapshot: LedgerSnapshot)
                                (selected: CrossProjectProjectedDemand) = result {
        let! view = WorkQueueRoleAuthority.loadAt binding snapshot
        if not (view.Proof.ProjectId.Equals(selected.ProjectId, StringComparison.Ordinal))
           || not (view.Proof.Head.Equals(snapshot.Commit, StringComparison.Ordinal))
           || view.Proof.CatalogBlobOid <> selected.CatalogBlobOid
           || view.Project.Version <> selected.ProjectVersion
           || snapshot.Commit <> selected.ProjectHead then
            return! Error(invalid "Pinned project/head/catalog version changed.")
        let! stage =
            match view.Project.Works.TryFind selected.WorkId with
            | Some stage when stage.Kind = WorkKind.Planning
                              && stage.QueueId = "planning"
                              && stage.State = WorkState.Available
                              && stage.StageGeneration = selected.WorkGeneration
                              && stage.AvailableAt = selected.AvailableAt
                              && stage.AvailableSequence = selected.AvailableSequence
                              && stage.QueueId = selected.QueueId -> Ok stage
            | _ -> Error(invalid "Work changed generation, queue, eligibility or is source-writing.")
        let! ranking =
            selectRoleDemandPractical view.Catalog view.Project selected.RoleId
            |> Result.mapError (fun error -> invalid ("Pinned role selection is unknown: " + explain error))
        if not (ranking.Chosen |> Option.exists (fun chosen ->
            chosen.WorkId = stage.Id
            && chosen.RoleId = selected.RoleId
            && chosen.RoleVersion = selected.RoleVersion)) then
            return! Error(invalid "Current pinned WQ04 role selects a different Work generation.")
        if not stage.Dependencies.IsEmpty
           || not stage.WaivedDependencies.IsEmpty
           || stage.Gate <> GateState.Clear then
            return! Error(invalid "Hard stage dependency, waiver or gate needs explicit reconciliation.")
        let! composition, _ = WorkCompositionGitLedger.loadAt binding snapshot
        if composition.Units |> Map.exists (fun _ unit ->
            unit.Spec.LinkedTaskId = Some stage.SubjectId) then
            return! Error(invalid
                "Task-linked composition requires the separately accepted atomic composition verifier.")
        let! originFound = WorkQueueGitLedger.readOriginAt binding snapshot stage.Id
        let! origin, originOid =
            match originFound with
            | Some (origin, oid) -> Ok(origin, oid)
            | None -> Error(invalid "Immutable original WQ04 work origin is missing.")
        if origin.SchemaVersion <> 1
           || origin.ProjectId <> selected.ProjectId
           || origin.WorkId <> stage.Id
           || origin.SourceOperationKind <> "CreateTask"
           || origin.AccountCreditStatus <> "NotReserved"
           || nonBlank origin.RoleId || origin.RoleVersion <> 0
           || nonBlank origin.SourceRef || nonBlank origin.SourceCommit
           || origin.SourceOperationId <> selected.SourceOperationId
           || origin.SourceRequestHash <> selected.SourceRequestHash
           || origin.SourceHead <> selected.SourceHead
           || not (nonBlank origin.SourceOriginHead)
           || not (nonBlank origin.SourceEntityBlobOid)
           || not (nonBlank origin.SourceEntitySha256)
           || origin.SourceHighWater >= snapshot.Header.NextSequence
           || origin.SourceOperationSequence < 1L then
            return! Error(invalid "Original task provenance or source-writing classification disagrees.")
        let! sourceId = parseUuid origin.SourceOperationId
        let! operation = LedgerStore.getOperationAt binding snapshot sourceId
        let! sourceOperation =
            match operation with
            | Some operation when operation.OperationId = origin.SourceOperationId
                                  && operation.Type = "CreateTask"
                                  && operation.RequestHash = origin.SourceRequestHash
                                  && operation.Actor = origin.SourceOperationActor
                                  && operation.Sequence = origin.SourceOperationSequence -> Ok operation
            | _ -> Error(invalid "Original task operation UUID, request hash or sequence is missing.")
        let! historicalHead = LedgerStore.getOperationOriginAt binding snapshot sourceOperation
        if historicalHead <> origin.SourceOriginHead then
            return! Error(invalid "Original task entity moved from its immutable Git commit.")
        let! historical = LedgerStore.verifiedHistoricalSnapshot binding historicalHead
        let! raw, oid = LedgerStore.readAt binding historical origin.SourceEntityPath
        match raw, oid with
        | Some body, Some blob when blob = origin.SourceEntityBlobOid
                                 && (SHA256.HashData(Encoding.UTF8.GetBytes body)
                                     |> Convert.ToHexString
                                     |> fun s -> s.ToLowerInvariant()) = origin.SourceEntitySha256 -> ()
        | _ -> return! Error(invalid "Original entity bytes do not match the pinned WQ04 origin.")
        return origin, originOid
    }

    let private hostPreview (context: DurableIntentContext) =
        WorkQueueCrossProjectHostPreview.previewWith
            { CompleteProjectSet = context.CompleteProjectSet
              ReadAccount = fun () ->
                  AccountCapacityLedger.fetch context.Account |> Result.mapError explain
              Projects =
                  [ for item in context.Projects do
                      let binding = item.Binding
                      yield
                        { Label = projectId binding
                          ProjectAuthoritySha256 =
                              AccountAdmissionAdapter.projectAuthority binding
                              |> AccountCapacityLedger.projectAuthoritySha256
                          ReadProject = fun () ->
                              LedgerStore.fetch binding
                              |> Result.bind (fun snapshot ->
                                  WorkQueueRoleAuthority.loadAt binding snapshot
                                  |> Result.map (fun view -> snapshot, view))
                              |> Result.mapError explain
                          ReadOrigin = fun snapshot workId ->
                              WorkQueueGitLedger.readOriginAt binding snapshot workId
                              |> Result.mapError explain
                          ReadOriginal = fun launchId ->
                              match item.OriginalJournal with
                              | Some journal -> OriginalBrowserJournal.findByLaunch journal launchId
                              | None -> Error "WQ03 original journal is not configured for an existing launch." } ] }

    let private freshSelection (context: DurableIntentContext) = result {
        let bindings = context.Projects |> List.map _.Binding
        if not context.CompleteProjectSet || bindings.Length < 2
           || bindings |> List.exists (fun b -> b.ProjectId = Guid.Empty || b.LedgerId = Guid.Empty)
           || (bindings |> List.map _.ProjectId |> Set.ofList |> Set.count) <> bindings.Length then
            return! Error(invalid "Account project membership is incomplete, duplicate or untrusted.")
        let report = hostPreview context
        if report.Mode <> "RealCrossProjectReadOnlyDefaultOff" || report.CanDispatch
           || not report.ProjectSetComplete then
            return! Error(invalid "Complete verified cross-project authority is unavailable.")
        let unexpectedIssue =
            report.Issues
            |> List.tryFind (fun i -> i.Code <> "NoDurablePreSendIntent")
        match unexpectedIssue with
        | Some issue ->
            return! Error(invalid ("Cross-project issue " + issue.Code + ": " + issue.NextAction))
        | None -> ()
        let unexpectedPlannerIssue =
            report.Planner.Issues
            |> List.tryFind (fun i ->
                i.Code <> "CandidateIdentityInvalid"
                && i.Code <> "AccountNotActivated")
        match unexpectedPlannerIssue with
        | Some issue ->
            return! Error(invalid ("WQ08 planner issue " + issue.Code + ": " + issue.NextAction))
        | None -> ()
        if report.AvailableSlots <> Some 1 && (report.AvailableSlots |> Option.defaultValue 0) < 1 then
            return! Error(invalid "Account global capacity is held or unknown.")
        let! candidate =
            match report.SimulatedWinner with
            | Some selected when selected.QueueId = "planning"
                                 && selected.SourceRef.IsNone
                                 && selected.SourceCommit.IsNone -> Ok selected
            | _ -> Error(invalid "No verified source-origin planning Work wins global blocking-first FIFO.")
        let! binding =
            match bindings |> List.tryFind (fun b -> projectId b = candidate.ProjectId) with
            | Some matchBinding -> Ok matchBinding
            | None -> Error(invalid "Selected project is not in the complete approved membership set.")
        let! head = LedgerStore.fetch binding
        if head.Commit <> candidate.ProjectHead then
            return! Error(invalid "Project Git head changed after read-only global selection.")
        let! origin, originOid = verifiedPlanning binding head candidate
        let! account = AccountCapacityLedger.fetch context.Account
                      |> Result.mapError (fun e -> invalid ("Account Git head unavailable: " + explain e))
        if account.Head <> (report.AccountHead |> Option.defaultValue "") then
            return! Error(invalid "Account Git head changed after global selection.")
        if account.State.MaxConcurrentAgents < 1
           || AccountCapacityLedger.occupied account.State >= account.State.MaxConcurrentAgents then
            return! Error(invalid "Account capacity is held, conflicting or exhausted.")
        return binding, head, account, candidate, origin, originOid
    }

    let private freshIntent (context: DurableIntentContext) (binding: LedgerBinding)
                            (head: LedgerSnapshot) (account: AccountSnapshot)
                            (selected: CrossProjectProjectedDemand)
                            (origin: CanonicalWorkOrigin) originOid (prepareId: Guid) =
        { SchemaVersion = 2
          AccountId = accountId context.Account
          AccountLedgerId = context.Account.LedgerId.ToString("D")
          AccountEpoch = context.Account.Epoch
          AccountHead = account.Head
          ProjectId = selected.ProjectId
          ProjectLedgerId = binding.LedgerId.ToString("D")
          ProjectEpoch = binding.Epoch
          ProjectAuthoritySha256 =
              AccountAdmissionAdapter.projectAuthority binding
              |> AccountCapacityLedger.projectAuthoritySha256
          ProjectHead = head.Commit
          ProjectVersion = selected.ProjectVersion
          CatalogBlobOid = selected.CatalogBlobOid
          RoleId = selected.RoleId
          RoleVersion = selected.RoleVersion
          WorkId = selected.WorkId
          WorkGeneration = selected.WorkGeneration
          OriginBlobOid = originOid
          SourceHead = origin.SourceHead
          SourceOperationId = origin.SourceOperationId
          SourceRequestHash = origin.SourceRequestHash
          SourceOriginHead = origin.SourceOriginHead
          SourceOperationKind = origin.SourceOperationKind
          SourceRef = origin.SourceRef
          SourceCommit = origin.SourceCommit
          LaunchId = Guid.NewGuid().ToString("D")
          PrepareOperationId = prepareId.ToString("D")
          OriginalBrowserOperationId = Guid.NewGuid().ToString("D")
          AdmissionOperationId = Guid.NewGuid().ToString("D")
          // The canonical original, not the invoking client's label, owns
          // both the WQ07 request hash and WQ04 immutable admission actor.
          AdmissionActor = "WQ12.Prepared:" + prepareId.ToString("D")
          AbortOperationId = Guid.NewGuid().ToString("D")
          ReleaseOperationId = Guid.NewGuid().ToString("D")
          DispatchOperationId = Guid.NewGuid().ToString("D")
          FirstConnectOperationId = Guid.NewGuid().ToString("D") }

    // Read-only reconciliation. If the original CAS was published but its
    // acknowledgment was lost, the canonical record is the *only* source of
    // launch IDs. Missing/contradictory data never authorizes regeneration.
    let readPrepared (context: DurableIntentContext) (project: LedgerBinding)
                     workId generation : DurableIntentOutcome =
        match LedgerStore.fetch project with
        | Error error -> DurableIntentOutcome.Unknown("Project Git read: " + explain error)
        | Ok head ->
            match readAt project head workId generation with
            | Error error -> DurableIntentOutcome.NeedsAttention(explain error)
            | Ok None -> DurableIntentOutcome.NeedsAttention "No canonical prepared original exists."
            | Ok(Some prepared) ->
                if not (sameContext context project prepared) then
                    DurableIntentOutcome.NeedsAttention "Original prepared project/account authority differs."
                else
                    DurableIntentOutcome.Prepared(prepared, head.Commit, true)

    // The record is committed to the original project ledger with a create-only
    // path and exact pinned-head/role/source checks INSIDE the winning CAS.
    // This is a prepare operation only: it does not reserve credit or send.
    let prepareExplicit (enabled: bool) (context: DurableIntentContext)
                        (actor: string) : DurableIntentOutcome =
        if not enabled then DurableIntentOutcome.Disabled
        elif not (nonBlank actor) then DurableIntentOutcome.NeedsAttention "Actor is required for audit."
        else
            match freshSelection context with
            | Error error -> DurableIntentOutcome.NeedsAttention(explain error)
            | Ok (binding, head, account, selected, origin, originOid) ->
                let path = intentPath selected.ProjectId selected.WorkId selected.WorkGeneration
                match readAt binding head selected.WorkId selected.WorkGeneration with
                | Error error -> DurableIntentOutcome.NeedsAttention(explain error)
                | Ok(Some existing) ->
                    if sameContext context binding existing
                       && existing.SourceOperationId = origin.SourceOperationId
                       && existing.SourceRequestHash = origin.SourceRequestHash
                       && existing.OriginBlobOid = originOid
                       && existing.RoleId = selected.RoleId
                       && existing.RoleVersion = selected.RoleVersion then
                        DurableIntentOutcome.Prepared(existing, head.Commit, true)
                    else DurableIntentOutcome.NeedsAttention "A different canonical original already owns this Work generation."
                | Ok None ->
                    let operationId = Guid.NewGuid()
                    let intended = freshIntent context binding head account selected origin originOid operationId
                    let transaction: LedgerIntent =
                        { OperationId = operationId; Actor = actor
                          Kind = "WQ12.PreparePlanningLaunch"
                          RequestJson = codec intended }
                    let result =
                        LedgerStore.transact binding transaction (fun winningHead -> result {
                            if winningHead.Commit <> head.Commit then
                                return! Error(invalid "Project Git head raced; reselect before preparing another intent.")
                            let! _, winningOid = verifiedPlanning binding winningHead selected
                            if winningOid <> originOid then
                                return! Error(invalid "Original WQ04 source blob changed during CAS.")
                            let! currentAccount =
                                AccountCapacityLedger.fetch context.Account
                                |> Result.mapError (fun e -> invalid ("Account head unknown during CAS: " + explain e))
                            if currentAccount.Head <> account.Head
                               || AccountCapacityLedger.occupied currentAccount.State
                                    >= currentAccount.State.MaxConcurrentAgents then
                                return! Error(invalid "Account Git head/cap changed during preparation.")
                            return [ { Path = path
                                       ExpectedBlobOid = None
                                       Content = codec intended } ]
                        })
                    match result with
                    | Ok applied ->
                        match readPrepared context binding selected.WorkId selected.WorkGeneration with
                        | DurableIntentOutcome.Prepared (stored, canonical, _) when stored = intended ->
                            DurableIntentOutcome.Prepared(stored, canonical, applied.AlreadyApplied)
                        | _ ->
                            DurableIntentOutcome.Unknown
                                "CAS replied but canonical original could not be verified; preserve the original UUID."
                    | Error failure ->
                        // A rejected CAS or lost reply might still have applied.
                        // If another client won, adopt only the exact same
                        // selected work, source, role and approved authority.
                        match readPrepared context binding selected.WorkId selected.WorkGeneration with
                        | DurableIntentOutcome.Prepared (stored, canonical, _) when
                            stored.WorkId = selected.WorkId
                            && stored.WorkGeneration = selected.WorkGeneration
                            && stored.SourceOperationId = origin.SourceOperationId
                            && stored.SourceRequestHash = origin.SourceRequestHash
                            && stored.OriginBlobOid = originOid
                            && stored.RoleId = selected.RoleId
                            && stored.RoleVersion = selected.RoleVersion ->
                            DurableIntentOutcome.Prepared(stored, canonical, true)
                        | _ ->
                            DurableIntentOutcome.NeedsAttention
                                ("Prepare CAS rejected or outcome uncertain; reconcile original before retry: " + explain failure)

    // Explicit WQ07/WQ04 admission boundary for a *stored* original, not a
    // scheduler registration. It reserves at most one global token and checks
    // the immutable WQ04 source again. No browser/provider callback exists.
    let private admitPreparedCore (credential: IssuedWorkCredential option) enabled
                                  (context: DurableIntentContext)
                                  (stored: DurablePlanningLaunchIntent) actor =
        let retained why = TokenRetained("WQ12 held: " + why)
        if not enabled then retained "Default-off admission."
        elif not (nonBlank actor) then retained "Missing actor."
        elif credential |> Option.exists (fun issued -> isNull (box issued) || issued.LaunchId <> stored.LaunchId) then retained "Credential does not match original launch."
        elif not context.CompleteProjectSet || context.Projects.Length < 2
             || (context.Projects |> List.map (fun item -> item.Binding.ProjectId)
                 |> Set.ofList |> Set.count) <> context.Projects.Length then
            retained "The approved complete account/project set is unavailable."
        else
            match context.Projects |> List.tryFind (fun p -> projectId p.Binding = stored.ProjectId) with
            | None -> retained "Project is missing from approved membership."
            | Some selected ->
                match readPrepared context selected.Binding stored.WorkId stored.WorkGeneration with
                | DurableIntentOutcome.Prepared (original, _, _) when original = stored ->
                    let currentSelection =
                        match WorkQueueGitLedger.load selected.Binding with
                        | Error error -> Error("Project Git head is unknown: " + explain error)
                        | Ok (_, state) when state.Launches.ContainsKey original.LaunchId ->
                            // WQ04 verifies the original issuing CAS before any
                            // project launch replay, including immutable credit.
                            Ok ()
                        | Ok (_, state) ->
                            // At cap=1, global selection cannot select anything
                            // after WQ07 held the same original token but WQ04
                            // lost its CAS acknowledgment. Reconcile exact IDs.
                            match AccountCapacityLedger.fetch context.Account with
                            | Error error -> Error("Account Git head is unknown: " + explain error)
                            | Ok account ->
                                let token =
                                    account.State.Tokens
                                    |> Array.tryFind (fun item ->
                                        item.LaunchId = original.LaunchId
                                        || item.AdmissionOperationId = original.AdmissionOperationId)
                                match token with
                                | Some held when
                                    held.Status = "Held"
                                    && held.LaunchId = original.LaunchId
                                    && held.AdmissionOperationId = original.AdmissionOperationId
                                    && held.ProjectId = original.ProjectId
                                    && held.ProjectLedgerId = original.ProjectLedgerId
                                    && held.ProjectLedgerEpoch = original.ProjectEpoch
                                    && held.ProjectAuthoritySha256 = original.ProjectAuthoritySha256
                                    && held.RoleId = original.RoleId
                                    && held.RoleConfigVersion = int64 original.RoleVersion ->
                                    // A held token is not blanket permission:
                                    // WQ04 independently checks role/source/CAS.
                                    match state.Works.TryFind original.WorkId with
                                    | Some stage when stage.Kind = WorkKind.Planning
                                                      && stage.QueueId = "planning"
                                                      && stage.State = WorkState.Available
                                                      && stage.StageGeneration = original.WorkGeneration
                                                      && stage.Gate = GateState.Clear
                                                      && stage.Dependencies.IsEmpty
                                                      && stage.WaivedDependencies.IsEmpty -> Ok ()
                                    | _ -> Error "Held original Work generation is no longer eligible for admission."
                                | Some _ -> Error "Original account token is foreign, released or inconsistent; retain it."
                                | None ->
                                    // Before the FIRST reservation redo
                                    // blocking-first FIFO and global capacity.
                                    match freshSelection context with
                                    | Ok (binding, _, freshAccount, chosen, source, oid) when
                                        binding.ProjectId = selected.Binding.ProjectId
                                        && freshAccount.Head = original.AccountHead
                                        && chosen.WorkId = original.WorkId
                                        && chosen.WorkGeneration = original.WorkGeneration
                                        && chosen.RoleId = original.RoleId
                                        && chosen.RoleVersion = original.RoleVersion
                                        && chosen.CatalogBlobOid = original.CatalogBlobOid
                                        && source.SourceOperationId = original.SourceOperationId
                                        && source.SourceRequestHash = original.SourceRequestHash
                                        && oid = original.OriginBlobOid -> Ok ()
                                    | Ok _ -> Error "Current global winner or pinned account/source differs from the original."
                                    | Error error -> Error("Current global authority is unresolved: " + explain error)
                    match currentSelection with
                    | Error why -> retained why
                    | Ok () ->
                        let request: AccountAdmissionRequest =
                            { Actor = original.AdmissionActor
                              LaunchId = Guid.Parse original.LaunchId
                              AdmissionOperationId = Guid.Parse original.AdmissionOperationId
                              AbortOperationId = Guid.Parse original.AbortOperationId
                              ReleaseOperationId = Guid.Parse original.ReleaseOperationId
                              RoleId = original.RoleId
                              RoleVersion = original.RoleVersion }
                        let demand: PracticalAdmissionDemand =
                            { WorkId = original.WorkId
                              OriginBlobOid = original.OriginBlobOid }
                        match credential with
                        | None ->
                            WorkQueuePracticalAdmission.admitExplicit true
                                context.Account selected.Binding request demand
                        | Some issued ->
                            WorkQueuePracticalAdmission.admitExplicitWithCredential true
                                context.Account selected.Binding request demand issued
                | _ -> retained "Canonical prepared intent changed or is unreconciled."

    // The canonical prepared identity is the only authority for issuance.
    let admitPreparedExplicit enabled context stored actor =
        admitPreparedCore None enabled context stored actor

    // Transient, opt-in launcher only. No public scheduler or MCP entrypoint.
    let internal admitPreparedWithCredentialExplicit enabled context stored actor issued =
        admitPreparedCore (Some issued) enabled context stored actor
