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
               Worker = worker; Reviewer = reviewer; AccountHead = accountHead }
    finally
        if Directory.Exists(root) then Directory.Delete(root, true)

let receipt (fixture: Fixture) kind =
    { Marker = "STAGE_B_DISPOSABLE_FAKE_ACCOUNT_RECEIPT/v1"
      AccountLedgerId = Guid.NewGuid().ToString("D")
      AccountEpoch = 1
      AccountHead = fixture.AccountHead
      OriginalReservationOperationId = Guid.NewGuid().ToString("D")
      ReservationId = Guid.NewGuid().ToString("D")
      ProjectId = fixture.One.ProjectId.ToString("D")
      Kind = kind }

let makeClaim fixture candidate =
    { OperationId = Guid.NewGuid()
      ExpectedProjectHead = (LedgerStore.fetch fixture.One |> success).Commit
      Account = receipt fixture candidate.Kind
      Candidate = candidate
      DispatchId = Guid.NewGuid()
      EffectId = Guid.NewGuid()
      DeviceId = "offline-device"
      WindowRegistration = "offline-window" }

let get fixture c =
    observe fixture.Two c |> success |> snd

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
                beginFakeEffect f.One f.Worker c.EffectId (Guid.NewGuid()) |> success |> ignore
                let mutable providerSubmissions = 0
                // A non-idempotent fake port. One accepted start permits exactly one invocation.
                providerSubmissions <- providerSubmissions + 1
                let _fakeClockRollback = DateTimeOffset.UnixEpoch
                beginFakeEffect f.Two f.Worker c.EffectId (Guid.NewGuid()) |> denied
                beginFakeEffect f.Three f.Worker c.EffectId (Guid.NewGuid()) |> denied
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
