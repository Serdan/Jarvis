module ServerOriginalGitPublisherGatewayTests

open System
open NUnit.Framework
open Jarvis.WorkQueues

let private unwrap = function
    | Ok value -> value
    | Error error -> failwithf "Unexpected publisher failure: %A" error

[<Test>]
let state_blocks_publication_after_durable_fence_begins () =
    let start = OriginalGitPublisherState.initial
    Assert.That(OriginalGitPublisherState.allowMutation start = Ok (), Is.True)
    let fenceId = Guid.NewGuid()
    let pending = OriginalGitPublisherState.beginFence fenceId start |> unwrap
    Assert.That(pending.Phase, Is.EqualTo(OriginalGitFixturePhase.FencePending fenceId))
    Assert.That(OriginalGitPublisherState.allowMutation pending = Error OriginalGitPublisherFailure.Fenced, Is.True)
    let finished = OriginalGitPublisherState.finishFence fenceId pending |> unwrap
    Assert.That(finished.Phase, Is.EqualTo(OriginalGitFixturePhase.Fenced(fenceId, 1L)))
    Assert.That(OriginalGitPublisherState.allowMutation finished = Error OriginalGitPublisherFailure.Fenced, Is.True)

[<Test>]
let fence_retry_has_one_identity () =
    let id = Guid.NewGuid()
    let pending = OriginalGitPublisherState.beginFence id OriginalGitPublisherState.initial |> unwrap
    Assert.That(OriginalGitPublisherState.beginFence id pending = Ok pending, Is.True)
    Assert.That(OriginalGitPublisherState.beginFence (Guid.NewGuid()) pending |> Result.isError, Is.True)
    let finished = OriginalGitPublisherState.finishFence id pending |> unwrap
    Assert.That(OriginalGitPublisherState.finishFence id finished = Ok finished, Is.True)

[<Test>]
let uncertain_external_effect_never_admits_after_recovery () =
    let op = Guid.NewGuid()
    let state = OriginalGitPublisherState.uncertain op OriginalGitPublisherState.initial
    Assert.That(state.Phase, Is.EqualTo(OriginalGitFixturePhase.RecoveryHold(Some op)))
    Assert.That(OriginalGitPublisherState.allowMutation state = Error OriginalGitPublisherFailure.UnresolvedOriginalEffect, Is.True)
    Assert.That(OriginalGitPublisherState.beginFence (Guid.NewGuid()) state |> Result.isError, Is.True)


open System.Collections.Generic
open System.IO
open System.Threading

let private oidA = String.replicate 40 "a"
let private oidB = String.replicate 40 "b"
let private digestA = String.replicate 64 "a"
let private digestB = String.replicate 64 "b"
let private digestC = String.replicate 64 "c"
let private accountId = Guid.NewGuid()
let private projectId1 = Guid.NewGuid()
let private projectId2 = Guid.NewGuid()
let private identity =
    { PublisherId = Guid.NewGuid(); DeviceId = Guid.NewGuid()
      ProtocolVersion = 1; CredentialGeneration = 1L }
let private projectTarget1 =
    { Purpose = OriginalGitRefPurpose.ProjectLedger
      AccountId = accountId; ProjectId = Some projectId1
      LedgerId = Guid.NewGuid(); LedgerEpoch = 1
      CanonicalRepositoryId = digestA
      ApprovedRemoteFingerprintSha256 = digestB
      GitRef = "refs/heads/jarvis-orchestration" }
let private projectTarget2 =
    { projectTarget1 with
        ProjectId = Some projectId2; LedgerId = Guid.NewGuid()
        CanonicalRepositoryId = digestB; ApprovedRemoteFingerprintSha256 = digestC }
let private accountTarget =
    { projectTarget1 with
        Purpose = OriginalGitRefPurpose.AccountLedger
        ProjectId = None
        LedgerId = Guid.NewGuid()
        CanonicalRepositoryId = digestC
        GitRef = "refs/heads/jarvis-account-admission" }
let private inventory = [projectTarget1; projectTarget2; accountTarget]
let private originalCas () =
    { Target = projectTarget1; Publisher = identity
      OperationId = Guid.NewGuid(); RequestSha256 = digestA
      ExpectedRemoteHead = Some oidA; ProposedRemoteHead = Some oidB
      SourceEffectOperationId = None }
let private originalBarrier () =
    { FenceOperationId = Guid.NewGuid()
      OperatorDecisionId = Guid.NewGuid()
      AccountId = accountId
      Targets = inventory }

let private withDisposableStore action =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-p1b-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(root) |> ignore
    File.WriteAllText(Path.Combine(root, OriginalGitPublisherStore.DisposableMarker),
                      OriginalGitPublisherStore.DisposableMarkerContent)
    try action (Path.Combine(root, "publisher.sqlite"))
    finally if Directory.Exists root then Directory.Delete(root, true)

