module ServerWorkLegacyImportFenceTests

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text
open NUnit.Framework
open Jarvis.WorkQueues
open Jarvis.WorkQueues.Contracts
open Server

let private shaA = String.replicate 40 "a"
let private shaB = String.replicate 40 "b"
let private shaC = String.replicate 40 "c"
let private digest = String.replicate 64 "e"
let private projectOne = Guid.Parse "11111111-1111-4111-8111-111111111111"
let private projectTwo = Guid.Parse "22222222-2222-4222-8222-222222222222"
let private ledgerOne = Guid.Parse "33333333-3333-4333-8333-333333333333"
let private ledgerTwo = Guid.Parse "44444444-4444-4444-8444-444444444444"
let private accountId = Guid.Parse "55555555-5555-4555-8555-555555555555"
let private accountLedger = Guid.Parse "66666666-6666-4666-8666-666666666666"
let private deviceId = Guid.Parse "77777777-7777-4777-8777-777777777777"
let private fenceOperation = Guid.Parse "88888888-8888-4888-8888-888888888888"
let private importOperation = Guid.Parse "99999999-9999-4999-8999-999999999999"
let private decisionId = Guid.Parse "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"
let private exclusiveId = Guid.Parse "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"

let private operation =
    { OperationId = Guid.Parse "cccccccc-cccc-4ccc-8ccc-cccccccccccc"
      Sequence = 1L
      RequestSha256 = digest
      ResultSha256 = digest
      Kind = "verified-ledger-operation"
      TerminalResult = None }

let private originalHead =
    { PinnedCommit = shaA
      ObservedCommit = shaA
      AncestryVerified = true
      HistoryComplete = true
      SourceHighWater = 1L
      ObservedHighWater = 1L }

let private subscriptions = Map.ofList [ "Worker", Set.singleton "implementation" ]

let private project (projectId: Guid) (ledgerId: Guid) workId =
    let stage =
        { Id = workId
          ProjectId = projectId.ToString("D")
          SubjectId = "task-" + workId
          Kind = WorkKind.Implementation
          QueueId = "implementation"
          StageGeneration = 1
          State = WorkState.Available
          Domain = DomainState.Undecided
          Gate = GateState.Clear
          ClaimGeneration = 0
          Dependencies = Set.empty
          WaivedDependencies = Map.empty
          AvailableAt = 1L
          AvailableSequence = 1L
          SourceLineage = None
          Push = None
          IntegrationAuthorized = false }
    { ProjectId = projectId
      LedgerId = ledgerId
      LedgerEpoch = 1
      Head = originalHead
      Queue =
        { emptyProject subscriptions with
            Migration = MigrationEpoch.Frozen
            Works = Map.ofList [ workId, stage ] }
      Graph = WorkComposition.empty
      Origins =
        [ { WorkId = workId
            StageGeneration = 1
            SubjectId = "task-" + workId
            SourceHead = shaA
            SourceHighWater = 1L
            SourceAncestryVerified = true
            RepositoryId = "trusted-test-repo"
            SourceRef = "refs/heads/verified-source"
            SourceCommit = shaB
            RoleId = "Worker"
            RoleVersion = 1
            GraphVersion = 0
            ContextDigest = digest } ]
      Reviews = []
      Integrations = []
      Terminals = []
      Validations = []
      Operations = [ operation ] }

let private input () =
    { Account =
        { AccountId = accountId
          LedgerId = accountLedger
          LedgerEpoch = 1
          Head = originalHead
          MaxConcurrent = 2
          Tokens = []
          Operations = [ operation ] }
      Projects = [ project projectOne ledgerOne "I1"; project projectTwo ledgerTwo "I2" ] }

let private binding scope entity ledger =
    { Scope = scope
      EntityId = entity
      LedgerId = ledger
      LedgerEpoch = 1
      DeviceId = deviceId
      TrustedRemoteId = "source-authenticated-origin/" + ledger.ToString("D")
      PinnedHead = shaA
      SourceHighWater = 1L }

