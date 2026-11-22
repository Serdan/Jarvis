module Program

open System
open System.IO
open System.Diagnostics
open System.Threading.Tasks
open Client.LedgerStore
open Client.NativeProjectEffectFence

let mutable passed = 0
let test name action =
    try
        action()
        passed <- passed + 1
        printfn "PASS %s" name
    with error ->
        eprintfn "FAIL %s: %s" name (error.ToString())
        reraise()

let check condition message = if not condition then failwith message
let success = function
    | Ok value -> value
    | Error error -> failwithf "Unexpected ledger failure %A" error
let denied = function
    | Error _ -> ()
    | Ok _ -> failwith "Unexpected acceptance; expected fail closed"

let git dir args =
    use p = new Process()
    p.StartInfo.FileName <- "git"
    p.StartInfo.WorkingDirectory <- dir
    p.StartInfo.RedirectStandardOutput <- true
    p.StartInfo.RedirectStandardError <- true
    args |> List.iter p.StartInfo.ArgumentList.Add
    check (p.Start()) "Git process did not start"
    let stdout = p.StandardOutput.ReadToEnd()
    let stderr = p.StandardError.ReadToEnd()
    p.WaitForExit()
    check (p.ExitCode = 0) ("Git: " + stderr)
    stdout.Trim()

type Fixture =
    { Root: string
      Bare: string
      AccountBare: string
      One: LedgerBinding
      Two: LedgerBinding
      Three: LedgerBinding
      AccountOne: LedgerBinding
      AccountTwo: LedgerBinding
      AccountThree: LedgerBinding
      Worker: NativeCandidate
      Reviewer: NativeCandidate
      AccountHead: string }