type private InMemoryFixtureRemote(authorized: OriginalGitPublisherIdentity,
                                    approved: OriginalGitPublisherTarget list) =
    let heads = Dictionary<string * string, string option>()
    let key (t: OriginalGitPublisherTarget) = t.CanonicalRepositoryId, t.GitRef
    let mutable canVerify = true
    let mutable completeHistory = true
    let mutable denialInstalled = false
    let mutable faultAfterWrite = false
    let mutable typedStaleAfterWrite = false
    let mutable gateBeforeWrite: (ManualResetEventSlim * ManualResetEventSlim) option = None
    let mutable gateAfterWrite: (ManualResetEventSlim * ManualResetEventSlim) option = None
    let mutable casCalls = 0
    let waitAtGate (gate: (ManualResetEventSlim * ManualResetEventSlim) option) =
        match gate with
        | None -> ()
        | Some (entered, release) ->
            entered.Set()
            if not (release.Wait(15000)) then
                failwith "Fixture CAS synchronization timed out"
    do for target in approved do heads.[key target] <- Some oidA
    member _.CasCalls = casCalls
    member _.SetFaultAfterWrite value = faultAfterWrite <- value
    member _.SetTypedStaleAfterWrite value = typedStaleAfterWrite <- value
    member _.GateBeforeWrite(entered, release) = gateBeforeWrite <- Some(entered, release)
    member _.GateAfterWrite(entered, release) = gateAfterWrite <- Some(entered, release)
    member _.SetCoverage value = canVerify <- value
    member _.SetOriginalHistory value = completeHistory <- value
    member _.MoveHead target newHead = heads.[key target] <- newHead
    member _.Head target = heads.[key target]
    interface IOriginalGitPublisherFixtureRemote with
        member _.AuthenticatedIdentity() = Some authorized
        member _.TrustedInventory() = approved
        member _.ReadHead target =
            match heads.TryGetValue(key target) with
            | true, head -> Ok head
            | _ -> Error OriginalGitPublisherFailure.UntrustedAuthority
        member _.ExactRemoteCas operation =
            casCalls <- casCalls + 1
            if heads.[key operation.Target] <> operation.ExpectedRemoteHead then
                Error OriginalGitPublisherFailure.StaleRemoteHead
            else
                waitAtGate gateBeforeWrite
                heads.[key operation.Target] <- operation.ProposedRemoteHead
                waitAtGate gateAfterWrite
                if faultAfterWrite then failwith "Simulated lost remote response after ref advanced"
                if typedStaleAfterWrite then Error OriginalGitPublisherFailure.StaleRemoteHead
                else Ok ()
        member _.InstallFixtureRemoteDenial targets =
            if not canVerify || List.length targets <> approved.Length then
                Error OriginalGitPublisherFailure.UnknownCredentialChannel
            else
                denialInstalled <- true
                Ok ()
        member _.VerifyFixtureRemoteDenial targets =
            if denialInstalled && canVerify && List.length targets = approved.Length then Ok ()
            else Error OriginalGitPublisherFailure.UnknownCredentialChannel
        member _.ReadOriginalHistory() =
            if completeHistory then Ok(12L, 9L)
            else Error OriginalGitPublisherFailure.UnresolvedOriginalEffect

[<Test>]
let durable_publisher_cas_replay_and_wrong_identity_are_fail_closed () =
    withDisposableStore (fun path ->
        let host = InMemoryFixtureRemote(identity, inventory)
        let store = OriginalGitPublisherStore(path)
        let gateway = OriginalGitPublisherFixtureExecutor(store, host)
        let operation = originalCas ()
        let applied = gateway.PublishCas operation |> unwrap
        Assert.That(applied.NewHead, Is.EqualTo(Some oidB))
        Assert.That(host.Head projectTarget1, Is.EqualTo(Some oidB))
        Assert.That(gateway.PublishCas operation = Ok applied, Is.True)
        Assert.That(host.CasCalls, Is.EqualTo(1))
        match gateway.PublishCas { operation with RequestSha256 = digestB } with
        | Error OriginalGitPublisherFailure.ReusedOperationId -> ()
        | other -> Assert.Fail(sprintf "Changed UUID envelope unexpectedly accepted: %A" other)
        match gateway.PublishCas { operation with Publisher = { identity with DeviceId = Guid.NewGuid() } } with
        | Error OriginalGitPublisherFailure.UntrustedAuthority -> ()
        | other -> Assert.Fail(sprintf "Untrusted caller accepted: %A" other))