let private plan () =
    { Account = binding WorkLegacyGitScope.Account accountId accountLedger
      Projects =
        [ binding WorkLegacyGitScope.Project projectOne ledgerOne
          binding WorkLegacyGitScope.Project projectTwo ledgerTwo ]
      Approval =
        { DecisionId = decisionId
          DecisionVersion = 4L
          DeviceId = deviceId
          AccountId = accountId
          ProjectIds = [ projectOne; projectTwo ]
          FenceOperationId = fenceOperation
          ImportOperationId = importOperation } }

let private hashHead (value: string) =
    SHA256.HashData(Encoding.UTF8.GetBytes value)
    |> Convert.ToHexString
    |> fun hex -> hex.ToLowerInvariant().Substring(0, 40)

// An in-memory stand-in for the future source-authenticated original Git
// writer. No production authority can be obtained through this test double.
type private HermeticOriginalGitGateway(approvedPlan: WorkLegacyFencePlan) =
    let gate = obj()
    let trusted =
        (approvedPlan.Account :: approvedPlan.Projects)
        |> List.map (fun b -> b.LedgerId, b)
        |> dict
    let currentHeads = Dictionary<Guid, string>()
    let receipts = Dictionary<Guid, WorkLegacyGitFenceReceipt>()
    let mutable exclusive = Guid.Empty
    let mutable casCalls = 0
    let mutable unknownOnce: Guid option = None
    let mutable rejectAt: Guid option = None
    let mutable forgedOnce: Guid option = None
    let mutable verifyUnknownOnce: Guid option = None
    let mutable mutateAfterCasOnce: Guid option = None
    let mutable attemptConcurrentMutation = false
    let mutable concurrentMutationBlocked = false
    do
        for binding in approvedPlan.Account :: approvedPlan.Projects do
            currentHeads.[binding.LedgerId] <- binding.PinnedHead

    member _.CasCalls = casCalls
    member _.IsQuiesced = exclusive <> Guid.Empty
    member _.ConcurrentMutationBlocked = concurrentMutationBlocked
    member _.RejectAt with get() = rejectAt and set value = rejectAt <- value
    member _.UnknownOnce with get() = unknownOnce and set value = unknownOnce <- value
    member _.ForgedOnce with get() = forgedOnce and set value = forgedOnce <- value
    member _.VerifyUnknownOnce with get() = verifyUnknownOnce and set value = verifyUnknownOnce <- value
    member _.MutateAfterCasOnce with get() = mutateAfterCasOnce and set value = mutateAfterCasOnce <- value
    member _.AttemptConcurrentMutation with get() = attemptConcurrentMutation and set value = attemptConcurrentMutation <- value

    member _.MoveRemoteHead(ledgerId, head) =
        lock gate (fun () -> currentHeads.[ledgerId] <- head)

    member _.TryOriginalMutation(ledgerId: Guid) =
        lock gate (fun () ->
            if exclusive <> Guid.Empty then false
            else
                currentHeads.[ledgerId] <- shaC
                true)

    interface IWorkLegacyOriginalGitFenceGateway with
        member _.VerifyOperatorDecision approval =
            if approval = approvedPlan.Approval then Ok approval
            else Error "Stale or forged operator decision."

        member _.ReadTrustedBinding binding =
            lock gate (fun () ->
                if not (trusted.ContainsKey binding.LedgerId) then
                    Error "Original remote binding absent."
                else
                    let expected = trusted.[binding.LedgerId]
                    let head = currentHeads.[binding.LedgerId]
                    let originalOrOwnedFence =
                        head = expected.PinnedHead
                        || (receipts.ContainsKey binding.LedgerId
                            && head = receipts.[binding.LedgerId].FencedHead)
                    if not originalOrOwnedFence then Error "Original Git head changed."
                    else Ok expected)

        member _.AcquireExclusiveBarrier approval =
            lock gate (fun () ->
                if approval <> approvedPlan.Approval then Error "Decision changed."
                else
                    if exclusive = Guid.Empty then exclusive <- exclusiveId
                    Ok exclusive)

        member this.CasFenceOriginalWriter(binding, approval, receiptId) =
            lock gate (fun () ->
                casCalls <- casCalls + 1
                if receiptId <> exclusiveId || exclusive = Guid.Empty then
                    Error "No exclusive writer barrier."
                elif rejectAt = Some binding.LedgerId then
                    Error "Source-Git CAS rejected."
                elif attemptConcurrentMutation then
                    concurrentMutationBlocked <- not (this.TryOriginalMutation binding.LedgerId)
                    attemptConcurrentMutation <- false
                    // All original writers are blocked before the first CAS.
                    let head = currentHeads.[binding.LedgerId]
                    if head <> binding.PinnedHead
                       && not (receipts.ContainsKey binding.LedgerId) then
                        Error "Concurrent old-Git writer modified head."
                    else
                        // Continue with the same CAS below using a local function.
                        let actual =
                            if receipts.ContainsKey binding.LedgerId then
                                receipts.[binding.LedgerId]
                            else
                                let fenceHead =
                                    hashHead (binding.LedgerId.ToString("D") + approval.FenceOperationId.ToString("D"))
                                let result =
                                    { Binding = binding
                                      DecisionId = approval.DecisionId
                                      DecisionVersion = approval.DecisionVersion
                                      FenceOperationId = approval.FenceOperationId
                                      ExclusiveReceiptId = receiptId
                                      OriginalHead = binding.PinnedHead
                                      FencedHead = fenceHead
                                      OriginalHighWater = binding.SourceHighWater
                                      FenceHighWater = binding.SourceHighWater + 1L
                                      WriterGeneration = 1L
                                      NoNewOriginalMutations = true
                                      DurableUntilExplicitCutover = true
                                      OriginalPendingIntentHighWater = 1L
                                      OriginalSourceEffectHighWater = 1L
                                      MinimumClientProtocolVersion = 1
                                      AllOriginalClientsQuiesced = true
                                      AllClientQuiescenceProofSha256 = digest
                                      AuditSha256 = digest }
                                receipts.[binding.LedgerId] <- result
                                currentHeads.[binding.LedgerId] <- fenceHead
                                result
                        Ok actual
                else
                    let existed = receipts.TryGetValue binding.LedgerId
                    let actual =
                        match existed with
                        | true, result -> Some result
                        | false, _ when currentHeads.[binding.LedgerId] <> binding.PinnedHead -> None
                        | false, _ ->
                            let fenceHead =
                                hashHead (binding.LedgerId.ToString("D") + approval.FenceOperationId.ToString("D"))
                            let result =
                                { Binding = binding
                                  DecisionId = approval.DecisionId
                                  DecisionVersion = approval.DecisionVersion
                                  FenceOperationId = approval.FenceOperationId
                                  ExclusiveReceiptId = receiptId
                                  OriginalHead = binding.PinnedHead
                                  FencedHead = fenceHead
                                  OriginalHighWater = binding.SourceHighWater
                                  FenceHighWater = binding.SourceHighWater + 1L
                                  WriterGeneration = 1L
                                  NoNewOriginalMutations = true
                                  DurableUntilExplicitCutover = true
                                  OriginalPendingIntentHighWater = 1L
                                  OriginalSourceEffectHighWater = 1L
                                  MinimumClientProtocolVersion = 1
                                  AllOriginalClientsQuiesced = true
                                  AllClientQuiescenceProofSha256 = digest
                                  AuditSha256 = digest }
                            receipts.[binding.LedgerId] <- result
                            currentHeads.[binding.LedgerId] <- fenceHead
                            Some result
                    match actual with
                    | None -> Error "Original-Git CAS expected pinned head."
                    | Some result when unknownOnce = Some binding.LedgerId ->
                        unknownOnce <- None
                        Error "Unknown Git transport outcome after CAS."
                    | Some result when mutateAfterCasOnce = Some binding.LedgerId ->
                        mutateAfterCasOnce <- None
                        currentHeads.[binding.LedgerId] <- shaC
                        Ok result
                    | Some result when forgedOnce = Some binding.LedgerId ->
                        forgedOnce <- None
                        Ok { result with AllOriginalClientsQuiesced = false }
                    | Some result -> Ok result)

        member _.VerifyDurableFence(binding, receipt) =
            lock gate (fun () ->
                if verifyUnknownOnce = Some binding.LedgerId then
                    verifyUnknownOnce <- None
                    Error "Unable to independently verify original Git fence."
                elif exclusive = Guid.Empty
                     || not (receipts.ContainsKey binding.LedgerId)
                     || currentHeads.[binding.LedgerId] <> receipts.[binding.LedgerId].FencedHead
                     || receipts.[binding.LedgerId] <> receipt then
                    Error "Fence receipt or original-Git CAS head diverged."
                else Ok receipt)