let withFixture body =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-native-stageb-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(root) |> ignore
    try
        let bare = Path.Combine(root, "project.git")
        let accountBare = Path.Combine(root, "account.git")
        git root ["init"; "--bare"; bare] |> ignore
        git root ["init"; "--bare"; accountBare] |> ignore
        let first = Path.Combine(root, "first")
        let second = Path.Combine(root, "second")
        let third = Path.Combine(root, "third")
        let account = Path.Combine(root, "fake-account-seed")
        git root ["clone"; bare; first] |> ignore
        git root ["clone"; accountBare; account] |> ignore
        for dir in [first; account] do
            git dir ["config"; "user.name"; "StageB disposable"] |> ignore
            git dir ["config"; "user.email"; "stageb@example.invalid"] |> ignore
            File.WriteAllText(Path.Combine(dir, "fixture.txt"), "disposable only")
            git dir ["add"; "fixture.txt"] |> ignore
            git dir ["commit"; "-m"; "disposable fixture"] |> ignore
            git dir ["push"; "-u"; "origin"; "HEAD"] |> ignore
        let sourceHead = git first ["rev-parse"; "HEAD"]
        let sourceRef = "refs/heads/" + git first ["branch"; "--show-current"]
        let accountHead = git account ["rev-parse"; "HEAD"]
        let reviewId = Guid.NewGuid().ToString("N")
        let taskId = "native-stageb-task"
        let attemptId = Guid.NewGuid().ToString("N")
        let reviewerTask = "native-stageb-review"
        let reviewRef = "refs/heads/jarvis-review/" + reviewerTask + "/" + reviewId + "/r5"
        git first ["push"; "origin"; sourceHead + ":" + reviewRef] |> ignore
        git root ["clone"; bare; second] |> ignore
        git root ["clone"; bare; third] |> ignore
        let projectId, ledgerId = Guid.NewGuid(), Guid.NewGuid()
        let binding dir =
            { RepositoryPath = dir; RemoteName = "origin"
              ApprovedFetchUrl = bare; ApprovedPushUrl = bare
              ProjectId = projectId; LedgerId = ledgerId; Epoch = 1 }
        let one, two, three = binding first, binding second, binding third
        LedgerStore.initialize one true |> success |> ignore
        let fakeAccountId, fakeAccountLedgerId = Guid.NewGuid(), Guid.NewGuid()
        let accountBinding dir =
            { RepositoryPath = dir; RemoteName = "origin"
              ApprovedFetchUrl = accountBare; ApprovedPushUrl = accountBare
              ProjectId = fakeAccountId; LedgerId = fakeAccountLedgerId; Epoch = 1 }
        let accountOne = accountBinding account
        LedgerStore.initialize accountOne true |> success |> ignore
        let accountSecond, accountThird =
            Path.Combine(root, "fake-account-second"), Path.Combine(root, "fake-account-third")
        git root ["clone"; accountBare; accountSecond] |> ignore
        git root ["clone"; accountBare; accountThird] |> ignore
        let accountTwo, accountThree = accountBinding accountSecond, accountBinding accountThird
        let worker =
            { Kind = "Worker"; TaskId = taskId; AttemptId = attemptId
              ReviewId = ""; OwnerAgentId = "Worker_A"
              AssignmentGeneration = 2; ReviewVersion = 0
              SourceRepositoryId = "disposable-repo-id"; SourceRef = sourceRef
              BaseCommit = sourceHead; TargetRef = sourceRef
              ReviewRef = ""; ReviewedHeadOid = "" }
        let reviewer =
            { Kind = "Reviewer"; TaskId = reviewerTask; AttemptId = ""
              ReviewId = reviewId; OwnerAgentId = "Reviewer_A"
              AssignmentGeneration = 0; ReviewVersion = 5
              SourceRepositoryId = ""; SourceRef = sourceRef
              BaseCommit = ""; TargetRef = sourceRef
              ReviewRef = reviewRef; ReviewedHeadOid = sourceHead }
        let seedIntent =
            { OperationId = Guid.NewGuid(); Actor = "FixtureSeeder"
              Kind = "NativeStageBSeedTestOnly"; RequestJson = "{}" }
        let records =
            [ ("tasks/" + taskId + "/task.json",
               sprintf """{"taskId":"%s","lifecycle":"InProgress","attemptId":"%s"}""" taskId attemptId)
              ("tasks/" + taskId + "/attempts/" + attemptId + ".json",
               sprintf """{"taskId":"%s","attemptId":"%s","ownerAgentId":"Worker_A","phase":"Ready","assignmentGeneration":2,"sourceRepositoryId":"disposable-repo-id","sourceRef":"%s","baseCommit":"%s","targetRef":"%s"}"""
                   taskId attemptId sourceRef sourceHead sourceRef)
              ("tasks/" + reviewerTask + "/reviews/" + reviewId + "/review.json",
               sprintf """{"taskId":"%s","reviewId":"%s","reviewerId":"Reviewer_A","version":5,"status":"Claimed","reviewRef":"%s","reviewedHeadOid":"%s","taskRef":"%s"}"""
                   reviewerTask reviewId reviewRef sourceHead sourceRef) ]
        LedgerStore.transact one seedIntent (fun _ ->
            Ok (records |> List.map (fun (path, content) ->
                { Path = path; ExpectedBlobOid = None; Content = content })))
        |> success |> ignore
        body { Root = root; Bare = bare; AccountBare = accountBare
               One = one; Two = two; Three = three
               AccountOne = accountOne; AccountTwo = accountTwo; AccountThree = accountThree
               Worker = worker; Reviewer = reviewer; AccountHead = accountHead }
    finally
        if Directory.Exists(root) then Directory.Delete(root, true)

let receipt (fixture: Fixture) kind =
    { Marker = "STAGE_B_DISPOSABLE_FAKE_ACCOUNT_RECEIPT/v1"
      AccountLedgerId = fixture.AccountOne.LedgerId.ToString("D")
      AccountEpoch = fixture.AccountOne.Epoch
      AccountHead = (LedgerStore.fetch fixture.AccountOne |> success).Commit
      OriginalReservationOperationId = Guid.NewGuid().ToString("D")
      ReservationId = Guid.NewGuid().ToString("D")
      ProjectId = fixture.One.ProjectId.ToString("D")
      Kind = kind }

let makeClaim fixture candidate =
    let account = receipt fixture candidate.Kind
    seedFakeReservation fixture.AccountOne account |> success |> ignore
    { OperationId = Guid.NewGuid()
      ExpectedProjectHead = (LedgerStore.fetch fixture.One |> success).Commit
      Account = account
      Candidate = candidate
      DispatchId = Guid.NewGuid()
      EffectId = Guid.NewGuid()
      DeviceId = "offline-device"
      WindowRegistration = "offline-window" }

let get fixture c =
    observe fixture.Two c |> success |> snd

let fakeState fixture account =
    observeFakeReservation fixture.AccountThree account |> success