[<Test>]
let interrupted_prepared_row_restarts_as_non_replayable_recovery_hold () =
    withDisposableStore (fun path ->
        let operationId = Guid.NewGuid()
        let first = OriginalGitPublisherStore(path)
        match first.Prepare(operationId, digestA) with
        | Ok(OriginalGitFixturePrepare.Fresh 1L) -> ()
        | other -> Assert.Fail(sprintf "No durable prepared entry: %A" other)
        let restarted = OriginalGitPublisherStore(path)
        Assert.That(restarted.ReadState().Phase,
                    Is.EqualTo(OriginalGitFixturePhase.RecoveryHold(Some operationId)))
        match restarted.Prepare(operationId, digestA) with
        | Ok(OriginalGitFixturePrepare.Replay row) ->
            Assert.That(row.Status, Is.EqualTo(OriginalGitFixtureCasStatus.Unknown))
        | other -> Assert.Fail(sprintf "Prepared UUID improperly retried: %A" other)
        Assert.That(restarted.Prepare(Guid.NewGuid(), digestB) |> Result.isError, Is.True))

[<Test>]
let unresolved_prepared_uuid_blocks_fresh_admission_without_restart () =
    withDisposableStore (fun path ->
        let store = OriginalGitPublisherStore(path)
        let firstId = Guid.NewGuid()
        match store.Prepare(firstId, digestA) with
        | Ok(OriginalGitFixturePrepare.Fresh _) -> ()
        | other -> Assert.Fail(sprintf "First CAS was not prepared: %A" other)
        // A prepared row is not a known completed effect. The persisted phase
        // is still Active, but a second UUID must never enter remote CAS.
        Assert.That(store.ReadState().Phase, Is.EqualTo(OriginalGitFixturePhase.Active))
        match store.Prepare(Guid.NewGuid(), digestB) with
        | Error OriginalGitPublisherFailure.UnresolvedOriginalEffect -> ()
        | other -> Assert.Fail(sprintf "Second UUID escaped unresolved journal: %A" other)
        match store.Prepare(firstId, digestA) with
        | Ok(OriginalGitFixturePrepare.Replay row) ->
            Assert.That(row.Status, Is.EqualTo(OriginalGitFixtureCasStatus.Prepared))
        | other -> Assert.Fail(sprintf "Original UUID replay changed: %A" other))

[<Test>]
let failed_unknown_write_poison_live_gateway_and_hold_after_restart () =
    withDisposableStore (fun path ->
        let mutable failNextUnknownWrite = true
        let store =
            OriginalGitPublisherStore(
                path,
                beforeMarkUnknownWrite = (fun () ->
                    if failNextUnknownWrite then
                        failNextUnknownWrite <- false
                        invalidOp "Injected transient unknown journal failure"))
        let host = InMemoryFixtureRemote(identity, inventory)
        host.SetFaultAfterWrite true
        let gateway = OriginalGitPublisherFixtureExecutor(store, host)
        let first = originalCas ()
        match gateway.PublishCas first with
        | Error(OriginalGitPublisherFailure.OutcomeUnknown id) when id = first.OperationId -> ()
        | other -> Assert.Fail(sprintf "Ambiguous first CAS outcome was promoted: %A" other)
        Assert.That(host.Head projectTarget1, Is.EqualTo(Some oidB))
        Assert.That((store.ReadOperation first.OperationId |> Option.get).Status,
                    Is.EqualTo(OriginalGitFixtureCasStatus.Prepared))
        // The injected failure was transient; absence of a durable Unknown
        // transition must nevertheless poison this still-running publisher.
        Assert.That(failNextUnknownWrite, Is.False)
        Assert.That(store.ReadState().Phase,
                    Is.EqualTo(OriginalGitFixturePhase.RecoveryHold(Some first.OperationId)))
        host.SetFaultAfterWrite false
        let second =
            { originalCas() with ExpectedRemoteHead = Some oidB; ProposedRemoteHead = Some oidA }
        match gateway.PublishCas second with
        | Error OriginalGitPublisherFailure.UnresolvedOriginalEffect -> ()
        | other -> Assert.Fail(sprintf "Fresh CAS escaped live poison: %A" other)
        Assert.That(host.CasCalls, Is.EqualTo(1))
        let restartedStore = OriginalGitPublisherStore(path)
        let restarted = OriginalGitPublisherFixtureExecutor(restartedStore, host)
        Assert.That(restartedStore.ReadState().Phase,
                    Is.EqualTo(OriginalGitFixturePhase.RecoveryHold(Some first.OperationId)))
        Assert.That((restartedStore.ReadOperation first.OperationId |> Option.get).Status,
                    Is.EqualTo(OriginalGitFixtureCasStatus.Unknown))
        match restarted.PublishCas second with
        | Error OriginalGitPublisherFailure.UnresolvedOriginalEffect -> ()
        | other -> Assert.Fail(sprintf "Fresh CAS escaped restart recovery: %A" other)
        Assert.That(host.CasCalls, Is.EqualTo(1)))

