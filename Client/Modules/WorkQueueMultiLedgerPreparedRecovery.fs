module Client.WorkQueueMultiLedgerPreparedRecovery

open System
open System.Text.Json
open Jarvis.WorkQueues
open Client.AccountCapacityLedger
open Client.LedgerStore
open Client.WorkQueueGitLedger
open Client.WorkQueueCanonicalMigrationPrepare
open Client.WorkQueueProjectMigrationPrepare

// A disposable-only coordinator. Three original Git repositories remain three
// independent authorities; no cross-repository transaction or new ledger exists.
type MultiLedgerProjectPlan =
    { Target: CanonicalProjectTarget
      OriginalOperationId: Guid
      OriginalJournalId: Guid }

type internal MultiLedgerPreparePlan =
    { AccountCommand: WorkQueueCanonicalMigrationPrepare.AccountPreparingCommand
      FixtureKeys: WorkQueueCanonicalMigrationPrepare.PinnedAccountPreparingKeys
      Projects: MultiLedgerProjectPlan list }

type MultiLedgerProjectObservation =
    { Name: string
      ProjectId: Guid
      ExpectedFrozenHead: string
      ObservedHead: string option
      OriginalOperationId: Guid
      Status: string
      Receipt: ProjectPreparedReceipt option
      Issue: string option }

type MultiLedgerRecovery =
    { AccountHead: string option
      AccountPhase: string
      AccountOriginalReceiptVerified: bool
      Projects: MultiLedgerProjectObservation list
      AllProjectsInertPrepared: bool
      SchedulerEnabled: bool
      ManagerEnabled: bool
      ProductionCutoverAuthorized: bool
      OperatorRepair: string list }

type MultiLedgerAttempt =
    { AccountWrite: AccountWrite option
      ProjectWrites: Map<Guid, LedgerWrite>
      Failure: string option
      Recovery: MultiLedgerRecovery }

