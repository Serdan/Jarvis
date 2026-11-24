module Client.Tests.NativeProjectNoSend20261124

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open Client.LedgerStore
open Client.NativeSchedulerAccountReservation
open Client.NativeSchedulerRosterPolicy
open Client.AutomationNativeAccountProjectIngress
open Client.NativeProjectEffectTransition

let private success = function
    | Ok x -> x
    | Error e -> failwithf "Expected success: %A" e
let private rejected = function
    | Error _ -> ()
    | Ok x -> failwithf "Expected rejection: %A" x
let private json value =
    JsonSerializer.Serialize(value, JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase))

let private git directory args =
    use proc = new Process()
    proc.StartInfo.FileName <- "git"
    proc.StartInfo.WorkingDirectory <- directory
    proc.StartInfo.RedirectStandardOutput <- true
    proc.StartInfo.RedirectStandardError <- true
    proc.StartInfo.Environment["GIT_TERMINAL_PROMPT"] <- "0"
    proc.StartInfo.Environment["GIT_NO_REPLACE_OBJECTS"] <- "1"
    args |> List.iter proc.StartInfo.ArgumentList.Add
    Assert.That(proc.Start(), Is.True)
    let output = proc.StandardOutput.ReadToEndAsync()
    let error = proc.StandardError.ReadToEndAsync()
    Assert.That(proc.WaitForExit(30000), Is.True, "Bounded offline git fixture timeout")
    Assert.That(proc.ExitCode, Is.EqualTo(0), error.GetAwaiter().GetResult())
    output.GetAwaiter().GetResult().Trim()