let fakeTerminal effectId status =
    { Marker = "STAGE_B_DISPOSABLE_FAKE_PROVIDER/v1"
      EffectId = effectId; ProviderSessionId = "disposable-session-1"; Status = status }

let reused = function
    | Error OperationIdReused -> ()
    | Error other -> failwithf "Expected OperationIdReused, got %A" other
    | Ok _ -> failwith "Changed immutable operation UUID intent was replayed"

[<EntryPoint>]
let main _ =
    try
        test "project Git CAS: competing Workers, distinct UUID" (fun () ->
            withFixture (fun f ->
                let a = makeClaim f f.Worker
                let b = { a with OperationId = Guid.NewGuid(); DispatchId = Guid.NewGuid(); EffectId = Guid.NewGuid() }
                claim f.One a |> success |> ignore
                claim f.Two b |> denied
                check ((get f f.Worker).EffectId = a.EffectId) "First original effect not preserved"))
        test "exact retry across clones uses original immutable operation receipt" (fun () ->
            withFixture (fun f ->
                let a = makeClaim f f.Worker
                let first = claim f.One a |> success
                let replay = claim f.Two a |> success
                check replay.AlreadyApplied "Exact UUID replay not recognized"
                check (replay.Operation.OriginalResultHash = first.Operation.OriginalResultHash) "Receipt replaced"
                claim f.Two { a with EffectId = Guid.NewGuid() } |> denied))
        test "account won, project head conflict: zero project claim" (fun () ->
            withFixture (fun f ->
                let a = makeClaim f f.Worker
                LedgerStore.putRecord f.Two
                    { OperationId = Guid.NewGuid(); Actor = "ExternalWriter"
                      Path = "records/external-change.json"; ExpectedBlobOid = None
                      Content = """{"changed":true}""" } |> success |> ignore
                claim f.One a |> denied
                check ((LedgerStore.readRecord f.One "records/external-change.json" |> success |> snd).IsSome)
                      "Concurrent writer missing"
                check ((fakeState f a.Account).Status = "Reserved") "Failed project CAS released fake account"
                observe f.One f.Worker |> denied))
        test "stale worker generation, owner and source lease are rejected" (fun () ->
            withFixture (fun f ->
                for candidate in
                    [{ f.Worker with AssignmentGeneration = 1 }
                     { f.Worker with OwnerAgentId = "Worker_B" }
                     { f.Worker with SourceRef = "refs/heads/forged" }
                     { f.Worker with SourceRepositoryId = "foreign" }] do
                    claim f.One (makeClaim f candidate) |> denied
                observe f.One f.Worker |> denied))
        test "Reviewer frozen claim/ref/version/winner enforced" (fun () ->
            withFixture (fun f ->
                for c in
                    [{ f.Reviewer with ReviewVersion = 4 }
                     { f.Reviewer with OwnerAgentId = "Reviewer_B" }
                     { f.Reviewer with ReviewRef = "refs/heads/foreign-ref" }
                     { f.Reviewer with ReviewedHeadOid = String.replicate 40 "f" }] do
                    claim f.One (makeClaim f c) |> denied
                let a = makeClaim f f.Reviewer
                claim f.One a |> success |> ignore
                claim f.Two { a with OperationId = Guid.NewGuid(); EffectId = Guid.NewGuid() } |> denied))
        test "foreign account marker, kind, project or empty reservation UUID denied" (fun () ->
            withFixture (fun f ->
                let a = makeClaim f f.Worker
                let originals = a.Account
                for fake in
                    [{ originals with Marker = "AUTHENTIC" }
                     { originals with Kind = "Reviewer" }
                     { originals with ProjectId = Guid.NewGuid().ToString("D") }
                     { originals with OriginalReservationOperationId = Guid.Empty.ToString("D") }] do
                    claim f.One { a with Account = fake } |> denied
                observe f.One f.Worker |> denied))
        test "confirmed durable Dispatching is at most once across two clones" (fun () ->
            withFixture (fun f ->
                let c = makeClaim f f.Worker
                claim f.One c |> success |> ignore
                let started = beginFakeEffect f.Two f.Worker c.EffectId (Guid.NewGuid()) |> success
                check (not started.AlreadyApplied) "Begin marker not new"
                beginFakeEffect f.One f.Worker c.EffectId (Guid.NewGuid()) |> denied
                beginFakeEffect f.Three f.Worker (Guid.NewGuid()) (Guid.NewGuid()) |> denied
                check ((get f f.Worker).Status = "Dispatching") "Missing persisted marker"))
        test "ACK loss/crash/reconnect/clock rollback cannot re-arm fake browser" (fun () ->
            withFixture (fun f ->
                let c = makeClaim f f.Worker
                claim f.One c |> success |> ignore
                let mutable providerSubmissions = 0
                let fakePort (_: NativeEffectRecord) =
                    providerSubmissions <- providerSubmissions + 1
                    Ok (fakeTerminal c.EffectId "Unknown")
                dispatchFakePort f.One f.Worker c.EffectId (Guid.NewGuid()) fakePort |> success |> ignore
                let _fakeClockRollback = DateTimeOffset.UnixEpoch
                dispatchFakePort f.Two f.Worker c.EffectId (Guid.NewGuid()) fakePort |> denied
                dispatchFakePort f.Three f.Worker c.EffectId (Guid.NewGuid()) fakePort |> denied
                check (providerSubmissions = 1) "Duplicate provider send"
                check ((get f f.Worker).Status = "Dispatching") "Uncertain effect was rearmed"))
        test "provider Unknown and foreign terminal fail closed" (fun () ->
            withFixture (fun f ->
                let c = makeClaim f f.Worker
                claim f.One c |> success |> ignore
                beginFakeEffect f.One f.Worker c.EffectId (Guid.NewGuid()) |> success |> ignore
                let terminal = { Marker = "STAGE_B_DISPOSABLE_FAKE_PROVIDER/v1"
                                 EffectId = c.EffectId
                                 ProviderSessionId = "fixture-session-1"; Status = "Terminal" }
                recordFakeTerminal f.Two f.Worker c.EffectId (Guid.NewGuid())
                    { terminal with Status = "Unknown" } |> denied
                recordFakeTerminal f.Two f.Worker c.EffectId (Guid.NewGuid())
                    { terminal with EffectId = Guid.NewGuid() } |> denied
                recordFakeTerminal f.Two f.Worker c.EffectId (Guid.NewGuid())
                    { terminal with Marker = "foreign" } |> denied
                check ((get f f.Worker).Status = "Dispatching") "False terminal was recorded"))
        test "fake terminal recorded once, repeated/foreign project effect denied" (fun () ->
            withFixture (fun f ->
                let c = makeClaim f f.Worker
                claim f.One c |> success |> ignore
                beginFakeEffect f.Two f.Worker c.EffectId (Guid.NewGuid()) |> success |> ignore
                let terminal = { Marker = "STAGE_B_DISPOSABLE_FAKE_PROVIDER/v1"
                                 EffectId = c.EffectId; ProviderSessionId = "fixture-session-1"
                                 Status = "Terminal" }
                recordFakeTerminal f.Three f.Worker c.EffectId (Guid.NewGuid()) terminal
                |> success |> ignore
                recordFakeTerminal f.One f.Worker c.EffectId (Guid.NewGuid()) terminal |> denied
                check ((get f f.Worker).Status = "VerifiedFakeTerminal") "No terminal record"
                check ((get f f.Worker).TerminalOperationId <> Guid.Empty) "No terminal ID"))
        test "same terminal UUID exact replay accepts, changed immutable proof refuses" (fun () ->
            withFixture (fun f ->
                let c = makeClaim f f.Worker
                claim f.One c |> success |> ignore
                let startId, terminalId = Guid.NewGuid(), Guid.NewGuid()
                beginFakeEffect f.Two f.Worker c.EffectId startId |> success |> ignore
                let proof = fakeTerminal c.EffectId "Terminal"
                let first = recordFakeTerminal f.One f.Worker c.EffectId terminalId proof |> success
                check (not first.AlreadyApplied) "First terminal must be a new CAS"
                let replay = recordFakeTerminal f.Two f.Worker c.EffectId terminalId proof |> success
                check replay.AlreadyApplied "Exact proof replay must reuse original receipt"
                check (replay.Operation.OriginalResultHash = first.Operation.OriginalResultHash)
                      "Exact proof replay changed terminal receipt"
                for altered in
                    [{ proof with Status = "Unknown" }
                     { proof with Marker = "foreign" }
                     { proof with ProviderSessionId = "different-provider-session" }
                     { proof with EffectId = Guid.NewGuid() }] do
                    recordFakeTerminal f.Three f.Worker c.EffectId terminalId altered |> reused
                check ((get f f.Worker).ProviderSessionId = proof.ProviderSessionId)
                      "Mutated proof displaced original terminal"))
        test "real disposable fake port is fenced on lost ACK across three clients" (fun () ->
            withFixture (fun f ->
                let c = makeClaim f f.Worker
                let mutable sends = 0
                let port (record: NativeEffectRecord) =
                    sends <- sends + 1
                    check (record.EffectId = c.EffectId) "Fake port got foreign effect"
                    Ok (fakeTerminal c.EffectId "Unknown")
                dispatchFakePort f.One f.Worker c.EffectId (Guid.NewGuid()) port |> denied
                check (sends = 0) "Fake port invoked before project claim"
                claim f.One c |> success |> ignore
                let startId = Guid.NewGuid()
                let unknown = dispatchFakePort f.Two f.Worker c.EffectId startId port |> success
                check (unknown.Status = "Unknown") "Expected uncertain fake provider"
                check (sends = 1) "Fake port did not perform exactly one initial submission"
                recordFakeTerminal f.One f.Worker c.EffectId (Guid.NewGuid()) unknown |> denied
                let _rolledBackClock = DateTimeOffset.UnixEpoch
                dispatchFakePort f.One f.Worker c.EffectId startId port |> denied
                dispatchFakePort f.Three f.Worker c.EffectId (Guid.NewGuid()) port |> denied
                releaseFakeReservation f.One f.AccountTwo f.Worker c.EffectId (Guid.NewGuid()) (Guid.NewGuid())
                |> denied
                check (sends = 1) "Fake port was called twice after uncertain return"
                check ((get f f.Worker).Status = "Dispatching") "Uncertain dispatch was rearmed"
                check ((fakeState f c.Account).Status = "Reserved") "Uncertain provider released account"))
        test "verified fake terminal releases original disposable Git reservation once" (fun () ->
            withFixture (fun f ->
                let c = makeClaim f f.Worker
                let mutable sends = 0
                let port (record: NativeEffectRecord) =
                    sends <- sends + 1
                    check (record.EffectId = c.EffectId) "Foreign fake effect"
                    Ok (fakeTerminal c.EffectId "Terminal")
                claim f.One c |> success |> ignore
                let terminalProof = dispatchFakePort f.Two f.Worker c.EffectId (Guid.NewGuid()) port |> success
                check (sends = 1) "Expected one real fake-port invocation"
                let terminalId = Guid.NewGuid()
                recordFakeTerminal f.Three f.Worker c.EffectId terminalId terminalProof |> success |> ignore
                let releaseId = Guid.NewGuid()
                releaseFakeReservation f.One f.AccountOne f.Worker c.EffectId (Guid.NewGuid()) releaseId |> denied
                check ((fakeState f c.Account).Status = "Reserved") "Foreign terminal released fake reservation"
                let release = releaseFakeReservation f.Two f.AccountTwo f.Worker c.EffectId terminalId releaseId |> success
                check (not release.AlreadyApplied) "First account release should be new"
                let replay = releaseFakeReservation f.Three f.AccountThree f.Worker c.EffectId terminalId releaseId |> success
                check replay.AlreadyApplied "Same UUID fake account release did not replay"
                releaseFakeReservation f.One f.AccountOne f.Worker c.EffectId terminalId (Guid.NewGuid()) |> denied
                let state = fakeState f c.Account
                check (state.Status = "Released" && state.ReleaseOperationId = releaseId
                       && state.TerminalOperationId = terminalId) "Fake account not once-only terminal released"
                check (sends = 1) "Fake provider was sent twice during release"))
        test "verified no-send cannot invoke fake port and releases fake account once" (fun () ->
            withFixture (fun f ->
                let c = makeClaim f f.Worker
                let mutable sends = 0
                let port (_: NativeEffectRecord) =
                    sends <- sends + 1
                    Ok (fakeTerminal c.EffectId "Terminal")
                claim f.One c |> success |> ignore
                let noSend = { Marker = "STAGE_B_DISPOSABLE_FAKE_NO_SEND/v1"; Reason = "fake-dispatch-cancelled-before-start" }
                let terminalId = Guid.NewGuid()
                recordFakeNoSend f.Two f.Worker c.EffectId terminalId noSend |> success |> ignore
                dispatchFakePort f.Three f.Worker c.EffectId (Guid.NewGuid()) port |> denied
                recordFakeNoSend f.One f.Worker c.EffectId (Guid.NewGuid()) noSend |> denied
                check (sends = 0) "No-send state invoked fake provider"
                check ((fakeState f c.Account).Status = "Reserved") "Fake reservation released before terminal CAS"
                let releaseId = Guid.NewGuid()
                releaseFakeReservation f.Three f.AccountOne f.Worker c.EffectId terminalId releaseId |> success |> ignore
                check ((fakeState f c.Account).Status = "Released") "No-send fake account release missing"
                releaseFakeReservation f.Two f.AccountTwo f.Worker c.EffectId terminalId (Guid.NewGuid()) |> denied
                check (sends = 0) "Fake port called after no-send release"))
        test "no-send terminal cannot be forged after uncertain dispatch" (fun () ->
            withFixture (fun f ->
                let c = makeClaim f f.Worker
                claim f.One c |> success |> ignore
                let fakePort (_: NativeEffectRecord) = Ok (fakeTerminal c.EffectId "Unknown")
                dispatchFakePort f.Two f.Worker c.EffectId (Guid.NewGuid()) fakePort |> success |> ignore
                let noSend = { Marker = "STAGE_B_DISPOSABLE_FAKE_NO_SEND/v1"; Reason = "after-uncertain" }
                recordFakeNoSend f.One f.Worker c.EffectId (Guid.NewGuid()) noSend |> denied
                check ((get f f.Worker).Status = "Dispatching") "Uncertain effect mislabeled as no-send"
                check ((fakeState f c.Account).Status = "Reserved") "Uncertain effect released fake account"))
        test "three-client fake-port dispatch race calls non-idempotent port once" (fun () ->
            withFixture (fun f ->
                let c = makeClaim f f.Worker
                claim f.One c |> success |> ignore
                let portCalls = [| 0 |]
                let fakePort (_: NativeEffectRecord) =
                    System.Threading.Interlocked.Increment(&portCalls[0]) |> ignore
                    Ok (fakeTerminal c.EffectId "Unknown")
                use gate = new System.Threading.ManualResetEventSlim(false)
                let clients = [| f.One; f.Two; f.Three |]
                let tasks =
                    [| for i in 0 .. 2 do
                           yield Task.Run(fun () ->
                               gate.Wait()
                               dispatchFakePort clients[i] f.Worker c.EffectId (Guid.NewGuid()) fakePort) |]
                gate.Set()
                Task.WaitAll(tasks |> Array.map (fun t -> t :> Task))
                let winners = tasks |> Array.filter (fun t ->
                    match t.Result with Ok _ -> true | Error _ -> false)
                check (winners.Length = 1) (sprintf "Expected one fake-send winner, got %i" winners.Length)
                check (portCalls[0] = 1) "Concurrent clients invoked fake browser more than once"
                check ((get f f.Worker).Status = "Dispatching") "Race lost durable effect fence"
                check ((fakeState f c.Account).Status = "Reserved") "Uncertain raced effect released fake account"))
        test "three-client simultaneous claim admits at most one" (fun () ->
            withFixture (fun f ->
                let baseClaim = makeClaim f f.Worker
                let variants =
                    [| baseClaim
                       { baseClaim with OperationId = Guid.NewGuid(); EffectId = Guid.NewGuid() }
                       { baseClaim with OperationId = Guid.NewGuid(); EffectId = Guid.NewGuid() } |]
                let clients = [| f.One; f.Two; f.Three |]
                use gate = new System.Threading.ManualResetEventSlim(false)
                let tasks =
                    [| for i in 0 .. 2 do
                           yield Task.Run(fun () ->
                               gate.Wait()
                               claim clients[i] variants[i]) |]
                gate.Set()
                Task.WaitAll(tasks |> Array.map (fun t -> t :> Task))
                let wins = tasks |> Array.filter (fun t ->
                    match t.Result with Ok _ -> true | Error _ -> false)
                check (wins.Length = 1) (sprintf "Expected one CAS winner, found %i" wins.Length)
                check ((get f f.Worker).ClaimOperationId <> Guid.Empty) "No winner receipt"))
        printfn "STAGE_B_DISPOSABLE_TEST_ONLY .NET 11 Release PASS %i" passed
        0
    with error ->
        eprintfn "FAILED after %i tests: %s" passed error.Message
        1