[<Test>]
let lost_response_after_remote_advance_remains_unknown_after_restart () =
    withDisposableStore (fun path ->
        let host = InMemoryFixtureRemote(identity, inventory)
        host.SetFaultAfterWrite true
        let store = OriginalGitPublisherStore(path)
        let gateway = OriginalGitPublisherFixtureExecutor(store, host)
        let operation = originalCas ()
        match gateway.PublishCas operation with
        | Error(OriginalGitPublisherFailure.OutcomeUnknown id) when id = operation.OperationId -> ()
        | other -> Assert.Fail(sprintf "Lost response was promoted: %A" other)
        Assert.That(host.Head projectTarget1, Is.EqualTo(Some oidB))
        let second = OriginalGitPublisherStore(path)
        let resumed = OriginalGitPublisherFixtureExecutor(second, host)
        match resumed.PublishCas operation with
        | Error(OriginalGitPublisherFailure.OutcomeUnknown _) -> ()
        | other -> Assert.Fail(sprintf "Unknown UUID was republished: %A" other)
        Assert.That(host.CasCalls, Is.EqualTo(1))
        Assert.That(second.ReadState().Phase,
                    Is.EqualTo(OriginalGitFixturePhase.RecoveryHold(Some operation.OperationId)))
        Assert.That(resumed.AcquireExclusiveFixtureBarrier(originalBarrier()) |> Result.isError, Is.True))

[<Test>]
let typed_stale_error_after_remote_advance_must_be_unknown_and_hold_fence () =
    withDisposableStore (fun path ->
        let host = InMemoryFixtureRemote(identity, inventory)
        host.SetTypedStaleAfterWrite true
        let store = OriginalGitPublisherStore(path)
        let gateway = OriginalGitPublisherFixtureExecutor(store, host)
        let operation = originalCas ()
        match gateway.PublishCas operation with
        | Error(OriginalGitPublisherFailure.OutcomeUnknown id) when id = operation.OperationId -> ()
        | other -> Assert.Fail(sprintf "Ambiguous typed CAS error incorrectly finalized: %A" other)
        Assert.That(host.Head projectTarget1, Is.EqualTo(Some oidB))
        let stored = store.ReadOperation(operation.OperationId) |> Option.get
        Assert.That(stored.Status, Is.EqualTo(OriginalGitFixtureCasStatus.Unknown))
        Assert.That(store.ReadState().Phase,
                    Is.EqualTo(OriginalGitFixturePhase.RecoveryHold(Some operation.OperationId)))
        Assert.That(gateway.AcquireExclusiveFixtureBarrier(originalBarrier()) |> Result.isError, Is.True))

[<Test>]
let fixture_barrier_is_durable_and_reinspection_rejects_forged_receipts () =
    withDisposableStore (fun path ->
        let host = InMemoryFixtureRemote(identity, inventory)
        let gateway = OriginalGitPublisherFixtureExecutor(OriginalGitPublisherStore(path), host)
        let request = originalBarrier ()
        let receipt = gateway.AcquireExclusiveFixtureBarrier request |> unwrap
        Assert.That(receipt.ExactTargets.Length, Is.EqualTo(3))
        Assert.That(receipt.FenceEpoch, Is.EqualTo(1L))
        Assert.That(receipt.OriginalPendingIntentHighWater, Is.EqualTo(12L))
        let reopened = OriginalGitPublisherFixtureExecutor(OriginalGitPublisherStore(path), host)
        Assert.That(reopened.AcquireExclusiveFixtureBarrier request = Ok receipt, Is.True)
        Assert.That(reopened.ReinspectFixtureBarrier receipt = Ok receipt, Is.True)
        let forged = { receipt with RemoteWitnessSha256 = digestA }
        Assert.That(reopened.ReinspectFixtureBarrier forged |> Result.isError, Is.True)
        Assert.That(reopened.PublishCas(originalCas()) = Error OriginalGitPublisherFailure.Fenced, Is.True)
        host.MoveHead projectTarget2 (Some oidB)
        Assert.That(reopened.ReinspectFixtureBarrier receipt |> Result.isError, Is.True))