let private withDisposablePath action =
    let root =
        Path.Combine(Path.GetTempPath(), "jarvis-work-c2-fixture-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    File.WriteAllText(Path.Combine(root, WorkLegacyImportSqlite.DisposableMarker),
                      WorkLegacyImportSqlite.DisposableMarkerContent)
    let dbPath = Path.Combine(root, "work.sqlite")
    try action dbPath
    finally
        if Directory.Exists root then Directory.Delete(root, true)

let private run gateway path evidence request =
    WorkLegacyImportFence.commitFencedOfflineFixture
        (gateway :> IWorkLegacyOriginalGitFenceGateway) path evidence request

let private importOrFail gateway path evidence request =
    match run gateway path evidence request with
    | Ok receipt -> receipt
    | Error gap -> failwithf "Valid C2 hermetic import rejected: %A" gap

[<Test>]
let c2_original_git_cas_fence_and_two_project_account_import_are_single_atomic_receipt () =
    withDisposablePath (fun path ->
        let evidence, request = input (), plan ()
        let gateway = HermeticOriginalGitGateway(request)
        let result = importOrFail gateway path evidence request
        Assert.That(result.Import.Replayed, Is.False)
        Assert.That(result.Import.SqliteSequence, Is.EqualTo 1L)
        Assert.That(result.OriginalWriterFence, Has.Length.EqualTo 3)
        Assert.That(gateway.IsQuiesced, Is.True)
        Assert.That(gateway.TryOriginalMutation ledgerOne, Is.False)
        Assert.That(gateway.TryOriginalMutation accountLedger, Is.False)
        let db = WorkSqliteAuthority path
        let auditKey =
            { ProjectId = accountId.ToString("D")
              Kind = WorkSqliteRecordKind.Project
              EntityId = "legacy-import-c2-authority-audit" }
        match db.ReadRecord auditKey with
        | Ok(Some record) ->
            Assert.That(record.Value.BodyJson, Does.Contain("c2-disposable-offline-only"))
            Assert.That(record.Value.BodyJson, Does.Contain("\"DurableUntilExplicitCutover\":true"))
            Assert.That(record.Value.BodyJson, Does.Contain("\"AllOriginalClientsQuiesced\":true"))
            Assert.That(record.Value.BodyJson, Does.Contain("\"FenceHighWater\":2"))
            Assert.That(record.Value.ExternalEffect, Is.EqualTo WorkSqliteEffect.Prepared)
        | other -> Assert.Fail(sprintf "No atomic C2 authority audit: %A" other)
        match db.ReadCapacity(accountId.ToString("D")) with
        | Ok(Some capacity) ->
            Assert.That(capacity.Maximum, Is.EqualTo 2)
            Assert.That(capacity.Reserved, Is.Zero)
        | other -> Assert.Fail(sprintf "Global capacity not imported: %A" other)
        let replay = importOrFail gateway path evidence request
        Assert.That(replay.Import.Replayed, Is.True)
        Assert.That(replay.Import.SqliteSequence, Is.EqualTo 1L)
        Assert.That(db.CurrentSequence() |> Result.defaultValue -1L, Is.EqualTo 1L))

