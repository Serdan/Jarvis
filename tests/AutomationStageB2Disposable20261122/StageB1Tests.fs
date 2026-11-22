module AutomationStageB2Disposable20261122.B1VerifiedTests

open System
open System.IO
open AutomationStageB2Disposable20261122.Tests
open Client.LedgerStore
open Client.NativeProjectEffectFence
open Client.NativeSchedulerAccountReservation
open NUnit.Framework

let private receipt (binding: LedgerBinding) sourceOid : FakeAccountReservationReceipt =
    { Marker = "STAGE_B_DISPOSABLE_FAKE_ACCOUNT_RECEIPT/v1"
      AccountLedgerId = Guid.NewGuid().ToString("D")
      AccountEpoch = 1; AccountHead = sourceOid
      OriginalReservationOperationId = Guid.NewGuid().ToString("D")
      ReservationId = Guid.NewGuid().ToString("D")
      ProjectId = binding.ProjectId.ToString("D"); Kind = "Worker" }

let private claimFor (binding: LedgerBinding) candidate sourceOid : PreSendClaim =
    { OperationId = Guid.NewGuid()
      ExpectedProjectHead = (LedgerStore.fetch binding |> ok).Commit
      Account = receipt binding sourceOid; Candidate = candidate
      DispatchId = Guid.NewGuid(); EffectId = Guid.NewGuid()
      DeviceId = "disposable-w16"; WindowRegistration = "fake-window" }

let private secondClient (binding: LedgerBinding) =
    let root = Directory.GetParent(binding.RepositoryPath).FullName
    let path = Path.Combine(root, "project-b")
    git root [ "clone"; binding.ApprovedFetchUrl; path ] |> ignore
    git path [ "config"; "user.name"; "Disposable B" ] |> ignore
    git path [ "config"; "user.email"; "b@example.test" ] |> ignore
    { binding with RepositoryPath = path }

[<TestFixture>]
type B1FailClosedBoundaryTests() =
    [<Test>]
    member _.RealStartedWorkerAttemptIsRejectedWithoutAProjectMutation() =
        fixture "Started" (fun pa candidate sourceOid ->
            let claim = claimFor pa candidate sourceOid
            let before = (LedgerStore.fetch pa |> ok).Commit
            let denied = Client.NativeProjectEffectFence.claim pa claim
            Assert.That(Result.isError denied, Is.True,
                sprintf "Started lease wrongly bridged into B1: %A" denied)
            Assert.That((LedgerStore.fetch pa |> ok).Commit, Is.EqualTo(before),
                "Rejected B1 claim cannot write a project effect marker"))

    [<Test>]
    member _.ReadyOnlyB1FakePortIsOnceOnlyAndTerminalIsDurableAcrossClients() =
        fixture "Ready" (fun pa candidate sourceOid ->
            let pb = secondClient pa
            let claim = claimFor pa candidate sourceOid
            let first = Client.NativeProjectEffectFence.claim pa claim |> ok
            Assert.That(first.AlreadyApplied, Is.False)
            let duplicate = Client.NativeProjectEffectFence.claim pb claim |> ok
            Assert.That(duplicate.AlreadyApplied, Is.True)
            Assert.That(Client.NativeProjectEffectFence.claim pb
                            { claim with DeviceId = "spoofed-device" }
                        |> Result.isError, Is.True,
                "Altered original operation UUID must not inherit receipt")
            Assert.That(Client.NativeProjectEffectFence.claim pb
                            { claim with OperationId = Guid.NewGuid(); DispatchId = Guid.NewGuid(); EffectId = Guid.NewGuid() }
                        |> Result.isError, Is.True,
                "Different UUID for same canonical attempt cannot create another effect")
            let mutable fakeCalls = 0
            let proof : FakeProviderTerminal =
                { Marker = "STAGE_B_DISPOSABLE_FAKE_PROVIDER/v1"
                  EffectId = claim.EffectId
                  ProviderSessionId = "fake-session-only"
                  Status = "Terminal" }
            let fakePort _ =
                fakeCalls <- fakeCalls + 1
                Ok proof
            let startOp = Guid.NewGuid()
            Client.NativeProjectEffectFence.dispatchFakePort pb candidate claim.EffectId startOp fakePort
            |> ok |> ignore
            Assert.That(fakeCalls, Is.EqualTo(1))
            Assert.That(Client.NativeProjectEffectFence.dispatchFakePort pa candidate claim.EffectId
                            startOp fakePort |> Result.isError, Is.True,
                        "Replayed/lost acknowledgment must not call fake provider twice")
            Assert.That(fakeCalls, Is.EqualTo(1))
            let fakeUnknown = { proof with Status = "Unknown" }
            Assert.That(Client.NativeProjectEffectFence.recordFakeTerminal pb candidate claim.EffectId
                            (Guid.NewGuid()) fakeUnknown |> Result.isError, Is.True)
            let fakeForeign = { proof with EffectId = Guid.NewGuid() }
            Assert.That(Client.NativeProjectEffectFence.recordFakeTerminal pa candidate claim.EffectId
                            (Guid.NewGuid()) fakeForeign |> Result.isError, Is.True)
            let _, unresolved = Client.NativeProjectEffectFence.observe pb candidate |> ok
            Assert.That(unresolved.Status, Is.EqualTo("Dispatching"),
                "Unknown and foreign terminal may not release the occupied fence")
            let terminalOp = Guid.NewGuid()
            Client.NativeProjectEffectFence.recordFakeTerminal pa candidate claim.EffectId terminalOp proof
            |> ok |> ignore
            let _, final = Client.NativeProjectEffectFence.observe pb candidate |> ok
            Assert.That(final.Status, Is.EqualTo("VerifiedFakeTerminal"))
            Assert.That(final.TerminalOperationId, Is.EqualTo(terminalOp))
            Assert.That(fakeCalls, Is.EqualTo(1))
            Assert.That(Client.NativeProjectEffectFence.recordFakeTerminal pb candidate claim.EffectId
                            (Guid.NewGuid()) proof |> Result.isError, Is.True,
                        "Late duplicate terminal cannot become another terminal")
            Assert.That(Client.NativeProjectEffectFence.recordFakeNoSend pb candidate claim.EffectId
                            (Guid.NewGuid())
                            { Marker = "STAGE_B_DISPOSABLE_FAKE_NO_SEND/v1"; Reason = "browser tab closed" }
                        |> Result.isError, Is.True,
                        "Closed tab after dispatch is not a verified no-send"))

    [<Test>]
    member _.ProductionNativeReservationEntrypointRemainsExplicitlyDefaultOff() =
        let account : Client.AccountCapacityLedger.AccountBinding =
            { RepositoryPath = "/nonexistent-disposable"
              RemoteName = "origin"; ApprovedFetchUrl = "/nonexistent"
              ApprovedPushUrl = "/nonexistent"
              AccountId = Guid.NewGuid(); LedgerId = Guid.NewGuid(); Epoch = 1 }
        let denied = NativeSchedulerAccountReservation.reserve account Unchecked.defaultof<NativeDispatchCandidate>
        Assert.That(Result.isError denied, Is.True,
            "No caller-shaped credential may enable production account reservation")