[<Test>]
let fence_replay_with_changed_operator_decision_is_rejected_after_restart () =
    withDisposableStore (fun path ->
        let host = InMemoryFixtureRemote(identity, inventory)
        let request = originalBarrier ()
        let gateway = OriginalGitPublisherFixtureExecutor(OriginalGitPublisherStore(path), host)
        let first = gateway.AcquireExclusiveFixtureBarrier request |> unwrap
        let restarted = OriginalGitPublisherFixtureExecutor(OriginalGitPublisherStore(path), host)
        Assert.That(restarted.AcquireExclusiveFixtureBarrier request = Ok first, Is.True)
        let changed = { request with OperatorDecisionId = Guid.NewGuid() }
        match restarted.AcquireExclusiveFixtureBarrier changed with
        | Error OriginalGitPublisherFailure.ReusedOperationId -> ()
        | other -> Assert.Fail(sprintf "Changed operator decision replay accepted: %A" other)
        Assert.That(restarted.ReinspectFixtureBarrier first = Ok first, Is.True))

[<Test>]
let pending_fence_replay_with_changed_operator_remains_refused_after_restart () =
    withDisposableStore (fun path ->
        let host = InMemoryFixtureRemote(identity, inventory)
        host.SetOriginalHistory false
        let request = originalBarrier ()
        let store = OriginalGitPublisherStore(path)
        let gateway = OriginalGitPublisherFixtureExecutor(store, host)
        Assert.That(gateway.AcquireExclusiveFixtureBarrier request |> Result.isError, Is.True)
        Assert.That(store.ReadState().Phase,
                    Is.EqualTo(OriginalGitFixturePhase.FencePending request.FenceOperationId))
        let restarted = OriginalGitPublisherFixtureExecutor(OriginalGitPublisherStore(path), host)
        let changed = { request with OperatorDecisionId = Guid.NewGuid() }
        match restarted.AcquireExclusiveFixtureBarrier changed with
        | Error OriginalGitPublisherFailure.ReusedOperationId -> ()
        | other -> Assert.Fail(sprintf "Changed pending fence operator was accepted: %A" other))

[<Test>]
let partial_inventory_or_missing_original_history_never_releases_barrier () =
    withDisposableStore (fun path ->
        let host = InMemoryFixtureRemote(identity, inventory)
        host.SetOriginalHistory false
        let store = OriginalGitPublisherStore(path)
        let gateway = OriginalGitPublisherFixtureExecutor(store, host)
        let request = originalBarrier ()
        Assert.That(gateway.AcquireExclusiveFixtureBarrier request |> Result.isError, Is.True)
        Assert.That(store.ReadState().Phase,
                    Is.EqualTo(OriginalGitFixturePhase.FencePending request.FenceOperationId))
        Assert.That(gateway.PublishCas(originalCas()) = Error OriginalGitPublisherFailure.Fenced, Is.True))
    withDisposableStore (fun path ->
        let host = InMemoryFixtureRemote(identity, inventory)
        host.SetCoverage false
        let gateway = OriginalGitPublisherFixtureExecutor(OriginalGitPublisherStore(path), host)
        Assert.That(gateway.AcquireExclusiveFixtureBarrier(originalBarrier()) |> Result.isError, Is.True))
    withDisposableStore (fun path ->
        let host = InMemoryFixtureRemote(identity, inventory)
        let gateway = OriginalGitPublisherFixtureExecutor(OriginalGitPublisherStore(path), host)
        let request = { originalBarrier() with Targets = [accountTarget; projectTarget1] }
        Assert.That(gateway.AcquireExclusiveFixtureBarrier request |> Result.isError, Is.True))


[<Test>]
let source_effect_ref_missing_from_fence_or_misclassified_fails_closed () =
    withDisposableStore (fun path ->
        let sourceEffect =
            { projectTarget1 with
                Purpose = OriginalGitRefPurpose.SourceEffect
                LedgerId = Guid.NewGuid()
                GitRef = "refs/heads/jarvis-original-source-effects" }
        let trusted = sourceEffect :: inventory
        let host = InMemoryFixtureRemote(identity, trusted)
        let gateway = OriginalGitPublisherFixtureExecutor(OriginalGitPublisherStore(path), host)
        match gateway.AcquireExclusiveFixtureBarrier(originalBarrier()) with
        | Error OriginalGitPublisherFailure.UnknownCredentialChannel -> ()
        | other -> Assert.Fail(sprintf "Incomplete source-effect fence accepted: %A" other)
        let misclassified =
            { sourceEffect with Purpose = OriginalGitRefPurpose.ProjectLedger }
        match OriginalGitPublisherAdmission.validateTargetAgainstTrustedInventory trusted misclassified with
        | Error(OriginalGitPublisherFailure.InvalidEnvelope "ScopeOrRefMismatch") -> ()
        | other -> Assert.Fail(sprintf "Source ref classification was not rejected: %A" other)
        let invalidSourceCas = { originalCas() with Target = sourceEffect }
        match gateway.PublishCas invalidSourceCas with
        | Error(OriginalGitPublisherFailure.InvalidEnvelope "SourceEffectJournalIdentityMissing") -> ()
        | other -> Assert.Fail(sprintf "Missing source-effect journal identity was accepted: %A" other))