let private withProject action =
    let root = Path.Combine(Path.GetTempPath(), "native-exclusion-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    try
        let remote = Path.Combine(root, "remote.git")
        let one = Path.Combine(root, "one")
        let two = Path.Combine(root, "two")
        git root ["init"; "--bare"; "--initial-branch=main"; remote] |> ignore
        git root ["clone"; remote; one] |> ignore
        git one ["config"; "user.name"; "Disposable Test"] |> ignore
        git one ["config"; "user.email"; "test@example.test"] |> ignore
        File.WriteAllText(Path.Combine(one, "seed"), "source")
        git one ["add"; "seed"] |> ignore
        git one ["commit"; "-m"; "seed"] |> ignore
        git one ["push"; "-u"; "origin"; "HEAD"] |> ignore
        let binding =
            { RepositoryPath = one; RemoteName = "origin"
              ApprovedFetchUrl = remote; ApprovedPushUrl = remote
              ProjectId = Guid.NewGuid(); LedgerId = Guid.NewGuid(); Epoch = 1 }
        LedgerStore.initialize binding true |> success |> ignore
        git root ["clone"; remote; two] |> ignore
        action binding { binding with RepositoryPath = two }
    finally
        Directory.Delete(root, true)

let private original (b: LedgerBinding) =
    let c: NativeDispatchCandidate =
        { DispatchId = string(Guid.NewGuid()); EffectOperationId = string(Guid.NewGuid())
          Kind = "Worker"; ProjectId = string b.ProjectId; ProjectLedgerId = string b.LedgerId
          ProjectEpoch = b.Epoch; OwnerAgentId = "Worker Fixture"; TaskId = "task"
          AttemptId = Guid.NewGuid().ToString("N"); AssignmentGeneration = 1L
          SourceRepositoryId = String.replicate 64 "a"; SourceBaseOid = String.replicate 40 "b"
          SourceRef = "refs/heads/test"; SourceLeaseSha256 = String.replicate 64 "c"
          ReviewId = ""; ReviewVersion = 0L; ReviewedHeadOid = ""; ReviewRef = ""; ClaimOperationId = "" }
    { AccountId = Guid.NewGuid(); AccountLedgerId = Guid.NewGuid(); AccountEpoch = 1
      ReservationOperationId = Guid.NewGuid(); CanonicalKey = String.replicate 64 "d"; Candidate = c }

let private path (i: OriginalEffectIdentity) =
    let c = i.Candidate
    "records/native-account-started-effect-v1/"
    + NativeSchedulerRosterPolicy.sha256 (c.ProjectLedgerId + "|" + c.Kind + "|" + c.TaskId + "|" + c.AttemptId)
    + ".json"

let private current b = (LedgerStore.fetch b |> success).Commit
let private claimIntent (i: OriginalEffectIdentity) head claimId =
    { OperationId = claimId; Actor = i.Candidate.OwnerAgentId
      Kind = "NativeStartedLaunchIntentDisposable"
      RequestJson = json {| candidate = i.Candidate; expectedProjectHead = head
                            originalReservationOperationId = i.ReservationOperationId |} }

let private heldRaw (i: OriginalEffectIdentity) claimId =
    let c = i.Candidate
    let effect: StartedProjectEffect =
        { SchemaVersion = 1; ProjectId = c.ProjectId; ProjectLedgerId = c.ProjectLedgerId
          ProjectEpoch = c.ProjectEpoch; AccountId = string i.AccountId
          AccountLedgerId = string i.AccountLedgerId; AccountEpoch = i.AccountEpoch
          AccountHead = String.replicate 40 "e"; OriginalReservationOperationId = string i.ReservationOperationId
          ReservationCanonicalKey = i.CanonicalKey; Candidate = c; ClaimOperationId = string claimId
          DispatchId = c.DispatchId; EffectOperationId = c.EffectOperationId
          SourceLeaseSha256 = c.SourceLeaseSha256; DeviceId = "fixture-device"
          WindowRegistration = "fixture-window"; Status = "HeldBeforeSend"
          ProviderSessionId = ""; TerminalOriginOid = "" }
    json effect

let private claim b head i intent =
    LedgerStore.transact b intent (fun snapshot ->
        checkClaim b snapshot head i (path i)
        |> Result.map (fun () ->
            [ { Path = path i; ExpectedBlobOid = None; Content = heldRaw i intent.OperationId } ]))

let private witness b i claimId =
    let s = LedgerStore.fetch b |> success
    let op = LedgerStore.getOperationAt b s claimId |> success |> Option.get
    let origin = LedgerStore.verifiedIntegrationOperationSnapshotAt b s op |> success
    let _, blob = LedgerStore.readAt b origin (path i) |> success
    { ClaimOperationId = claimId; ClaimRequestSha256 = op.RequestHash; ClaimOrigin = origin.Commit; ClaimBlobOid = Option.get blob }

// The evidence fixture matches the observed consumer draft's field shape.
// It is not an account proof; account receipt verification and authorized
// runtime composition deliberately remain outside this isolated test project.
let private exclusionIntent (i: OriginalEffectIdentity) head predecessor fenceOrigin op =
    let predecessorJson =
        match predecessor with
        | BeforeClaim -> box {| kind = "BeforeClaim" |}
        | ExactHeldFence held ->
            box {| kind = "ExactHeldFence"; claimOperationId = held.ClaimOperationId
                   claimRequestSha256 = held.ClaimRequestSha256; claimOrigin = held.ClaimOrigin
                   claimBlobOid = held.ClaimBlobOid |}
    let evidence =
        {| schemaVersion = 1; state = "VerifiedNoSend"; accountId = string i.AccountId
           accountLedgerId = string i.AccountLedgerId; accountEpoch = i.AccountEpoch
           reservationOrigin = String.replicate 40 "f"; canonicalKey = i.CanonicalKey
           original =
               {| reservationOperationId = i.ReservationOperationId
                  reservationRequestSha256 = String.replicate 64 "a"; candidate = i.Candidate
                  expectedGeneration = if fenceOrigin = "" then 1L else 2L
                  expectedProjectFenceOid = fenceOrigin; expectedProviderSessionId = "" |}
           noSendOperationId = op |}
    { OperationId = op; Actor = "Fixture"; Kind = "NativeVerifiedNoSend"
      RequestJson = json {| schemaVersion = 1; expectedProjectHead = head
                            predecessor = predecessorJson; evidence = evidence |} }

let private exclude b head i predecessor intent =
    LedgerStore.transact b intent (fun s -> planExclusion b s head i (path i) predecessor intent)

let private sendIntent (i: OriginalEffectIdentity) (held: HeldFence) op =
    { OperationId = op; Actor = i.Candidate.OwnerAgentId; Kind = "NativeBeginSend"
      RequestJson = json {| original = i; held = held |} }

let private beginSend b head i held intent =
    LedgerStore.transact b intent (fun s ->
        planBeginSend b s head i (path i) held |> Result.map List.singleton)

let private assertEvidence b i (intent: LedgerIntent) =
    let s = LedgerStore.fetch b |> success
    let op = LedgerStore.getOperationAt b s intent.OperationId |> success |> Option.get
    Assert.That(op.Type, Is.EqualTo("NativeVerifiedNoSend"))
    let origin = LedgerStore.verifiedIntegrationOperationSnapshotAt b s op |> success
    LedgerStore.verifyIntegrationOperationIntent b op intent |> success
    let expected = LedgerStore.readAt b origin (path i) |> success
    use request = JsonDocument.Parse(intent.RequestJson)
    let evidence = request.RootElement.GetProperty("evidence").GetRawText()
    let predecessor = request.RootElement.GetProperty("predecessor")
    let head = request.RootElement.GetProperty("expectedProjectHead").GetString()
    Assert.That(git b.RepositoryPath ["rev-parse"; origin.Commit + "^"], Is.EqualTo(head))
    use receipt = JsonDocument.Parse(op.OriginalResultJson)
    let changes = receipt.RootElement.GetProperty("changes").EnumerateArray() |> Seq.toArray
    Assert.That(changes.Length, Is.EqualTo(2))
    let fenceChange = changes |> Array.find (fun c -> c.GetProperty("path").GetString() = path i)
    let evidenceChange = changes |> Array.find (fun c -> c.GetProperty("path").GetString() = noSendEvidencePath intent.OperationId)
    let beforeOid = fenceChange.GetProperty("beforeOid")
    match predecessor.GetProperty("kind").GetString() with
    | "BeforeClaim" -> Assert.That(beforeOid.ValueKind, Is.EqualTo(JsonValueKind.Null))
    | "ExactHeldFence" ->
        Assert.That(beforeOid.GetString(), Is.EqualTo(predecessor.GetProperty("claimBlobOid").GetString()))
    | other -> failwithf "Unexpected predecessor: %s" other
    Assert.That(evidenceChange.GetProperty("beforeOid").ValueKind, Is.EqualTo(JsonValueKind.Null))
    let evidenceHash = NativeSchedulerRosterPolicy.sha256 evidence
    for change in changes do
        Assert.That(change.GetProperty("contentSha256").GetString(), Is.EqualTo(evidenceHash))
    Assert.That(fst expected, Is.EqualTo(Some evidence))
    Assert.That(snd expected |> Option.isSome, Is.True)
    for at in [origin; s] do
        for p in [path i; noSendEvidencePath intent.OperationId] do
            Assert.That(LedgerStore.readAt b at p |> success, Is.EqualTo(expected))

[<Test>]
let ``preclaim exclusion holds permanently and replay is receipt only`` () =
    withProject (fun one two ->
        let i = original one
        let head = current one
        let op = exclusionIntent i head BeforeClaim "" (Guid.NewGuid())
        let committed = exclude one head i BeforeClaim op |> success
        Assert.That(committed.AlreadyApplied, Is.False)
        assertEvidence two i op
        let claimId = Guid.NewGuid()
        claim two head i (claimIntent i head claimId) |> rejected
        let newHead = current two
        claim two newHead i (claimIntent i newHead claimId) |> rejected
        let replay = exclude two head i BeforeClaim op |> success
        Assert.That(replay.AlreadyApplied, Is.True)
        let modified = { op with RequestJson = op.RequestJson.Replace(String.replicate 64 "a", String.replicate 64 "b") }
        match exclude two newHead i BeforeClaim modified with
        | Error OperationIdReused -> ()
        | x -> failwithf "Changed request must not replay: %A" x
        exclude two newHead i BeforeClaim (exclusionIntent i newHead BeforeClaim "" (Guid.NewGuid())) |> rejected
        assertEvidence two i op
        let successor = original two
        Assert.That(path successor, Is.Not.EqualTo(path i))
        let successorHead = current two
        claim two successorHead successor (claimIntent successor successorHead (Guid.NewGuid())) |> success |> ignore
        assertEvidence one i op)

[<Test>]
let ``exact held fence can be excluded but never subsequently begun`` () =
    withProject (fun one two ->
        let i = original one
        let head = current one
        let claimId = Guid.NewGuid()
        claim one head i (claimIntent i head claimId) |> success |> ignore
        let held = witness two i claimId
        let heldHead = current two
        let op = exclusionIntent i heldHead (ExactHeldFence held) held.ClaimOrigin (Guid.NewGuid())
        exclude two heldHead i BeforeClaim op |> rejected
        // Keep assertions and serialized request consistent here: these
        // negatives must exercise the actual held-record/claim verification.
        let denyHeld badIdentity badFence =
            let predecessor = ExactHeldFence badFence
            let intent = exclusionIntent badIdentity heldHead predecessor "" (Guid.NewGuid())
            exclude two heldHead badIdentity predecessor intent |> rejected
        denyHeld i {held with ClaimOrigin = head}
        denyHeld i {held with ClaimBlobOid = String.replicate 40 "a"}
        denyHeld i {held with ClaimOperationId = Guid.NewGuid()}
        denyHeld i {held with ClaimRequestSha256 = String.replicate 64 "e"}
        denyHeld {i with Candidate = {i.Candidate with AssignmentGeneration = 2L}} held
        denyHeld {i with Candidate = {i.Candidate with EffectOperationId = string(Guid.NewGuid())}} held
        denyHeld {i with ReservationOperationId = Guid.NewGuid()} held
        denyHeld {i with AccountEpoch = 2} held
        exclude two heldHead i (ExactHeldFence held) op |> success |> ignore
        assertEvidence one i op
        let send = sendIntent i held (Guid.NewGuid())
        beginSend one heldHead i held send |> rejected
        beginSend one (current one) i held send |> rejected
        let oldClaimReplay = claim one head i (claimIntent i head claimId) |> success
        Assert.That(oldClaimReplay.AlreadyApplied, Is.True)
        assertEvidence two i op)

[<Test>]
let ``begin send consumes held state and replay never grants send twice`` () =
    withProject (fun one two ->
        let i = original one
        let head = current one
        let claimId = Guid.NewGuid()
        claim one head i (claimIntent i head claimId) |> success |> ignore
        let held = witness two i claimId
        let heldHead = current two
        let send = sendIntent i held (Guid.NewGuid())
        let first = beginSend two heldHead i held send |> success
        Assert.That(first.AlreadyApplied, Is.False)
        let replay = beginSend one heldHead i held send |> success
        Assert.That(replay.AlreadyApplied, Is.True)
        let latest = current one
        exclude one latest i (ExactHeldFence held) (exclusionIntent i latest (ExactHeldFence held) held.ClaimOrigin (Guid.NewGuid())) |> rejected
        beginSend one latest i held (sendIntent i held (Guid.NewGuid())) |> rejected
        let s = LedgerStore.fetch one |> success
        let raw, _ = LedgerStore.readAt one s (path i) |> success
        use doc = JsonDocument.Parse(Option.get raw)
        Assert.That(doc.RootElement.GetProperty("status").GetString(), Is.EqualTo("Dispatching")))

[<Test>]
let ``actual two clone preclaim race permits only one writer`` () =
    withProject (fun one two ->
        let i = original one
        let head = current one
        let claimOp = claimIntent i head (Guid.NewGuid())
        let exclusion = exclusionIntent i head BeforeClaim "" (Guid.NewGuid())
        use barrier = new Barrier(2)
        let waitOnce (once: int ref) result =
            if Result.isOk result && Interlocked.Increment(&once.contents) = 1 then
                Assert.That(barrier.SignalAndWait(TimeSpan.FromSeconds 30.0), Is.True)
            result
        let firstCalls, secondCalls = ref 0, ref 0
        let first = Task.Run(fun () ->
            LedgerStore.transact one claimOp (fun s ->
                checkClaim one s head i (path i)
                |> Result.map (fun () -> [{Path = path i; ExpectedBlobOid = None; Content = heldRaw i claimOp.OperationId}])
                |> waitOnce firstCalls))
        let second = Task.Run(fun () ->
            LedgerStore.transact two exclusion (fun s ->
                planExclusion two s head i (path i) BeforeClaim exclusion |> waitOnce secondCalls))
        Assert.That(Task.WaitAll([|first :> Task; second :> Task|], TimeSpan.FromSeconds 90.0), Is.True)
        let outcomes = [first.Result; second.Result]
        Assert.That(outcomes |> List.filter Result.isOk |> List.length, Is.EqualTo(1))
        if Result.isOk second.Result then
            assertEvidence one i exclusion
            claim one (current one) i (claimIntent i (current one) (Guid.NewGuid())) |> rejected
        else
            let held = witness one i claimOp.OperationId
            let freshHead = current two
            exclude two freshHead i (ExactHeldFence held) exclusion |> rejected
            let fresh = exclusionIntent i freshHead (ExactHeldFence held) "" (Guid.NewGuid())
            exclude two freshHead i (ExactHeldFence held) fresh |> success |> ignore
            assertEvidence one i fresh)

[<Test>]
let ``actual two clone held race excludes or begins but cannot do both`` () =
    withProject (fun one two ->
        let i = original one
        let head = current one
        let claimId = Guid.NewGuid()
        claim one head i (claimIntent i head claimId) |> success |> ignore
        let held = witness two i claimId
        let heldHead = current two
        let send = sendIntent i held (Guid.NewGuid())
        let exclusion = exclusionIntent i heldHead (ExactHeldFence held) held.ClaimOrigin (Guid.NewGuid())
        use barrier = new Barrier(2)
        let sync (calls: int ref) result =
            if Result.isOk result && Interlocked.Increment(&calls.contents) = 1 then
                Assert.That(barrier.SignalAndWait(TimeSpan.FromSeconds 30.0), Is.True)
            result
        let firstCalls, secondCalls = ref 0, ref 0
        let first = Task.Run(fun () ->
            LedgerStore.transact one send (fun s ->
                planBeginSend one s heldHead i (path i) held |> Result.map List.singleton |> sync firstCalls))
        let second = Task.Run(fun () ->
            LedgerStore.transact two exclusion (fun s ->
                planExclusion two s heldHead i (path i) (ExactHeldFence held) exclusion |> sync secondCalls))
        Assert.That(Task.WaitAll([|first :> Task; second :> Task|], TimeSpan.FromSeconds 90.0), Is.True)
        Assert.That([first.Result; second.Result] |> List.filter Result.isOk |> List.length, Is.EqualTo(1))
        if Result.isOk second.Result then
            assertEvidence one i exclusion
            beginSend one (current one) i held send |> rejected
        else
            let latest = current two
            let fresh = exclusionIntent i latest (ExactHeldFence held) held.ClaimOrigin (Guid.NewGuid())
            exclude two latest i (ExactHeldFence held) fresh |> rejected)

[<Test>]
let ``wrong canonical key and unknown fence fail closed`` () =
    withProject (fun one two ->
        let i = original one
        let head = current one
        let snapshot = LedgerStore.fetch one |> success
        let wrongPath = "records/native-account-started-effect-v1/" + String.replicate 64 "a" + ".json"
        let exclusion = exclusionIntent i head BeforeClaim "" (Guid.NewGuid())
        checkClaim one snapshot head i wrongPath |> rejected
        planExclusion one snapshot head i wrongPath BeforeClaim exclusion |> rejected
        checkClaim one snapshot head {i with Candidate = {i.Candidate with ProjectEpoch = i.Candidate.ProjectEpoch + 1}} (path i) |> rejected
        let claimId = Guid.NewGuid()
        claim one head i (claimIntent i head claimId) |> success |> ignore
        let held = witness two i claimId
        let unknown =
            { OperationId = Guid.NewGuid(); Actor = i.Candidate.OwnerAgentId
              Kind = "FixtureUnknown"; RequestJson = json {| original = i |} }
        LedgerStore.transact two unknown (fun s ->
            LedgerStore.readAt two s (path i)
            |> Result.map (fun (raw, blob) ->
                [{ Path = path i; ExpectedBlobOid = blob
                   Content = (Option.get raw).Replace("HeldBeforeSend", "Unknown") }]))
        |> success |> ignore
        let latest = current one
        let fresh = exclusionIntent i latest (ExactHeldFence held) held.ClaimOrigin (Guid.NewGuid())
        exclude one latest i (ExactHeldFence held) fresh |> rejected
        beginSend one latest i held (sendIntent i held (Guid.NewGuid())) |> rejected
        claim one latest i (claimIntent i latest (Guid.NewGuid())) |> rejected
        let s = LedgerStore.fetch one |> success
        let raw, _ = LedgerStore.readAt one s (path i) |> success
        use doc = JsonDocument.Parse(Option.get raw)
        Assert.That(doc.RootElement.GetProperty("status").GetString(), Is.EqualTo("Unknown")))

[<Test>]
let ``claim before account recording requires a freshly bound held request`` () =
    withProject (fun one two ->
        let i = original one
        let head = current one
        let preclaim = exclusionIntent i head BeforeClaim "" (Guid.NewGuid())
        let claimId = Guid.NewGuid()
        claim one head i (claimIntent i head claimId) |> success |> ignore
        let held = witness two i claimId
        let latest = current two
        exclude two latest i (ExactHeldFence held) preclaim |> rejected
        let fresh = exclusionIntent i latest (ExactHeldFence held) "" (Guid.NewGuid())
        // Account still Reserved, generation 1; the local predecessor is held.
        let wrongAccount = exclusionIntent i latest (ExactHeldFence held) head (Guid.NewGuid())
        exclude two latest i (ExactHeldFence held) wrongAccount |> rejected
        let stale = exclusionIntent i head (ExactHeldFence held) "" (Guid.NewGuid())
        exclude two latest i (ExactHeldFence held) stale |> rejected
        let wrongIdentity = exclusionIntent {i with AccountId = Guid.NewGuid()} latest (ExactHeldFence held) "" (Guid.NewGuid())
        exclude two latest i (ExactHeldFence held) wrongIdentity |> rejected
        let wrongOperation = {fresh with OperationId = Guid.NewGuid()}
        exclude two latest i (ExactHeldFence held) wrongOperation |> rejected
        exclude two latest i (ExactHeldFence held) fresh |> success |> ignore
        assertEvidence one i fresh
        let receipt = exclude one latest i (ExactHeldFence held) fresh |> success
        Assert.That(receipt.AlreadyApplied, Is.True)
        beginSend one (current one) i held (sendIntent i held (Guid.NewGuid())) |> rejected)

[<Test>]
let ``preclaim excludes only a compatible unique account evidence request`` () =
    withProject (fun one two ->
        let i = original one
        let head = current one
        let fresh = exclusionIntent i head BeforeClaim "" (Guid.NewGuid())
        let withRecordedFence = exclusionIntent i head BeforeClaim head (Guid.NewGuid())
        exclude one head i BeforeClaim withRecordedFence |> rejected
        let wrongGeneration = {fresh with RequestJson = fresh.RequestJson.Replace("\"expectedGeneration\":1", "\"expectedGeneration\":2")}
        Assert.That(wrongGeneration.RequestJson, Is.Not.EqualTo(fresh.RequestJson))
        exclude one head i BeforeClaim wrongGeneration |> rejected
        let wrongCandidate = exclusionIntent {i with Candidate = {i.Candidate with EffectOperationId = string(Guid.NewGuid())}} head BeforeClaim "" (Guid.NewGuid())
        exclude one head i BeforeClaim wrongCandidate |> rejected
        let duplicate = {fresh with RequestJson = fresh.RequestJson.Replace("\"state\":\"VerifiedNoSend\"", "\"state\":\"Other\",\"state\":\"VerifiedNoSend\"")}
        Assert.That(duplicate.RequestJson, Is.Not.EqualTo(fresh.RequestJson))
        exclude one head i BeforeClaim duplicate |> rejected
        Assert.That(current two, Is.EqualTo(head))
        exclude one head i BeforeClaim fresh |> success |> ignore
        assertEvidence two i fresh)
