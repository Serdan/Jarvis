module AutomationProviderHostTrust20261122.ContractTests

open System
open NUnit.Framework
open Client.NativeSchedulerAccountReservation
open Client.NativeProjectEffectFence
open Client.AutomationNativeProviderTrustContract


[<TestFixture>]
type ContractFixture() =
    let guid (text: string) = Guid.Parse text
    let accountId = "d1356132-22a1-4990-874c-192e0c357001"
    let ledgerId = "e1356132-22a1-4990-874c-192e0c357002"
    let dispatch = "a1356132-22a1-4990-874c-192e0c357003"
    let effect = "b1356132-22a1-4990-874c-192e0c357004"
    let original = "c1356132-22a1-4990-874c-192e0c357005"
    let terminal = "f1356132-22a1-4990-874c-192e0c357006"
    let head = String.replicate 40 "a"
    let oid = String.replicate 40 "b"
    let digest = String.replicate 64 "c"
    let repository = String.replicate 64 "d"
    let attemptId = String.replicate 32 "e"
    let candidate =
        { DispatchId = dispatch; EffectOperationId = effect; Kind = "Worker"
          ProjectId = accountId; ProjectLedgerId = ledgerId; ProjectEpoch = 1
          OwnerAgentId = "Worker 12"; TaskId = "task-one"; AttemptId = attemptId
          AssignmentGeneration = 7L; SourceRepositoryId = repository
          SourceBaseOid = head; SourceRef = "refs/heads/frozen-work"
          SourceLeaseSha256 = digest; ReviewId = ""; ReviewVersion = 0L
          ReviewedHeadOid = ""; ReviewRef = ""; ClaimOperationId = "" }
    let reserved =
        { CanonicalKey = digest; OriginalOperationId = original
          Candidate = candidate; Status = "Reserved"; Generation = 1L
          ProjectFenceOid = ""; ProviderSessionId = ""; TerminalOperationId = "" }
    let observation =
        { ExpectedCandidate = candidate; Reservation = reserved
          AccountHead = head; ExpectedGeneration = 1L; CanonicalWorkerPhase = "Started" }
    let fakeReceipt =
        { Marker = "STAGE_B_DISPOSABLE_FAKE_ACCOUNT_RECEIPT/v1"
          AccountLedgerId = ledgerId; AccountEpoch = 1; AccountHead = head
          OriginalReservationOperationId = original; ReservationId = "fake-only"
          ProjectId = accountId; Kind = "Worker" }
    let fakeCandidate =
        { Kind = "Worker"; TaskId = candidate.TaskId; AttemptId = candidate.AttemptId
          ReviewId = ""; OwnerAgentId = candidate.OwnerAgentId; AssignmentGeneration = 7
          ReviewVersion = 0; SourceRepositoryId = repository; SourceRef = candidate.SourceRef
          BaseCommit = head; TargetRef = "refs/heads/main"; ReviewRef = ""; ReviewedHeadOid = "" }
    let fakeClaim =
        { OperationId = guid "11356132-22a1-4990-874c-192e0c357001"
          ExpectedProjectHead = head; Account = fakeReceipt; Candidate = fakeCandidate
          DispatchId = guid dispatch; EffectId = guid effect
          DeviceId = "test-device"; WindowRegistration = "fake-window" }
    let fakeProject =
        { SchemaVersion = 1; ProjectId = accountId; ProjectLedgerId = ledgerId
          ProjectEpoch = 1; Account = fakeReceipt; Candidate = fakeCandidate
          ClaimOperationId = fakeClaim.OperationId; DispatchId = guid dispatch
          EffectId = guid effect; DeviceId = "test-device"; WindowRegistration = "fake-window"
          Status = "Claimed"; RequestSha256 = digest; StartOperationId = Guid.Empty
          TerminalOperationId = Guid.Empty; ProviderSessionId = "" }

    let holds reason result =
        match result with
        | Error xs -> Assert.That(xs |> List.contains reason, Is.True, sprintf "Expected hold %A in %A" reason xs)
        | Ok _ -> Assert.Fail(sprintf "Unexpected authority/valid result; expected %A" reason)

    [<Test>]
    member _.``genuine account shape projects identity but never grants send authority`` () =
        match inspectAccount observation with
        | Ok p ->
            Assert.That(p.Key, Is.EqualTo(digest))
            Assert.That(p.Generation, Is.EqualTo(1L))
        | Error why -> Assert.Fail(sprintf "Expected non-authorizing structural projection: %A" why)

    [<Test>]
    member _.``account refuses a foreign candidate`` () =
        inspectAccount { observation with ExpectedCandidate = { candidate with ProjectId = Guid.NewGuid().ToString("D") } }
        |> holds ForeignCandidate

    [<Test>]
    member _.``account refuses mutated source ref even when dispatch ID matches`` () =
        inspectAccount { observation with ExpectedCandidate = { candidate with SourceRef = "refs/heads/mutated" } }
        |> holds ForeignCandidate

    [<Test>]
    member _.``account refuses changed source lease checksum`` () =
        inspectAccount { observation with ExpectedCandidate = { candidate with SourceLeaseSha256 = String.replicate 64 "f" } }
        |> holds ForeignCandidate

    [<Test>]
    member _.``account refuses stale reservation generation`` () =
        inspectAccount { observation with ExpectedGeneration = 2L } |> holds StaleGeneration

    [<Test>]
    member _.``account refuses a stale or invalid account head`` () =
        inspectAccount { observation with AccountHead = "not-an-oid" } |> holds InvalidAccountHead

    [<Test>]
    member _.``account refuses a revoked Worker phase`` () =
        inspectAccount { observation with CanonicalWorkerPhase = "Revoked" } |> holds WorkerNotStarted

    [<Test>]
    member _.``account refuses a Ready Worker phase`` () =
        inspectAccount { observation with CanonicalWorkerPhase = "Ready" } |> holds WorkerNotStarted

    [<Test>]
    member _.``account refuses unknown and uncertain reservation states`` () =
        for state in [ "Unknown"; "Uncertain"; "Active"; "Released" ] do
            inspectAccount { observation with Reservation = { reserved with Status = state } }
            |> holds NotFreshReservation

    [<Test>]
    member _.``duplicate reservation cannot be reused as a fresh one`` () =
        inspectAccount { observation with Reservation = { reserved with Status = "Dispatching" } }
        |> holds NotFreshReservation

    [<Test>]
    member _.``disposable B1 Ready cannot consume the genuine Started native account shape`` () =
        inspectB1 { Account = observation; FakeClaim = fakeClaim
                    FakeProject = fakeProject; CanonicalProjectWorkerPhase = "Ready" }
        |> holds StartedReadyMismatch

    [<Test>]
    member _.``even matching B1 identities have fake receipt not production authority`` () =
        inspectB1 { Account = observation; FakeClaim = fakeClaim
                    FakeProject = fakeProject; CanonicalProjectWorkerPhase = "Ready" }
        |> holds DisposableReceiptNotAuthority

    [<Test>]
    member _.``B1 cannot hide a foreign project claim`` () =
        inspectB1 { Account = observation; FakeClaim = { fakeClaim with DispatchId = Guid.NewGuid() }
                    FakeProject = fakeProject; CanonicalProjectWorkerPhase = "Ready" }
        |> holds ForeignProjectFence

    [<Test>]
    member _.``B1 cannot hide an altered source generation`` () =
        inspectB1 { Account = observation; FakeClaim = { fakeClaim with Candidate = { fakeCandidate with AssignmentGeneration = 8 } }
                    FakeProject = fakeProject; CanonicalProjectWorkerPhase = "Ready" }
        |> holds ForeignProjectFence

    [<TestCase("LostAck")>]
    [<TestCase("TabClosed")>]
    [<TestCase("Unknown")>]
    [<TestCase("Revoked")>]
    [<TestCase("LateAck")>]
    [<TestCase("Duplicate")>]
    [<TestCase("SecondClientCASLoser")>]
    member _.``untrusted event is not a release or a new send permit`` (eventName: string) =
        evaluateHostEvent eventName |> holds VerifiedOutcomeUnavailable

    [<Test>]
    member _.``terminal locator with empty operation cannot authorize release`` () =
        let locator : NativeTerminalLocator =
            { DispatchId = guid dispatch; OriginalReservationId = guid original
              ReservationRequestSha256 = digest; ExpectedGeneration = reserved.Generation
              EffectOperationId = guid effect; TerminalOperationId = Guid.Empty }
        let proof =
            { DispatchId = locator.DispatchId; OriginalReservationId = locator.OriginalReservationId
              EffectOperationId = locator.EffectOperationId; TerminalOperationId = locator.TerminalOperationId
              ProjectId = guid accountId; ProjectLedgerId = guid ledgerId; ProjectEpoch = 1
              CanonicalKey = digest; ProviderSessionId = "s1"; ProjectHead = head
              ProjectTerminalOid = oid; ProviderTerminalOid = head }
        inspectTerminal { Reservation = { reserved with Status = "Active"; ProjectFenceOid = oid; ProviderSessionId = "s1" }
                          Locator = locator; Proof = proof }
        |> holds InvalidTerminalProof

    [<Test>]
    member _.``matching typed terminal shape remains non-authoritative without verified canonical origin`` () =
        let locator : NativeTerminalLocator =
            { DispatchId = guid dispatch; OriginalReservationId = guid original
              ReservationRequestSha256 = digest; ExpectedGeneration = reserved.Generation
              EffectOperationId = guid effect; TerminalOperationId = guid terminal }
        let proof =
            { DispatchId = locator.DispatchId; OriginalReservationId = locator.OriginalReservationId
              EffectOperationId = locator.EffectOperationId; TerminalOperationId = locator.TerminalOperationId
              ProjectId = guid accountId; ProjectLedgerId = guid ledgerId; ProjectEpoch = 1
              CanonicalKey = digest; ProviderSessionId = "s1"; ProjectHead = head
              ProjectTerminalOid = oid; ProviderTerminalOid = head }
        match inspectTerminal { Reservation = { reserved with Status = "Active"; ProjectFenceOid = oid; ProviderSessionId = "s1" }
                                Locator = locator; Proof = proof } with
        | Ok structural -> 
            Assert.That(structural.SessionId, Is.EqualTo("s1"))
            authorizeRelease structural |> holds VerifiedOutcomeUnavailable
        | Error e -> Assert.Fail(sprintf "Expected structural-only terminal observation: %A" e)

    [<Test>]
    member _.``forged provider session is rejected before non-authoritative projection`` () =
        let locator : NativeTerminalLocator =
            { DispatchId = guid dispatch; OriginalReservationId = guid original
              ReservationRequestSha256 = digest; ExpectedGeneration = reserved.Generation
              EffectOperationId = guid effect; TerminalOperationId = guid terminal }
        let proof =
            { DispatchId = locator.DispatchId; OriginalReservationId = locator.OriginalReservationId
              EffectOperationId = locator.EffectOperationId; TerminalOperationId = locator.TerminalOperationId
              ProjectId = guid accountId; ProjectLedgerId = guid ledgerId; ProjectEpoch = 1
              CanonicalKey = digest; ProviderSessionId = "foreign"; ProjectHead = head
              ProjectTerminalOid = oid; ProviderTerminalOid = head }
        inspectTerminal { Reservation = { reserved with Status = "Active"; ProjectFenceOid = oid; ProviderSessionId = "original" }
                          Locator = locator; Proof = proof }
        |> holds InvalidTerminalProof

    [<Test>]
    member _.MalformedB1MissingAccountReturnsTypedHold() =
        inspectB1 { Account = Unchecked.defaultof<AccountObservation>
                    FakeClaim = fakeClaim; FakeProject = fakeProject
                    CanonicalProjectWorkerPhase = "Ready" }
        |> holds MissingObservation

    [<Test>]
    member _.MalformedB1MissingExpectedCandidateReturnsTypedHold() =
        inspectB1 { Account = { observation with ExpectedCandidate = Unchecked.defaultof<NativeDispatchCandidate> }
                    FakeClaim = fakeClaim; FakeProject = fakeProject
                    CanonicalProjectWorkerPhase = "Ready" }
        |> holds MissingObservation

    [<Test>]
    member _.MalformedB1MissingFakeClaimCandidateReturnsTypedHold() =
        inspectB1 { Account = observation
                    FakeClaim = { fakeClaim with Candidate = Unchecked.defaultof<_> }
                    FakeProject = fakeProject; CanonicalProjectWorkerPhase = "Ready" }
        |> holds MissingObservation

    [<Test>]
    member _.MalformedB1MissingFakeClaimAccountReturnsTypedHold() =
        inspectB1 { Account = observation
                    FakeClaim = { fakeClaim with Account = Unchecked.defaultof<_> }
                    FakeProject = fakeProject; CanonicalProjectWorkerPhase = "Ready" }
        |> holds MissingObservation

    [<Test>]
    member _.MalformedActiveTerminalMissingCandidateReturnsTypedHold() =
        let locator : NativeTerminalLocator =
            { DispatchId = guid dispatch; OriginalReservationId = guid original
              ReservationRequestSha256 = digest; ExpectedGeneration = reserved.Generation
              EffectOperationId = guid effect; TerminalOperationId = guid terminal }
        let proof =
            { DispatchId = locator.DispatchId; OriginalReservationId = locator.OriginalReservationId
              EffectOperationId = locator.EffectOperationId; TerminalOperationId = locator.TerminalOperationId
              ProjectId = guid accountId; ProjectLedgerId = guid ledgerId; ProjectEpoch = 1
              CanonicalKey = digest; ProviderSessionId = "s1"; ProjectHead = head
              ProjectTerminalOid = oid; ProviderTerminalOid = head }
        inspectTerminal { Reservation = { reserved with
                                            Status = "Active"
                                            Candidate = Unchecked.defaultof<NativeDispatchCandidate>
                                            ProjectFenceOid = oid; ProviderSessionId = "s1" }
                          Locator = locator; Proof = proof }
        |> holds MissingObservation