[<Test>]
let in_flight_pre_write_cas_is_accounted_before_barrier_positive () =
    withDisposableStore (fun path ->
        let host = InMemoryFixtureRemote(identity, inventory)
        use entered = new ManualResetEventSlim(false)
        use release = new ManualResetEventSlim(false)
        use barrierStarted = new ManualResetEventSlim(false)
        host.GateBeforeWrite(entered, release)
        let store = OriginalGitPublisherStore(path)
        let gateway = OriginalGitPublisherFixtureExecutor(store, host)
        let operation = originalCas ()
        let casJob = System.Threading.Tasks.Task.Run(fun () -> gateway.PublishCas operation)
        try
            Assert.That(entered.Wait(10000), Is.True)
            let barrierJob =
                System.Threading.Tasks.Task.Run(fun () ->
                    barrierStarted.Set()
                    gateway.AcquireExclusiveFixtureBarrier(originalBarrier()))
            Assert.That(barrierStarted.Wait(10000), Is.True)
            Assert.That(barrierJob.Wait(50), Is.False,
                        "Barrier must not complete while remote CAS is in flight")
            release.Set()
            Assert.That(casJob.Wait(15000), Is.True)
            Assert.That(barrierJob.Wait(15000), Is.True)
            Assert.That(casJob.Result |> Result.isOk, Is.True)
            let stored = store.ReadOperation(operation.OperationId) |> Option.get
            Assert.That(stored.Status, Is.EqualTo(OriginalGitFixtureCasStatus.Applied))
            Assert.That(barrierJob.Result |> Result.isOk, Is.True)
            Assert.That(host.Head projectTarget1, Is.EqualTo(Some oidB))
        finally release.Set())

[<Test>]
let in_flight_post_write_typed_error_blocks_barrier_positive () =
    withDisposableStore (fun path ->
        let host = InMemoryFixtureRemote(identity, inventory)
        use entered = new ManualResetEventSlim(false)
        use release = new ManualResetEventSlim(false)
        use barrierStarted = new ManualResetEventSlim(false)
        host.GateAfterWrite(entered, release)
        host.SetTypedStaleAfterWrite true
        let store = OriginalGitPublisherStore(path)
        let gateway = OriginalGitPublisherFixtureExecutor(store, host)
        let operation = originalCas ()
        let casJob = System.Threading.Tasks.Task.Run(fun () -> gateway.PublishCas operation)
        try
            Assert.That(entered.Wait(10000), Is.True)
            let barrierJob =
                System.Threading.Tasks.Task.Run(fun () ->
                    barrierStarted.Set()
                    gateway.AcquireExclusiveFixtureBarrier(originalBarrier()))
            Assert.That(barrierStarted.Wait(10000), Is.True)
            Assert.That(barrierJob.Wait(50), Is.False,
                        "Barrier must wait for ambiguous external CAS outcome")
            release.Set()
            Assert.That(casJob.Wait(15000), Is.True)
            Assert.That(barrierJob.Wait(15000), Is.True)
            match casJob.Result with
            | Error(OriginalGitPublisherFailure.OutcomeUnknown id) when id = operation.OperationId -> ()
            | other -> Assert.Fail(sprintf "In-flight ambiguous CAS was finalized: %A" other)
            Assert.That(barrierJob.Result |> Result.isError, Is.True)
            let stored = store.ReadOperation(operation.OperationId) |> Option.get
            Assert.That(stored.Status, Is.EqualTo(OriginalGitFixtureCasStatus.Unknown))
            Assert.That(store.ReadState().Phase,
                        Is.EqualTo(OriginalGitFixturePhase.RecoveryHold(Some operation.OperationId)))
        finally release.Set())

open System.Diagnostics
open System.Threading.Tasks

let private runGit directory (arguments: string list) =
    let info = ProcessStartInfo("git")
    info.WorkingDirectory <- directory
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    for argument in arguments do info.ArgumentList.Add(argument)
    use proc = Process.Start(info)
    let output = proc.StandardOutput.ReadToEndAsync()
    let error = proc.StandardError.ReadToEndAsync()
    if not (proc.WaitForExit(30000)) then
        proc.Kill(true)
        failwithf "Git fixture timed out: %A" arguments
    proc.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult()

let private gitSuccess directory arguments =
    let code, stdout, stderr = runGit directory arguments
    if code <> 0 then failwithf "Git fixture failed %A: %s %s" arguments stdout stderr
    stdout.Trim()