[<Test>]
let c2_original_writer_is_quiesced_before_first_git_cas_even_under_simultaneous_attempt () =
    withDisposablePath (fun path ->
        let evidence, request = input (), plan ()
        let gateway = HermeticOriginalGitGateway request
        gateway.AttemptConcurrentMutation <- true
        importOrFail gateway path evidence request |> ignore
        Assert.That(gateway.ConcurrentMutationBlocked, Is.True)
        Assert.That(gateway.TryOriginalMutation ledgerTwo, Is.False))

[<Test>]
let c2_same_name_other_project_and_device_cannot_rebind_original_git_source () =
    withDisposablePath (fun path ->
        let evidence, request = input (), plan ()
        let gateway = HermeticOriginalGitGateway request
        let wrongDevice =
            { request with
                Projects =
                    request.Projects
                    |> List.map (fun p ->
                        if p.EntityId = projectTwo then { p with DeviceId = Guid.NewGuid() } else p) }
        match run gateway path evidence wrongDevice with
        | Error(WorkLegacyFenceFailure.InvalidPlan _) -> ()
        | result -> Assert.Fail(sprintf "Same name on other device accepted: %A" result)
        let duplicateProject =
            { request with
                Projects =
                    request.Projects
                    |> List.map (fun p ->
                        if p.EntityId = projectTwo then { p with EntityId = projectOne } else p) }
        match run gateway path evidence duplicateProject with
        | Error(WorkLegacyFenceFailure.InvalidPlan _) -> ()
        | result -> Assert.Fail(sprintf "Ambiguous project binding accepted: %A" result)
        Assert.That(gateway.CasCalls, Is.Zero)
        Assert.That(File.Exists path, Is.False))