module WorkQueueMultiLedgerPreparedRecovery =
    let private original (guid: Guid) = guid.ToString("D")
    let private account (plan: MultiLedgerPreparePlan) = plan.AccountCommand.Target.Account
    let private target (plan: MultiLedgerPreparePlan) = plan.AccountCommand.Target

    let private validate (plan: MultiLedgerPreparePlan) =
        let t = target plan
        let originals = t.Projects
        let planned = plan.Projects
        let checkpoints = plan.AccountCommand.OriginalSourceCheckpoints
        let distinctCount values = values |> List.distinct |> List.length
        if t.Mode <> CanonicalPrepareMode.AttemptOnDisposable then
            Error "Only a marked, explicitly requested disposable WQ10 attempt can write."
        elif t.OperationId = Guid.Empty || String.IsNullOrWhiteSpace t.Actor
             || originals.Length <> 2 || planned.Length <> 2
             || isNull checkpoints || checkpoints.Length <> 2 then
            Error "Exactly one signed account and two original project participants are required."
        elif distinctCount (originals |> List.map (fun p -> p.Binding.ProjectId)) <> 2
             || distinctCount (originals |> List.map (fun p -> p.Binding.LedgerId)) <> 2
             || distinctCount (originals |> List.map (fun p -> p.Binding.ApprovedPushUrl)) <> 2
             || distinctCount (planned |> List.map _.OriginalOperationId) <> 2
             || distinctCount (planned |> List.map _.OriginalJournalId) <> 2
             || planned |> List.exists (fun p ->
                 p.OriginalOperationId = Guid.Empty || p.OriginalJournalId = Guid.Empty
                 || p.OriginalOperationId = t.OperationId
                 || p.Target.Binding.ApprovedPushUrl = t.Account.ApprovedPushUrl) then
            Error "Project authorities, original operation UUIDs and original journal IDs must be distinct."
        elif planned |> List.mapi (fun index p ->
                 let expected = originals[index]
                 let sources = checkpoints |> Array.filter (fun c -> c.ProjectId = original p.Target.Binding.ProjectId)
                 p.Target <> expected
                 || sources.Length <> 1
                 || sources[0].FrozenProjectHead <> p.Target.ExpectedHead
                 || sources[0].OriginalJournalId <> original p.OriginalJournalId)
             |> List.exists id then
            Error "Project identity, epoch, frozen head or original journal does not match the signed account roster."
        else
            Ok ()

    // Compare the separately signed original account high-water against each
    // immutable project fixture journal BEFORE committing the first account CAS.
    // The existing project writer independently verifies every original UUID/hash.
    let private verifyOriginalJournals (plan: MultiLedgerPreparePlan) =
        let checkpoints = plan.AccountCommand.OriginalSourceCheckpoints
        let check p =
            let checkpoint =
                checkpoints |> Array.find (fun c -> c.ProjectId = original p.Target.Binding.ProjectId)
            match LedgerStore.fetch p.Target.Binding with
            | Error e -> Error(sprintf "%s: project read failed: %A" p.Target.Name e)
            | Ok snapshot ->
                match LedgerStore.readAt p.Target.Binding snapshot WorkQueueProjectMigrationPrepare.FixtureJournalPath with
                | Error e -> Error(sprintf "%s: original journal read failed: %A" p.Target.Name e)
                | Ok (Some json, Some _) ->
                    try
                        let journal = CanonicalCodec.decode<DisposableOriginalJournal> json
                        if CanonicalCodec.encode journal <> json
                           || journal.SchemaVersion <> 1
                           || journal.JournalId <> original p.OriginalJournalId
                           || journal.OriginalHighWater <= 0L
                           || journal.OriginalHighWater <> checkpoint.OriginalJournalHighWater
                           || journal.OriginalSourceEffectHighWater <> checkpoint.OriginalSourceEffectHighWater
                           || journal.OriginalRequestHashes.IsEmpty then
                            Error(sprintf "%s: signed source checkpoint does not match its original journal." p.Target.Name)
                        else Ok ()
                    with _ -> Error(sprintf "%s: malformed original journal." p.Target.Name)
                | _ -> Error(sprintf "%s: missing independent original journal." p.Target.Name)
        plan.Projects |> List.map check
        |> List.tryPick (function Error reason -> Some reason | Ok () -> None)
        |> function Some reason -> Error reason | None -> Ok ()

    let private matchesReceipt (plan: MultiLedgerPreparePlan) (p: MultiLedgerProjectPlan)
                               (receipt: ProjectPreparedReceipt) =
        receipt.OriginalOperationId = original p.OriginalOperationId
        && receipt.ProjectId = original p.Target.Binding.ProjectId
        && receipt.FrozenBaseHead = p.Target.ExpectedHead
        && receipt.AccountId = original (account plan).AccountId
        && receipt.AccountLedgerId = original (account plan).LedgerId
        && receipt.AccountEpoch = (account plan).Epoch
        && receipt.OriginalJournalId = original p.OriginalJournalId
        && receipt.ReviewedReportSha256 = (target plan).ReviewedOfflineReportSha256
        && not receipt.RuntimeAuthorized

    // Prepared conversion validates its own linked stages, not unrelated
    // WQ02 work added in a later Git CAS. A recovery claim covers the whole
    // current queue, including launches and every possible runnable stage.
    let private inspectWholeQueueInert (binding: LedgerBinding) =
        match LedgerStore.fetch binding with
        | Error error -> Error(sprintf "Cannot reread current project queue: %A" error)
        | Ok snapshot ->
            match WorkQueueGitLedger.loadAt binding snapshot with
            | Error error -> Error(sprintf "Cannot verify current project queue: %A" error)
            | Ok queue ->
                let everyStageInert =
                    queue.Works
                    |> Map.forall (fun _ stage ->
                        not stage.IntegrationAuthorized
                        && (match stage.State with
                            | WorkState.Blocked -> stage.Gate <> GateState.Clear
                            | WorkState.Completed | WorkState.Cancelled -> true
                            | WorkState.Available | WorkState.Claimed _
                            | WorkState.GateException -> false))
                let noAvailable =
                    match Contracts.orderAvailable queue with
                    | Ok stages -> List.isEmpty stages
                    | Error _ -> false
                if queue.Migration = MigrationEpoch.Preparing
                   && queue.Launches.IsEmpty && everyStageInert && noAvailable then
                    Ok snapshot.Commit
                else
                    Error "Current WQ02 queue is not wholly inert: a non-held/authorized stage, launch, or invalid ordering remains."

    // Recovery only reads the three original Git authorities. Project-only
    // success is intentionally NEVER an executable Work queue or cutover.
    let internal inspect (plan: MultiLedgerPreparePlan) : MultiLedgerRecovery =
        let accountHead, accountPhase, accountVerified =
            match AccountCapacityLedger.readOperation (account plan) (target plan).OperationId with
            | Ok (snapshot, Some operation) ->
                let verified =
                    operation.Kind = "AccountMigrationPreparing"
                    && operation.Actor = (target plan).Actor
                    && snapshot.State.MigrationPhase = "Preparing"
                    && AccountCapacityLedger.occupied snapshot.State = 0
                    && snapshot.State.StateVersion = plan.AccountCommand.ExpectedAccountStateVersion + 1L
                    && (try
                            let receipt =
                                JsonSerializer.Deserialize<AccountMigrationPreparingReceipt>(
                                    operation.OriginalResultJson,
                                    JsonSerializerOptions(PropertyNameCaseInsensitive = true))
                            not (isNull (box receipt))
                            && receipt.SchemaVersion = 1
                            && receipt.OriginalOperationId = original (target plan).OperationId
                            && receipt.Actor = (target plan).Actor
                            && receipt.AccountId = original (account plan).AccountId
                            && receipt.LedgerId = original (account plan).LedgerId
                            && receipt.Epoch = (account plan).Epoch
                            && receipt.PreviousAccountHead = (target plan).ExpectedAccountHead
                            && receipt.PreviousStateVersion = plan.AccountCommand.ExpectedAccountStateVersion
                            && receipt.StatementSha256 =
                               WorkQueueCanonicalMigrationPrepare.accountPreparingStatementSha256 plan.AccountCommand
                            && receipt.OriginalSourceCoverageSha256 =
                               WorkQueueCanonicalMigrationPrepare.accountPreparingCoverageSha256 plan.AccountCommand
                            && receipt.OriginalSourceCheckpoints = plan.AccountCommand.OriginalSourceCheckpoints
                            && receipt.ParticipantRoster.Length = 2
                            && (target plan).Projects |> List.forall (fun expected ->
                               receipt.ParticipantRoster |> Array.exists (fun bound ->
                                   bound.ProjectId = original expected.Binding.ProjectId
                                   && bound.LedgerId = original expected.Binding.LedgerId
                                   && bound.LedgerEpoch = expected.Binding.Epoch
                                   && bound.FrozenHead = expected.ExpectedHead
                                   && bound.ProjectVersion = expected.ExpectedVersion
                                   && bound.SourceHighWater = expected.ExpectedHighWater))
                            && receipt.AccountStatus = "AccountPreparing"
                            && receipt.ProjectStatus = "ProjectPreparedNotEstablished"
                            && receipt.ReviewedReportSha256 = (target plan).ReviewedOfflineReportSha256
                        with _ -> false)
                Some snapshot.Head, snapshot.State.MigrationPhase, verified
            | Ok (snapshot, None) -> Some snapshot.Head, snapshot.State.MigrationPhase, false
            | Error _ -> None, "Unreadable", false

        let projects =
            plan.Projects |> List.map (fun p ->
                let observed =
                    match LedgerStore.fetch p.Target.Binding with
                    | Ok snapshot -> Some snapshot.Commit
                    | Error _ -> None
                let status, receipt, issue =
                    match WorkQueueProjectMigrationPrepare.inspect p.Target.Binding (account plan) with
                    | Ok ProjectPreparedInspection.NotPrepared -> "NotPrepared", None, None
                    | Ok (ProjectPreparedInspection.ProjectOnly r) when matchesReceipt plan p r ->
                        match inspectWholeQueueInert p.Target.Binding with
                        | Ok head when Some head = observed -> "PreparedInert", Some r, None
                        | Ok _ -> "RecoveryBlocked", Some r, Some "Project head moved during recovery inspection; reread the canonical authorities."
                        | Error reason -> "RecoveryBlocked", Some r, Some reason
                    | Ok (ProjectPreparedInspection.AccountHeadDiverged(r, head)) when matchesReceipt plan p r ->
                        "AccountHeadDiverged", Some r, Some("Account head moved to " + head)
                    | Ok (ProjectPreparedInspection.RecoveryBlocked reason) ->
                        "RecoveryBlocked", None, Some reason
                    | Ok _ -> "RecoveryBlocked", None, Some "Project receipt does not bind the original signed account/project source."
                    | Error error -> "Unreadable", None, Some(sprintf "%A" error)
                { Name = p.Target.Name
                  ProjectId = p.Target.Binding.ProjectId
                  ExpectedFrozenHead = p.Target.ExpectedHead
                  ObservedHead = observed
                  OriginalOperationId = p.OriginalOperationId
                  Status = status
                  Receipt = receipt
                  Issue = issue })
        let complete = accountVerified
                       && projects.Length = 2
                       && projects |> List.forall (fun p -> p.Status = "PreparedInert")
        let describe p =
            sprintf "%s original UUID %s: expected Frozen %s; observed %s; status %s"
                p.Name (original p.OriginalOperationId) p.ExpectedFrozenHead
                (defaultArg p.ObservedHead "<unreadable>") p.Status
        let repair =
            [ sprintf "Inspect original account UUID %s and account head %s (%s); never force-reset a Git authority."
                  (original (target plan).OperationId) (defaultArg accountHead "<unreadable>") accountPhase
              yield! projects |> List.map describe
              "For a stale or changed participant, preserve already committed Prepared receipts, reconcile original operation IDs and signed Frozen/source checkpoints at the observed heads, and obtain explicit operator repair or a newly reviewed re-plan. Do not mint replacement UUIDs for uncertain acknowledgments."
              "Prepared project queues remain Blocked with a non-clear gate; no token release, Scheduler, Manager, production epoch flip or public migration write is authorized." ]
        { AccountHead = accountHead; AccountPhase = accountPhase
          AccountOriginalReceiptVerified = accountVerified
          Projects = projects
          AllProjectsInertPrepared = complete
          SchedulerEnabled = false; ManagerEnabled = false
          ProductionCutoverAuthorized = false; OperatorRepair = repair }

    // No host caller can supply public authority to cross this boundary.
    let internal attempt (_: MultiLedgerPreparePlan) : Result<MultiLedgerAttempt, string> =
        Error "Public WQ10 multi-ledger migration is disabled: real host trust, legacy writer high-water and source-role authorization remain NO-GO."

    // An internal deterministic hook only for two-clone race fixtures. It runs
    // after A was independently committed and before the B CAS, never as a
    // signature, authority, or runnable host callback.
    let internal attemptOnDisposableFixtureWithInterposition
        (plan: MultiLedgerPreparePlan) (afterA: unit -> unit) : Result<MultiLedgerAttempt, string> =
        match validate plan |> Result.bind (fun () -> verifyOriginalJournals plan) with
        | Error reason -> Error reason
        | Ok () ->
            let prepared =
                WorkQueueCanonicalMigrationPrepare.attemptAccountPreparingWithFixtureTrustAnchors
                    plan.FixtureKeys plan.AccountCommand
            match prepared.Account with
            | WorkQueueCanonicalMigrationPrepare.AccountPreparingState.AccountRecoveryBlocked reasons ->
                Ok { AccountWrite = None; ProjectWrites = Map.empty
                     Failure = Some(String.concat "; " reasons); Recovery = inspect plan }
            | WorkQueueCanonicalMigrationPrepare.AccountPreparingState.AccountPreparing(_, write) ->
                let mutable writes = Map.empty
                let mutable failure: string option = None
                let expectedAccountHead = write.CurrentHead
                let expectedAccountVersion = plan.AccountCommand.ExpectedAccountStateVersion + 1L
                for index, p in plan.Projects |> List.indexed do
                    if failure.IsNone then
                        match AccountCapacityLedger.fetch (account plan) with
                        | Error error -> failure <- Some(sprintf "Account reread: %A" error)
                        | Ok snapshot when snapshot.Head <> expectedAccountHead
                                           || snapshot.State.StateVersion <> expectedAccountVersion
                                           || snapshot.State.MigrationPhase <> "Preparing"
                                           || AccountCapacityLedger.occupied snapshot.State <> 0 ->
                            failure <- Some "Account head/epoch changed after Preparing; stop before the next project CAS."
                        | Ok _ ->
                            let command: ProjectPrepareCommand =
                                { OriginalOperationId = p.OriginalOperationId
                                  Actor = (target plan).Actor
                                  Project = p.Target.Binding
                                  ExpectedProjectHead = p.Target.ExpectedHead
                                  Account = account plan
                                  ExpectedAccountHead = expectedAccountHead
                                  ExpectedAccountStateVersion = expectedAccountVersion
                                  ReviewedReportSha256 = (target plan).ReviewedOfflineReportSha256
                                  OriginalJournalId = p.OriginalJournalId
                                  DisposableRoot = (target plan).DisposableRoot }
                            match WorkQueueProjectMigrationPrepare.attemptOnDisposableFixture command with
                            | Error error -> failure <- Some(sprintf "%s CAS: %A" p.Target.Name error)
                            | Ok result ->
                                writes <- writes.Add(p.Target.Binding.ProjectId, result)
                                if index = 0 then
                                    try afterA ()
                                    with ex -> failure <- Some("Fixture interposition failed: " + ex.Message)
                // Idempotent AlreadyApplied UUID receipts are not proof that the
                // current three Git heads still contain a verified inert state.
                let recovery = inspect plan
                let finalFailure =
                    match failure with
                    | Some _ -> failure
                    | None when not recovery.AccountOriginalReceiptVerified ->
                        Some "Partial WQ10 recovery: the current account Preparing receipt/head is not verified."
                    | None when not recovery.AllProjectsInertPrepared ->
                        Some "Partial WQ10 recovery: both original project receipts and fully inert queues are not verified."
                    | None -> None
                Ok { AccountWrite = Some write; ProjectWrites = writes
                     Failure = finalFailure; Recovery = recovery }

    let internal attemptOnDisposableFixture plan =
        attemptOnDisposableFixtureWithInterposition plan ignore