let private makeGitRemote root name (target: OriginalGitPublisherTarget) =
    let remote = Path.Combine(root, name + ".git")
    gitSuccess root ["init"; "--bare"; "--initial-branch=main"; remote] |> ignore
    let seed = Path.Combine(root, name + "-seed")
    gitSuccess root ["clone"; remote; seed] |> ignore
    gitSuccess seed ["config"; "user.name"; "publisher-fixture"] |> ignore
    gitSuccess seed ["config"; "user.email"; "publisher@example.invalid"] |> ignore
    gitSuccess seed ["commit"; "--allow-empty"; "-m"; "seed"] |> ignore
    let seedCommit = gitSuccess seed ["rev-parse"; "HEAD"]
    gitSuccess seed ["push"; "origin"; "HEAD:refs/heads/main"; seedCommit + ":" + target.GitRef] |> ignore
    gitSuccess seed ["commit"; "--allow-empty"; "-m"; "publisher-proposed-next"] |> ignore
    let proposed = gitSuccess seed ["rev-parse"; "HEAD"]
    // Stage an object in the disposable remote without moving the original
    // protected ref. The publisher below uses direct exact update-ref CAS.
    gitSuccess seed ["push"; "origin"; "HEAD:refs/heads/publisher-stage"] |> ignore
    let stale = Path.Combine(root, name + "-uncooperative")
    gitSuccess root ["clone"; remote; stale] |> ignore
    gitSuccess stale ["config"; "user.name"; "stale-fixture"] |> ignore
    gitSuccess stale ["config"; "user.email"; "stale@example.invalid"] |> ignore
    gitSuccess stale ["commit"; "--allow-empty"; "-m"; "before-fence-stale"] |> ignore
    gitSuccess stale ["push"; "origin"; "HEAD:" + target.GitRef] |> ignore
    let old = gitSuccess root ["--git-dir=" + remote; "rev-parse"; target.GitRef]
    remote, stale, old, proposed