[<Test>]
let c2_stale_operator_decision_and_untrusted_original_git_head_fail_before_any_cas () =
    withDisposablePath (fun path ->
        let evidence, request = input (), plan ()
        let gateway = HermeticOriginalGitGateway request
        let stale = { request with Approval = { request.Approval with DecisionVersion = 3L } }
        match run gateway path evidence stale with
        | Error(WorkLegacyFenceFailure.GatewayFailure _) -> ()
        | result -> Assert.Fail(sprintf "Stale approval accepted: %A" result)
        gateway.MoveRemoteHead(ledgerOne, shaC)
        match run gateway path evidence request with
        | Error(WorkLegacyFenceFailure.GatewayFailure _) -> ()
        | result -> Assert.Fail(sprintf "Unverified upstream head accepted: %A" result)
        Assert.That(gateway.CasCalls, Is.Zero)
        Assert.That(File.Exists path, Is.False))

[<Test>]
let c2_unknown_git_cas_does_not_create_sqlite_and_replays_without_second_old_writer () =
    withDisposablePath (fun path ->
        let evidence, request = input (), plan ()
        let gateway = HermeticOriginalGitGateway request
        gateway.UnknownOnce <- Some accountLedger
        match run gateway path evidence request with
        | Error(WorkLegacyFenceFailure.GatewayFailure _) -> ()
        | result -> Assert.Fail(sprintf "Unknown Git CAS treated as success: %A" result)
        Assert.That(File.Exists path, Is.False)
        Assert.That(gateway.IsQuiesced, Is.True)
        Assert.That(gateway.TryOriginalMutation ledgerOne, Is.False)
        let recovered = importOrFail gateway path evidence request
        Assert.That(recovered.Import.Replayed, Is.False)
        Assert.That(recovered.OriginalWriterFence, Has.Length.EqualTo 3))

[<Test>]
let c2_partial_git_fence_failure_retains_exclusive_barrier_and_recovery_operation () =
    withDisposablePath (fun path ->
        let evidence, request = input (), plan ()
        let gateway = HermeticOriginalGitGateway request
        gateway.RejectAt <- Some ledgerOne
        match run gateway path evidence request with
        | Error(WorkLegacyFenceFailure.GatewayFailure _) -> ()
        | result -> Assert.Fail(sprintf "Partial original Git fence wrongly imported: %A" result)
        Assert.That(File.Exists path, Is.False)
        Assert.That(gateway.IsQuiesced, Is.True)
        Assert.That(gateway.TryOriginalMutation ledgerTwo, Is.False)
        gateway.RejectAt <- None
        importOrFail gateway path evidence request |> ignore
        Assert.That(gateway.CasCalls, Is.GreaterThan 3))

[<Test>]
let c2_forged_cas_receipt_and_unknown_fence_reverification_fail_closed () =
    withDisposablePath (fun path ->
        let evidence, request = input (), plan ()
        let gateway = HermeticOriginalGitGateway request
        gateway.ForgedOnce <- Some ledgerOne
        match run gateway path evidence request with
        | Error(WorkLegacyFenceFailure.InvalidFenceReceipt _) -> ()
        | result -> Assert.Fail(sprintf "Forged writer generation accepted: %A" result)
        Assert.That(File.Exists path, Is.False)
        gateway.VerifyUnknownOnce <- Some ledgerOne
        match run gateway path evidence request with
        | Error(WorkLegacyFenceFailure.GatewayFailure _) -> ()
        | result -> Assert.Fail(sprintf "Unknown fence verification accepted: %A" result)
        Assert.That(File.Exists path, Is.False)
        importOrFail gateway path evidence request |> ignore)