type private BareGitFixtureRemote(
        root: string,
        authorized: OriginalGitPublisherIdentity,
        approved: OriginalGitPublisherTarget list,
        directories: (OriginalGitPublisherTarget * string) list) =
    let byRepository =
        directories |> List.map (fun (target, path) -> target.CanonicalRepositoryId, path) |> Map.ofList
    let pathFor target =
        match Map.tryFind target.CanonicalRepositoryId byRepository with
        | Some path -> path
        | None -> failwith "Untrusted repository inventory"
    let gitHead target =
        let path = pathFor target
        let code, stdout, _ =
            runGit root ["--git-dir=" + path; "show-ref"; "--verify"; target.GitRef]
        if code = 0 then Ok(Some(stdout.Split(' ')[0]))
        elif code = 1 then Ok None
        else Error OriginalGitPublisherFailure.UntrustedAuthority
    let hookText = "#!/bin/sh\necho ORIGINAL_REMOTE_FENCED >&2\nexit 1\n"
    interface IOriginalGitPublisherFixtureRemote with
        member _.AuthenticatedIdentity() = Some authorized
        member _.TrustedInventory() = approved
        member _.ReadHead target = gitHead target
        member _.ExactRemoteCas operation =
            let path = pathFor operation.Target
            let old = operation.ExpectedRemoteHead |> Option.defaultValue (String.replicate 40 "0")
            let proposed = operation.ProposedRemoteHead |> Option.defaultValue (String.replicate 40 "0")
            let code, _, _ =
                runGit root ["--git-dir=" + path; "update-ref"; operation.Target.GitRef; proposed; old]
            if code = 0 then Ok ()
            // Arbitrary nonzero Git exit is not a no-publication proof:
            // even a terminated process can leave the external effect uncertain.
            else Error(OriginalGitPublisherFailure.OutcomeUnknown operation.OperationId)
        member _.InstallFixtureRemoteDenial targets =
            for target in targets do
                let path = pathFor target
                let hook = Path.Combine(path, "hooks", "pre-receive")
                File.WriteAllText(hook, hookText)
                File.SetUnixFileMode(hook,
                    UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
            Ok ()
        member _.VerifyFixtureRemoteDenial targets =
            let verified =
                targets
                |> List.forall (fun target ->
                    let hook = Path.Combine(pathFor target, "hooks", "pre-receive")
                    File.Exists hook && File.ReadAllText(hook) = hookText &&
                    (File.GetUnixFileMode(hook) &&& UnixFileMode.UserExecute) <> enum<UnixFileMode>(0))
            // A hook proves only ordinary receive-pack denial. It neither
            // drains an already-running push nor excludes direct filesystem
            // actors, so no positive all-channel barrier is authorized.
            if not verified then Error OriginalGitPublisherFailure.UnknownCredentialChannel
            else Error OriginalGitPublisherFailure.UntrustedAuthority
        member _.ReadOriginalHistory() =
            // FIXTURE high-water only, not retroactive authenticated coverage
            // of real original client journals and transport populations.
            Ok(0L, 0L)

[<Test>]
let two_project_one_account_bare_git_gateway_and_stale_push_denial () =
    if not (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) then
        Assert.Ignore("POSIX disposable Git receive-pack fixture")
    let root = Path.Combine(Path.GetTempPath(), "jarvis-p1b-bare-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    try
        let fixtures =
            inventory
            |> List.mapi (fun index target ->
                let remote, stale, old, proposed =
                    makeGitRemote root ("remote-" + string index) target
                target, remote, stale, old, proposed)
        let paths = fixtures |> List.map (fun (target, remote, _, _, _) -> target, remote)
        let host = BareGitFixtureRemote(root, identity, inventory, paths)
        File.WriteAllText(Path.Combine(root, OriginalGitPublisherStore.DisposableMarker),
                          OriginalGitPublisherStore.DisposableMarkerContent)
        let db = OriginalGitPublisherStore(Path.Combine(root, "fixture.sqlite"))
        let gateway = OriginalGitPublisherFixtureExecutor(db, host)
        let project, _, _, old, proposed = fixtures.Head
        let operation =
            { originalCas() with
                Target = project; ExpectedRemoteHead = Some old
                ProposedRemoteHead = Some proposed }
        let result = gateway.PublishCas operation |> unwrap
        Assert.That(result.NewHead, Is.EqualTo(Some proposed))
        let projectPath = let _, path, _, _, _ = fixtures.Head in path
        let applied = gitSuccess root ["--git-dir=" + projectPath; "rev-parse"; project.GitRef]
        Assert.That(applied, Is.EqualTo(proposed))
        let request = originalBarrier ()
        Assert.That(gateway.AcquireExclusiveFixtureBarrier request |> Result.isError, Is.True)
        Assert.That(db.ReadState().Phase,
                    Is.EqualTo(OriginalGitFixturePhase.FencePending request.FenceOperationId))
        for target, remote, stale, _, _ in fixtures do
            gitSuccess stale ["commit"; "--allow-empty"; "-m"; "after-fence-stale"] |> ignore
            for gitArgs in [
                ["push"; "origin"; "+HEAD:" + target.GitRef]
                ["push"; "origin"; "+HEAD:refs/heads/unregistered-alternate"]
                ["push"; "origin"; "+HEAD:refs/tags/bypass-attempt"]
                ["push"; "--delete"; "origin"; target.GitRef]
            ] do
                let code, _, stderr = runGit stale gitArgs
                Assert.That(code, Is.Not.EqualTo(0), sprintf "Stale Git effect escaped %A" gitArgs)
                Assert.That(stderr, Does.Contain("ORIGINAL_REMOTE_FENCED"))
            let remoteHead = gitSuccess root ["--git-dir=" + remote; "rev-parse"; target.GitRef]
            let expected = if target = project then proposed else
                               let _, _, _, original, _ =
                                   fixtures |> List.find (fun (t, _, _, _, _) -> t = target)
                               original
            Assert.That(remoteHead, Is.EqualTo(expected))
        // This direct filesystem bypass is deliberately NOT blocked by a
        // receive-pack hook. Hence the barrier remains pending and a hosted
        // credential/transport witness is HARD NO-GO.
        let target, remote, _, previous, _ = fixtures.Head
        gitSuccess root ["--git-dir=" + remote; "update-ref"; target.GitRef; previous] |> ignore
        Assert.That(db.ReadState().Phase,
                    Is.EqualTo(OriginalGitFixturePhase.FencePending request.FenceOperationId))
    finally
        if Directory.Exists root then Directory.Delete(root, true)

[<Test>]
let serialized_concurrent_cas_has_at_most_one_winner () =
    withDisposableStore (fun path ->
        let host = InMemoryFixtureRemote(identity, inventory)
        let gateway = OriginalGitPublisherFixtureExecutor(OriginalGitPublisherStore(path), host)
        let original = originalCas ()
        let attempts =
            [| for i in 1..8 ->
                Task.Run(fun () -> gateway.PublishCas { original with OperationId = Guid.NewGuid() }) |]
        Task.WaitAll(attempts |> Array.map (fun task -> task :> Task))
        let winners = attempts |> Array.filter (fun attempt -> Result.isOk attempt.Result)
        Assert.That(winners.Length, Is.EqualTo(1))
        Assert.That(host.CasCalls, Is.EqualTo(1)))