[<Test>]
let c2_preflight_failure_and_unmarked_target_never_touch_original_git_writer () =
    withDisposablePath (fun path ->
        let evidence, request = input (), plan ()
        let gateway = HermeticOriginalGitGateway request
        let wrong =
            { evidence with
                Projects =
                    evidence.Projects
                    |> List.map (fun p ->
                        if p.ProjectId = projectOne then { p with Origins = [] } else p) }
        match run gateway path wrong request with
        | Error(WorkLegacyFenceFailure.PreflightRejected _) -> ()
        | result -> Assert.Fail(sprintf "Preflight gap accepted: %A" result)
        let outside = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "live.sqlite")
        match run gateway outside evidence request with
        | Error(WorkLegacyFenceFailure.UnsafeDestination _) -> ()
        | result -> Assert.Fail(sprintf "Unmarked database accepted: %A" result)
        Assert.That(gateway.CasCalls, Is.Zero)
        Assert.That(gateway.IsQuiesced, Is.False)
        Assert.That(File.Exists path, Is.False))

[<Test>]
let c2_sqlite_late_conflict_rolls_back_audit_and_project_records_together () =
    withDisposablePath (fun path ->
        let evidence, request = input (), plan ()
        let gateway = HermeticOriginalGitGateway request
        let db = WorkSqliteAuthority path
        Assert.That(db.Initialize() |> Result.defaultValue -1, Is.EqualTo 1)
        let seedKey =
            { ProjectId = projectTwo.ToString("D")
              Kind = WorkSqliteRecordKind.Stage
              EntityId = "I2" }
        let seedValue =
            { BodyJson = "{}"
              GraphVersion = 0L
              StageGeneration = 0L
              ClaimGeneration = 0L
              StopGeneration = 0L
              RoleVersion = 0L
              SourceSha = None
              ReviewSha = None
              IntegrationSha = None
              ExternalEffect = WorkSqliteEffect.NoneRecorded }
        let seed =
            { OperationId = Guid.Parse "dddddddd-dddd-4ddd-8ddd-dddddddddddd"
              Guards = []
              Records = [ { Key = seedKey; ExpectedVersion = None; Value = seedValue } ]
              Capacities = [] }
        Assert.That(db.Commit seed |> Result.isOk, Is.True)
        match run gateway path evidence request with
        | Error(WorkLegacyFenceFailure.ImportRejected(
            WorkLegacyImportSqliteGap.StorageFailure(WorkSqliteFailure.EntityAlreadyExists _))) -> ()
        | result -> Assert.Fail(sprintf "Late SQLite collision not atomic: %A" result)
        let audit = { seedKey with ProjectId = accountId.ToString("D")
                                   Kind = WorkSqliteRecordKind.Project
                                   EntityId = "legacy-import-c2-authority-audit" }
        match db.ReadRecord audit with
        | Ok None -> ()
        | result -> Assert.Fail(sprintf "C2 audit survived rollback: %A" result)
        match db.ReadCapacity(accountId.ToString("D")) with
        | Ok None -> ()
        | result -> Assert.Fail(sprintf "Capacity committed despite failure: %A" result)
        Assert.That(db.CurrentSequence() |> Result.defaultValue -1L, Is.EqualTo 1L)
        Assert.That(gateway.IsQuiesced, Is.True))


[<Test>]
let c2_changed_original_git_remote_after_cas_blocks_import_even_with_a_receipt () =
    withDisposablePath (fun path ->
        let evidence, request = input (), plan ()
        let gateway = HermeticOriginalGitGateway request
        gateway.MutateAfterCasOnce <- Some ledgerTwo
        match run gateway path evidence request with
        | Error(WorkLegacyFenceFailure.GatewayFailure _) -> ()
        | result -> Assert.Fail(sprintf "Changed original Git remote accepted: %A" result)
        Assert.That(File.Exists path, Is.False)
        Assert.That(gateway.IsQuiesced, Is.True))
