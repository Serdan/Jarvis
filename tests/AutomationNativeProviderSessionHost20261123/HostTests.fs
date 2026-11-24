module Client.Tests.AutomationNativeProviderSessionHost20261123Tests

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Sockets
open System.Net.WebSockets
open System.Text
open System.Threading
open System.Threading.Tasks
open Client.AutomationNativeProviderSessionHost
open System.Security.Cryptography
open System.Text.Json
open NUnit.Framework
open Client.AccountCapacityLedger
open Client.LedgerStore
open Client.NativeSchedulerRosterPolicy
open Client.NativeSchedulerAccountReservation
open Client.ReviewWorkflow
open Client.AutomationNativeAccountProjectIngress

type private ConfiguredFixture =
    { Root: string
      AccountOne: AccountBinding
      AccountTwo: AccountBinding
      ProjectOne: LedgerBinding
      ProjectTwo: LedgerBinding }

module private ConfiguredFixture =
    let git directory args =
        use proc = new Process()
        proc.StartInfo.FileName <- "git"
        proc.StartInfo.WorkingDirectory <- directory
        proc.StartInfo.RedirectStandardOutput <- true
        proc.StartInfo.RedirectStandardError <- true
        proc.StartInfo.Environment["GIT_TERMINAL_PROMPT"] <- "0"
        proc.StartInfo.Environment["GIT_NO_REPLACE_OBJECTS"] <- "1"
        args |> List.iter proc.StartInfo.ArgumentList.Add
        Assert.That(proc.Start(), Is.True)
        let stdout = proc.StandardOutput.ReadToEnd().Trim()
        let stderr = proc.StandardError.ReadToEnd()
        Assert.That(proc.WaitForExit(30000), Is.True, "Bounded offline git fixture timeout")
        Assert.That(proc.ExitCode, Is.EqualTo(0), stderr)
        stdout

    let success = function
        | Ok x -> x
        | Error e -> failwithf "Expected success; actual %A" e

    let private markPrivate (path: string) =
        if not (OperatingSystem.IsWindows()) then
            File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

    let private remote root name =
        let bare = Path.Combine(root, name + ".git")
        let local = Path.Combine(root, name + "-seed")
        git root ["init"; "--bare"; "--initial-branch=main"; bare] |> ignore
        git root ["clone"; bare; local] |> ignore
        git local ["config"; "user.name"; "Disposable Fixture"] |> ignore
        git local ["config"; "user.email"; "disposable@example.test"] |> ignore
        File.WriteAllText(Path.Combine(local,"seed.txt"), name)
        git local ["add"; "seed.txt"] |> ignore
        git local ["commit"; "-m"; "bounded source seed"] |> ignore
        git local ["push"; "-u"; "origin"; "HEAD"] |> ignore
        bare, local

    let withGit action =
        let root = Path.Combine(Path.GetTempPath(), "native-started-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        markPrivate root
        try
            let scope1 = Path.Combine(root, "wq10-private-client-one")
            let scope2 = Path.Combine(root, "wq10-private-client-two")
            Directory.CreateDirectory(scope1) |> ignore
            Directory.CreateDirectory(scope2) |> ignore
            markPrivate scope1
            markPrivate scope2
            let accountRemote, _ = remote root "account"
            let acc1Path = Path.Combine(scope1, "account")
            let acc2Path = Path.Combine(scope2, "account")
            git root ["clone"; accountRemote; acc1Path] |> ignore
            git root ["clone"; accountRemote; acc2Path] |> ignore
            let accountId = Guid.NewGuid()
            let accountLedgerId = Guid.NewGuid()
            let account path =
                { RepositoryPath = path; RemoteName = "origin"
                  ApprovedFetchUrl = accountRemote; ApprovedPushUrl = accountRemote
                  AccountId = accountId; LedgerId = accountLedgerId; Epoch = 1 }
            AccountCapacityLedger.initialize (account acc1Path) 2 |> success |> ignore
            let projectRemote, projectPath = remote root "project"
            let projectId, projectLedgerId = Guid.NewGuid(), Guid.NewGuid()
            let project path =
                { RepositoryPath = path; RemoteName = "origin"
                  ApprovedFetchUrl = projectRemote; ApprovedPushUrl = projectRemote
                  ProjectId = projectId; LedgerId = projectLedgerId; Epoch = 1 }
            LedgerStore.initialize (project projectPath) true |> success |> ignore
            let otherProject = Path.Combine(root, "project-other-client")
            git root ["clone"; projectRemote; otherProject] |> ignore
            let f =
                { Root = root; AccountOne = account acc1Path; AccountTwo = account acc2Path
                  ProjectOne = project projectPath; ProjectTwo = project otherProject }
            action f
        finally
            try Directory.Delete(root, true) with _ -> ()

    let projectBindings f account =
        if account = 1 then [| f.ProjectOne |] else [| f.ProjectTwo |]

    let configureLimits f version previous accountWorkerLimit projectWorkerLimit accountReviewerLimit projectReviewerLimit =
        let project = f.ProjectOne
        let account = f.AccountOne
        let initial = (AccountCapacityLedger.fetch account |> success).Head
        let authority =
            { ProjectId = project.ProjectId; LedgerId = project.LedgerId; Epoch = project.Epoch
              ApprovedFetchUrl = project.ApprovedFetchUrl; ApprovedPushUrl = project.ApprovedPushUrl }
        let policy =
            { SchemaVersion = 1
              AccountId = account.AccountId.ToString("D")
              AccountLedgerId = account.LedgerId.ToString("D")
              AccountEpoch = account.Epoch
              AccountAuthoritySha256 =
                  NativeSchedulerRosterPolicy.accountAuthoritySha256
                      (account.AccountId.ToString("D")) (account.LedgerId.ToString("D"))
                      account.Epoch account.ApprovedFetchUrl account.ApprovedPushUrl
              Version = version; MembershipComplete = true
              AccountLimits = { Worker = accountWorkerLimit; Reviewer = accountReviewerLimit }
              Projects =
                  [| { ProjectId = project.ProjectId.ToString("D")
                       Alias = "Disposable project"
                       LedgerId = project.LedgerId.ToString("D")
                       Epoch = project.Epoch
                       SourceRef = NativeSchedulerRosterPolicy.ProjectSourceRef
                       RemoteAuthoritySha256 = AccountCapacityLedger.projectAuthoritySha256 authority
                       Limits = { Worker = projectWorkerLimit; Reviewer = projectReviewerLimit } } |] }
        let update =
            { SchemaVersion = 1
              OperationId = Guid.NewGuid().ToString("D")
              Actor = "Configured account fixture"
              PreviousVersion = previous
              ExpectedAccountHead = initial
              Policy = policy }
        AccountCapacityLedger.setNativeRosterPolicy account [| project |] update
        |> success |> ignore

    let configureVersion f version previous accountWorkerLimit projectWorkerLimit =
        configureLimits f version previous accountWorkerLimit projectWorkerLimit 1 1

    let configure f = configureVersion f 1L 0L 1 1

    let coverage f account projects =
        let projection =
            AccountCapacityLedger.readNativeRosterPolicy account projects
        Assert.That(projection.Status, Is.EqualTo("PolicyVerified"),
                    String.concat ";" projection.HoldReasons)
        { AccountHead = projection.VerifiedAccountHead.Value
          ProjectHeads = projection.VerifiedProjectHeads
          ObservedAtUtc = DateTimeOffset.UtcNow }

    let private json value =
        JsonSerializer.Serialize(value, JsonSerializerOptions(PropertyNamingPolicy=JsonNamingPolicy.CamelCase))

    let startedWorker f agent =
        let p = f.ProjectOne
        let originalHead = (LedgerStore.fetch p |> success).Commit
        let taskId = "started-" + Guid.NewGuid().ToString("N")
        let attemptId = Guid.NewGuid().ToString("N")
        let sourceRef = "refs/heads/disposable-source"
        let spec = "<h1>Native Started fixture</h1>"
        let specOid = LedgerStore.computeBlobOid p spec |> success
        let task: ReviewTask =
            { TaskId=taskId; Title="Native Started fixture"; Version=2
              Lifecycle="InProgress"; SpecVersion=1; AttemptId=attemptId }
        let attempt: ReviewAttempt =
            { TaskId=taskId; AttemptId=attemptId; Version=3
              OwnerAgentId=agent; Phase="Started"
              AssignmentGeneration=1; Priority=0; IssuedSequence=1L
              SpecBlobOid=specOid; BaseCommit=originalHead
              TargetRef="refs/heads/main"
              SourceRepositoryId=String.replicate 64 "a"
              SourceRemote=p.ApprovedFetchUrl; SourcePushRemote=p.ApprovedPushUrl
              SourceRef=sourceRef }
        let attemptRaw = json attempt
        LedgerStore.transact p
            { OperationId=Guid.NewGuid(); Actor="Disposable issuer"
              Kind="NativeWorkerIssued"
              RequestJson=json {| taskId=taskId; attemptId=attemptId |} }
            (fun _ -> Ok [
                { Path=$"tasks/{taskId}/task.json"; ExpectedBlobOid=None; Content=json task }
                { Path=$"tasks/{taskId}/spec.html"; ExpectedBlobOid=None; Content=spec }
                { Path=$"tasks/{taskId}/attempts/{attemptId}.json"; ExpectedBlobOid=None; Content=attemptRaw } ])
        |> success |> ignore
        { DispatchId=Guid.NewGuid().ToString("D")
          EffectOperationId=Guid.NewGuid().ToString("D")
          Kind="Worker"
          ProjectId=p.ProjectId.ToString("D")
          ProjectLedgerId=p.LedgerId.ToString("D")
          ProjectEpoch=p.Epoch
          OwnerAgentId=attempt.OwnerAgentId
          TaskId=taskId; AttemptId=attemptId
          AssignmentGeneration=int64 attempt.AssignmentGeneration
          SourceRepositoryId=attempt.SourceRepositoryId
          SourceBaseOid=originalHead
          SourceRef=sourceRef
          SourceLeaseSha256=NativeSchedulerRosterPolicy.sha256 attemptRaw
          ReviewId=""; ReviewVersion=0L; ReviewedHeadOid=""
          ReviewRef=""; ClaimOperationId="" }

    let reservedWithAgent f agent template =
        configure f
        ReviewWorkflow.registerAgent f.ProjectOne (Guid.NewGuid()) agent |> success |> ignore
        template |> Option.iter (fun body ->
            Client.RoleInstructions.RoleInstructions.setLaunchTemplate f.ProjectOne "Disposable fixture" (Guid.NewGuid()) 0 body
            |> success |> ignore)
        let c = startedWorker f agent
        let original = Guid.NewGuid()
        NativeSchedulerAccountReservation.reserve
            f.AccountOne [| f.ProjectOne |]
            (coverage f f.AccountOne [| f.ProjectOne |])
            original "Disposable native source" c
        |> success |> ignore
        let head = (LedgerStore.fetch f.ProjectOne |> success).Commit
        let accountHead = (AccountCapacityLedger.fetch f.AccountOne |> success).Head
        c, original,
        { OperationId=Guid.NewGuid(); ExpectedProjectHead=head
          AccountHead=accountHead; OriginalReservationOperationId=original
          Candidate=c; DeviceId="client-device"; WindowRegistration="window-disposable" }

    let reserved f template = reservedWithAgent f "Worker fixture" template

    let state f =
        let cover = coverage f f.AccountOne [| f.ProjectOne |]
        NativeSchedulerAccountReservation.readVerified
            f.AccountOne [| f.ProjectOne |] cover
        |> success |> snd
module private PairedBrowser =
    let port () =
        use listener = new TcpListener(IPAddress.Loopback, 0)
        listener.Start()
        (listener.LocalEndpoint :?> IPEndPoint).Port

    let send (socket: ClientWebSocket) (raw: string) =
        socket.SendAsync(ArraySegment<byte>(Encoding.UTF8.GetBytes raw),
                         WebSocketMessageType.Text, true, CancellationToken.None)

    let receive (socket: ClientWebSocket) = task {
        use stream = new MemoryStream()
        use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 5.0)
        let buffer = Array.zeroCreate<byte> 8192
        let mutable finished = false
        while not finished do
            let! part = socket.ReceiveAsync(ArraySegment<byte>(buffer), deadline.Token)
            if part.MessageType <> WebSocketMessageType.Text then failwith "Non-text bridge frame"
            stream.Write(buffer, 0, part.Count)
            finished <- part.EndOfMessage
        return Encoding.UTF8.GetString(stream.ToArray())
    }

    let pair port (secret: string) = task {
        let socket = new ClientWebSocket()
        socket.Options.SetRequestHeader("Origin", "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
        try
            use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 5.0)
            do! socket.ConnectAsync(Uri($"ws://127.0.0.1:{port}/"), deadline.Token)
            let! challenge = receive socket
            use document = JsonDocument.Parse challenge
            let nonce = document.RootElement.GetProperty("nonce").GetString()
            use hmac = new HMACSHA256(Encoding.UTF8.GetBytes secret)
            let proof = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes nonce)).ToLowerInvariant()
            do! send socket (JsonSerializer.Serialize({| version = 2; kind = "authenticate"; proof = proof |}))
            let! ready = receive socket
            use receipt = JsonDocument.Parse ready
            Assert.That(receipt.RootElement.GetProperty("kind").GetString(), Is.EqualTo("ready"))
            return socket
        with e ->
            socket.Dispose()
            return raise e
    }

    let register (socket: ClientWebSocket) (bridge: Client.BrowserBridge.Service) expectedCount = task {
        do! send socket """{"version":2,"kind":"windows","windows":[{"windowRegistration":"window-disposable","alias":"fixture","availability":"ready","registrationGeneration":7}]}"""
        let mutable registered = false
        for _ in 1..100 do
            if not registered then
                match bridge.ListWindows() with
                | Ok listing ->
                    use doc = JsonDocument.Parse listing
                    let count =
                        doc.RootElement.GetProperty("windows").EnumerateArray()
                        |> Seq.filter (fun w -> w.GetProperty("windowRegistration").GetString() = "window-disposable")
                        |> Seq.length
                    if count = expectedCount then registered <- true else do! Task.Delay 25
                | _ -> do! Task.Delay 25
        Assert.That(registered, Is.True, "Authenticated window did not register")
    }

    let withPaired action =
        let p = port()
        let secret = Guid.NewGuid().ToString("N")
        use bridge = new Client.BrowserBridge.Service(p, secret)
        bridge.Start()
        use socket = (pair p secret).GetAwaiter().GetResult()
        (register socket bridge 1).GetAwaiter().GetResult()
        action bridge

let private template = "{{agentId}} assigned to {{projectName}}."
let private prepare f account project bridge timeout claim =
    let projects = [| project |]
    prepareWorker account projects
        (ConfiguredFixture.coverage f account projects) bridge "Disposable" timeout claim

let private assertNoIntent project claim =
    let snapshot = LedgerStore.fetch project |> ConfiguredFixture.success
    let operation = LedgerStore.getOperationAt project snapshot claim.OperationId |> ConfiguredFixture.success
    Assert.That(operation.IsNone, Is.True)
    let id = Guid.Parse claim.Candidate.EffectOperationId
    let path = $"records/native-scheduler/launch-intents/{id:N}.json"
    let value, _ = LedgerStore.readAt project snapshot path |> ConfiguredFixture.success
    Assert.That(value.IsNone, Is.True)
    let arguments, _ = LedgerStore.readAt project snapshot $"records/native-scheduler/launch-arguments/{id:N}.json" |> ConfiguredFixture.success
    Assert.That(arguments.IsNone, Is.True)

[<Test>]
let ``paired browser and verified Started CAS pins exact immutable prompt and effect`` () =
    ConfiguredFixture.withGit (fun f ->
        let c, original, claim = ConfiguredFixture.reserved f (Some template)
        PairedBrowser.withPaired (fun bridge ->
            let before = LedgerStore.fetch f.ProjectOne |> ConfiguredFixture.success
            let rendered =
                Client.RoleInstructions.RoleInstructions.renderLaunchPromptAt f.ProjectOne before "Disposable" c.OwnerAgentId
                |> ConfiguredFixture.success
            let write = prepare f f.AccountOne f.ProjectOne bridge 27000 claim |> ConfiguredFixture.success
            let origin, blob, pinned, arguments =
                readOriginalIntent f.ProjectOne claim.OperationId (Guid.Parse c.EffectOperationId)
                |> ConfiguredFixture.success
            Assert.That(write.AlreadyApplied, Is.False)
            Assert.That(origin, Is.EqualTo(write.CurrentHead))
            Assert.That(blob.Length, Is.GreaterThan(0))
            Assert.That(pinned.Claim, Is.EqualTo(claim))
            Assert.That(pinned.AccountGeneration, Is.EqualTo(1L))
            Assert.That(pinned.TemplateVersion, Is.EqualTo(rendered.TemplateVersion))
            Assert.That(pinned.TemplateBlobOid, Is.EqualTo(rendered.TemplateBlobOid))
            Assert.That(pinned.PromptSha256, Is.EqualTo(rendered.PromptSha256))
            let owner, _ = bridge.TryGetExactWindowOwner(claim.WindowRegistration) |> ConfiguredFixture.success
            Assert.That(pinned.ObservedConnectionId, Is.EqualTo(owner))
            Assert.That(pinned.ObservedWindowGeneration, Is.EqualTo(7))
            use args = JsonDocument.Parse arguments
            Assert.That(args.RootElement.GetProperty("prompt").GetString(), Is.EqualTo(rendered.Prompt))
            Assert.That(args.RootElement.GetProperty("timeoutMs").GetInt32(), Is.EqualTo(27000))
            Assert.That(Client.BrowserArguments.fits arguments, Is.True)
            let originalRecord = (ConfiguredFixture.state f).Records[0]
            Assert.That(originalRecord.OriginalOperationId, Is.EqualTo(string original))
            Assert.That(originalRecord.Status, Is.EqualTo("Reserved"), "Preparation cannot send or release")
            Assert.That(authorizeProviderDispatch().IsError, Is.True)
            Assert.That(releaseFromProviderTerminal().IsError, Is.True)
            // The original UUID is not reusable, even with a new timeout.
            Assert.That((prepare f f.AccountOne f.ProjectOne bridge 26000 claim).IsError, Is.True)
            let newHead = LedgerStore.fetch f.ProjectTwo |> ConfiguredFixture.success
            let second = { claim with OperationId = Guid.NewGuid(); ExpectedProjectHead = newHead.Commit }
            Assert.That((prepare f f.AccountTwo f.ProjectTwo bridge 27000 second).IsError, Is.True)
            // Template change after commitment cannot change the original bytes.
            Client.RoleInstructions.RoleInstructions.setLaunchTemplate f.ProjectOne "Fixture" (Guid.NewGuid()) 1
                "{{agentId}} revised {{projectName}}" |> ConfiguredFixture.success |> ignore
            let _, _, _, reread =
                readOriginalIntent f.ProjectTwo claim.OperationId (Guid.Parse c.EffectOperationId)
                |> ConfiguredFixture.success
            Assert.That(reread, Is.EqualTo(arguments))))

[<Test>]
let ``missing actual paired window leaves original occupancy and project untouched`` () =
    ConfiguredFixture.withGit (fun f ->
        let _, _, claim = ConfiguredFixture.reserved f (Some template)
        use bridge = new Client.BrowserBridge.Service(PairedBrowser.port(), Guid.NewGuid().ToString("N"))
        bridge.Start()
        Assert.That((prepare f f.AccountOne f.ProjectOne bridge 27000 claim).IsError, Is.True)
        assertNoIntent f.ProjectOne claim
        Assert.That((ConfiguredFixture.state f).Records[0].Status, Is.EqualTo("Reserved")))

[<Test>]
let ``template revision between reservation and claim refuses stale project head`` () =
    ConfiguredFixture.withGit (fun f ->
        let _, _, claim = ConfiguredFixture.reserved f (Some template)
        Client.RoleInstructions.RoleInstructions.setLaunchTemplate f.ProjectOne "Fixture" (Guid.NewGuid()) 1
            "{{agentId}} revised {{projectName}}" |> ConfiguredFixture.success |> ignore
        PairedBrowser.withPaired (fun bridge ->
            Assert.That((prepare f f.AccountOne f.ProjectOne bridge 27000 claim).IsError, Is.True)
            assertNoIntent f.ProjectOne claim))

[<Test>]
let ``missing canonical template refuses preparation before original CAS`` () =
    ConfiguredFixture.withGit (fun f ->
        let _, _, claim = ConfiguredFixture.reserved f None
        PairedBrowser.withPaired (fun bridge ->
            Assert.That((prepare f f.AccountOne f.ProjectOne bridge 27000 claim).IsError, Is.True)
            assertNoIntent f.ProjectOne claim))

[<Test>]
let ``reviewer identity cannot be reinterpreted as Worker ingress`` () =
    ConfiguredFixture.withGit (fun f ->
        let _, _, claim = ConfiguredFixture.reserved f (Some template)
        let reviewer = { claim with Candidate = { claim.Candidate with Kind = "Reviewer" } }
        PairedBrowser.withPaired (fun bridge ->
            Assert.That((prepare f f.AccountOne f.ProjectOne bridge 27000 reviewer).IsError, Is.True)
            assertNoIntent f.ProjectOne claim))

[<Test>]
let ``full envelope beyond bridge budget is rejected despite valid minimal prompt`` () =
    ConfiguredFixture.withGit (fun f ->
        let agent = String.replicate 40 "界"
        let body = String.replicate 545 "{{agentId}}" + "{{projectName}}" + String.replicate 113 "x"
        let c, _, claim = ConfiguredFixture.reservedWithAgent f agent (Some body)
        let snapshot = LedgerStore.fetch f.ProjectOne |> ConfiguredFixture.success
        let rendered = Client.RoleInstructions.RoleInstructions.renderLaunchPromptAt
                            f.ProjectOne snapshot "Disposable" c.OwnerAgentId |> ConfiguredFixture.success
        Assert.That(Client.BrowserArguments.serializeMinimalLaunchPrompt rendered.Prompt
                    |> Client.BrowserArguments.byteCount, Is.EqualTo(Client.BrowserArguments.MaxBytes))
        PairedBrowser.withPaired (fun bridge ->
            match prepare f f.AccountOne f.ProjectOne bridge 27000 claim with
            | Error(InvalidBinding reason) -> Assert.That(reason, Does.Contain("Full serialized browser launch arguments"))
            | other -> Assert.Fail(sprintf "Expected full-envelope rejection, received %A" other)
            assertNoIntent f.ProjectOne claim
            Assert.That((ConfiguredFixture.state f).Records[0].Status, Is.EqualTo("Reserved"))))

[<Test>]
let ``duplicate window registration from a second authenticated connection cannot claim`` () =
    ConfiguredFixture.withGit (fun f ->
        let _, _, claim = ConfiguredFixture.reserved f (Some template)
        let p = PairedBrowser.port()
        let secret = Guid.NewGuid().ToString("N")
        use bridge = new Client.BrowserBridge.Service(p, secret)
        bridge.Start()
        use first = (PairedBrowser.pair p secret).GetAwaiter().GetResult()
        (PairedBrowser.register first bridge 1).GetAwaiter().GetResult()
        use second = (PairedBrowser.pair p secret).GetAwaiter().GetResult()
        (PairedBrowser.register second bridge 2).GetAwaiter().GetResult()
        Assert.That((prepare f f.AccountOne f.ProjectOne bridge 27000 claim).IsError, Is.True)
        assertNoIntent f.ProjectOne claim
        Assert.That((ConfiguredFixture.state f).Records[0].Status, Is.EqualTo("Reserved")))

[<Test>]
let ``full wire envelope at exact bridge limit survives durable Git round trip`` () =
    ConfiguredFixture.withGit (fun f ->
        let agent = String.replicate 40 "界"
        // The minimal 65536-byte fixture above loses exactly 18 bytes to
        // accommodate the comma, timeout property and value.
        let body = String.replicate 545 "{{agentId}}" + "{{projectName}}" + String.replicate 95 "x"
        let c, _, claim = ConfiguredFixture.reservedWithAgent f agent (Some body)
        PairedBrowser.withPaired (fun bridge ->
            prepare f f.AccountOne f.ProjectOne bridge 27000 claim |> ConfiguredFixture.success |> ignore
            let _, _, pinned, arguments =
                readOriginalIntent f.ProjectTwo claim.OperationId (Guid.Parse c.EffectOperationId)
                |> ConfiguredFixture.success
            Assert.That(Client.BrowserArguments.byteCount arguments, Is.EqualTo(Client.BrowserArguments.MaxBytes))
            Assert.That(pinned.ArgumentsSha256, Is.EqualTo(NativeSchedulerRosterPolicy.sha256 arguments))
            Assert.That((ConfiguredFixture.state f).Records[0].Status, Is.EqualTo("Reserved"))))

[<Test>]
let ``rewritten original argument blob is denied even if JSON remains valid`` () =
    ConfiguredFixture.withGit (fun f ->
        let c, _, claim = ConfiguredFixture.reserved f (Some template)
        PairedBrowser.withPaired (fun bridge ->
            prepare f f.AccountOne f.ProjectOne bridge 27000 claim |> ConfiguredFixture.success |> ignore
            let id = Guid.Parse c.EffectOperationId
            let path = $"records/native-scheduler/launch-arguments/{id:N}.json"
            LedgerStore.transact f.ProjectOne
                { OperationId=Guid.NewGuid(); Actor="Disposable tamper fixture"
                  Kind="TestRewrittenOriginalArgs"; RequestJson="{}" }
                (fun snapshot ->
                    LedgerStore.readAt f.ProjectOne snapshot path
                    |> Result.bind (fun (_, oid) ->
                        Ok [ { Path=path; ExpectedBlobOid=oid
                               Content="""{"prompt":"rewritten","timeoutMs":27000}""" } ]))
            |> ConfiguredFixture.success |> ignore
            Assert.That((readOriginalIntent f.ProjectTwo claim.OperationId id).IsError, Is.True)
            Assert.That((ConfiguredFixture.state f).Records[0].Status, Is.EqualTo("Reserved"))))

[<Test>]
let ``two separately cloned clients can durably claim original launch only once`` () =
    ConfiguredFixture.withGit (fun f ->
        let c, _, claim = ConfiguredFixture.reserved f (Some template)
        PairedBrowser.withPaired (fun bridge ->
            let second = { claim with OperationId = Guid.NewGuid() }
            use barrier = new Barrier(2)
            let race account project candidate =
                Task.Run<Result<LedgerWrite, LedgerFailure>>(fun () ->
                    if not (barrier.SignalAndWait(TimeSpan.FromSeconds 10.0)) then failwith "Race fixture timed out"
                    prepare f account project bridge 27000 candidate)
            let firstJob = race f.AccountOne f.ProjectOne claim
            let secondJob = race f.AccountTwo f.ProjectTwo second
            Task.WaitAll([| firstJob :> Task; secondJob :> Task |])
            let outcomes = [ firstJob.Result; secondJob.Result ]
            Assert.That(outcomes |> List.filter Result.isOk |> List.length, Is.EqualTo(1))
            let winner, loser = if Result.isOk firstJob.Result then claim, second else second, claim
            let id = Guid.Parse c.EffectOperationId
            Assert.That((readOriginalIntent f.ProjectOne winner.OperationId id).IsOk, Is.True)
            Assert.That((readOriginalIntent f.ProjectTwo loser.OperationId id).IsError, Is.True)
            Assert.That((ConfiguredFixture.state f).Records[0].Status, Is.EqualTo("Reserved"))))


[<Test>]
let ``compiled default off host registers only existing B1 path and never reads or executes`` () =
    let mutable calls = 0
    let unexpected () =
        calls <- calls + 1
        failwith "Disabled host called authority port"
    let ports : Client.WorkQueueReactiveWake.WorkWakePorts =
        { ReadVerified = unexpected
          Plan = fun _ -> unexpected()
          Execute = fun _ -> unexpected() }
    let coordinator = Client.WorkQueueReactiveWake.WorkQueueReactiveWake.Coordinator(ports)
    let messages = ResizeArray<string>()
    let hostPorts = defaultOffHostPorts coordinator messages.Add
    let host, onCommitted =
        Client.AutomationChallengeWakeAdapter.registerHost false hostPorts
            (fun _ -> unexpected()) messages.Add
    use host = host
    Assert.That(typeof<OriginalLaunchIntent>.Assembly, Is.EqualTo(typeof<Client.BrowserBridge.Service>.Assembly))
    Assert.That(messages.Count, Is.EqualTo(1))
    Assert.That(messages[0], Does.Contain("HOLD").And.Contain("ProviderTerminalUnavailable"))
    Assert.That(host.IsRunning, Is.False)
    Assert.That(host.Start(), Is.False)
    Assert.That(host.Notify Client.WorkQueueReactiveWake.WorkWakeTrigger.ManualReconcile, Is.False)
    Assert.That(host.RunOnce [Client.WorkQueueReactiveWake.WorkWakeTrigger.ManualReconcile],
                Is.EqualTo(Client.WorkQueueSchedulerHostLoop.SchedulerHostOutcome.Disabled))
    onCommitted Unchecked.defaultof<_> Unchecked.defaultof<_>
    host.StopAsync().GetAwaiter().GetResult()
    Assert.That(calls, Is.EqualTo(0))
    Assert.That(authorizeProviderDispatch() = Error(productionHolds()), Is.True)
    Assert.That(releaseFromProviderTerminal() = Error ProviderTerminalUnavailable, Is.True)

[<Test>]
let ``exact authenticated owner distinguishes same generation reconnect and refuses alias or malformed registrations`` () =
    let p = PairedBrowser.port()
    let secret = Guid.NewGuid().ToString("N")
    use bridge = new Client.BrowserBridge.Service(p, secret)
    bridge.Start()
    let lookup () = bridge.TryGetExactWindowOwner("window-disposable")
    Assert.That(lookup().IsError, Is.True)
    use first = (PairedBrowser.pair p secret).GetAwaiter().GetResult()
    (PairedBrowser.register first bridge 1).GetAwaiter().GetResult()
    let owner, generation = lookup() |> ConfiguredFixture.success
    Assert.That(generation, Is.EqualTo(7))
    Assert.That(bridge.TryGetExactWindowOwner("fixture").IsError, Is.True)
    let publish generation availability =
        let raw = """{"version":2,"kind":"windows","windows":[{"windowRegistration":"window-disposable","alias":"fixture","availability":"AVAIL","registrationGeneration":GEN}]}"""
                      .Replace("AVAIL", availability).Replace("GEN", generation)
        (PairedBrowser.send first raw).GetAwaiter().GetResult()
        Assert.That(SpinWait.SpinUntil((fun () ->
            match bridge.ListWindows() with
            | Ok listing ->
                use doc = JsonDocument.Parse listing
                let windows = doc.RootElement.GetProperty("windows")
                windows.GetArrayLength() = 1 &&
                windows[0].GetProperty("registrationGeneration").GetRawText() = generation &&
                windows[0].GetProperty("availability").GetString() = availability
            | Error _ -> false), TimeSpan.FromSeconds 5.0), Is.True)
    publish "8" "ready"
    Assert.That(lookup() = Ok(owner, 8), Is.True)
    publish "999999999999" "ready"
    Assert.That(lookup().IsError, Is.True)
    publish "8" "unavailable"
    Assert.That(lookup().IsError, Is.True)
    first.Abort()
    Assert.That(SpinWait.SpinUntil((fun () -> bridge.ListWindows().IsError), TimeSpan.FromSeconds 5.0), Is.True)
    use second = (PairedBrowser.pair p secret).GetAwaiter().GetResult()
    (PairedBrowser.register second bridge 1).GetAwaiter().GetResult()
    let replacementOwner, replacementGeneration = lookup() |> ConfiguredFixture.success
    Assert.That(replacementGeneration, Is.EqualTo(generation))
    Assert.That(replacementOwner, Is.Not.EqualTo(owner))
    // Neither authenticated transport connection proves a provider terminal.
    Assert.That(releaseFromProviderTerminal().IsError, Is.True)


// Stage C uses a real independent BranchReview claim and frozen source ref,
// not a worker record with Kind rewritten or a fabricated account receipt.
let private claimedReviewer f =
    let p = f.ProjectOne
    let agent = "Reviewer fixture"
    ReviewWorkflow.registerAgent p (Guid.NewGuid()) agent |> ConfiguredFixture.success |> ignore
    Client.RoleInstructions.RoleInstructions.createRole p "Disposable fixture" (Guid.NewGuid())
        "Reviewer" "Independent review" "Review exact frozen source independently."
    |> ConfiguredFixture.success |> ignore
    Client.RoleInstructions.RoleInstructions.attachRole p "Disposable fixture" (Guid.NewGuid())
        agent "Reviewer" 0 |> ConfiguredFixture.success |> ignore
    Client.RoleInstructions.RoleInstructions.setLaunchTemplate p "Disposable fixture"
        (Guid.NewGuid()) 0 template |> ConfiguredFixture.success |> ignore
    let source = (LedgerStore.fetch p |> ConfiguredFixture.success).Commit
    let taskId = "review-fixture"
    let reviewId = Guid.NewGuid()
    let claimId = Guid.NewGuid()
    let reviewRef = $"refs/heads/jarvis-review/{taskId}/{reviewId:N}/r1"
    ReviewWorkflow.submitBranchReview p (Guid.NewGuid()) taskId
        "Worker fixture" reviewId "" source "Disposable native reviewer"
        "Review exact source" "" |> ConfiguredFixture.success |> ignore
    ReviewWorkflow.claimBranchReview p claimId taskId reviewId agent 1
    |> ConfiguredFixture.success |> ignore
    let c : NativeDispatchCandidate =
        { DispatchId = string (Guid.NewGuid()); EffectOperationId = string (Guid.NewGuid())
          Kind = "Reviewer"; ProjectId = string p.ProjectId
          ProjectLedgerId = string p.LedgerId; ProjectEpoch = p.Epoch
          OwnerAgentId = agent; TaskId = ""; AttemptId = ""; AssignmentGeneration = 0L
          SourceRepositoryId = ""; SourceBaseOid = ""; SourceRef = ""; SourceLeaseSha256 = ""
          ReviewId = reviewId.ToString("N"); ReviewVersion = 2L
          ReviewedHeadOid = source; ReviewRef = reviewRef; ClaimOperationId = string claimId }
    let original = Guid.NewGuid()
    let claim : ReviewerProjectClaim =
        { OperationId = Guid.NewGuid()
          ExpectedProjectHead = (LedgerStore.fetch p |> ConfiguredFixture.success).Commit
          AccountHead = (AccountCapacityLedger.fetch f.AccountOne |> ConfiguredFixture.success).Head
          OriginalReservationOperationId = original; ReviewCandidate = c
          DeviceId = "client-device"; WindowRegistration = "window-disposable" }
    taskId, reviewId, claim

// Real account reservation of a canonical claimed review.
let private reservedReviewer f =
    ConfiguredFixture.configure f
    let taskId, reviewId, claim = claimedReviewer f
    let projects = [| f.ProjectOne |]
    NativeSchedulerAccountReservation.reserve
            f.AccountOne projects
        (ConfiguredFixture.coverage f f.AccountOne projects) claim.OriginalReservationOperationId
        "Configured account fixture" claim.ReviewCandidate
    |> ConfiguredFixture.success |> ignore
    taskId, reviewId, { claim with AccountHead = (AccountCapacityLedger.fetch f.AccountOne |> ConfiguredFixture.success).Head }

// Only the negative legacy Worker test constructs this deliberately
// mismatched type; the real reviewer fixture remains a distinct claim.
let private asWorkerForNegativeTest (claim: ReviewerProjectClaim) : StartedProjectClaim =
    { OperationId = claim.OperationId; ExpectedProjectHead = claim.ExpectedProjectHead
      AccountHead = claim.AccountHead; OriginalReservationOperationId = claim.OriginalReservationOperationId
      Candidate = claim.ReviewCandidate; DeviceId = claim.DeviceId
      WindowRegistration = claim.WindowRegistration }

[<Test>]
let ``real configured reviewer reservation has canonical frozen claim and distinct Worker denial`` () =
    ConfiguredFixture.withGit (fun f ->
        let _, _, claim = reservedReviewer f
        let c = claim.ReviewCandidate
        let snapshot = LedgerStore.fetch f.ProjectTwo |> ConfiguredFixture.success
        Assert.That(snapshot.Commit, Is.EqualTo(claim.ExpectedProjectHead))
        LedgerStore.fetchReviewRef f.ProjectTwo c.ReviewRef c.ReviewedHeadOid
        |> ConfiguredFixture.success |> ignore
        let operation =
            LedgerStore.getOperationAt f.ProjectTwo snapshot (Guid.Parse c.ClaimOperationId)
            |> ConfiguredFixture.success |> Option.get
        Assert.That(operation.Type, Is.EqualTo("ClaimBranchReview"))
        LedgerStore.getOperationOriginAt f.ProjectTwo snapshot operation
        |> ConfiguredFixture.success |> ignore
        let rendered =
            Client.RoleInstructions.RoleInstructions.renderLaunchPromptAt
                f.ProjectTwo snapshot "Disposable" c.OwnerAgentId |> ConfiguredFixture.success
        Assert.That(rendered.Prompt, Is.EqualTo("Reviewer fixture assigned to Disposable."))
        let instructions =
            Client.RoleInstructions.RoleInstructions.resolveAt
                f.ProjectTwo snapshot c.OwnerAgentId |> ConfiguredFixture.success
        Assert.That(instructions.SnapshotCommit, Is.EqualTo(rendered.SnapshotCommit))
        Assert.That(instructions.RoleIds = ["Reviewer"], Is.True)
        Assert.That(instructions.Documents.Head.Content, Is.EqualTo("Review exact frozen source independently."))
        let launch = renderReviewerArgumentsAt f.ProjectTwo snapshot "Disposable" 27000 claim
                     |> ConfiguredFixture.success
        Assert.That(launch.Rendered, Is.EqualTo(rendered))
        Assert.That(launch.AgentConfigVersion, Is.EqualTo(instructions.AgentConfigVersion))
        Assert.That(launch.InstructionProvenanceSha256.Length, Is.EqualTo(64))
        Assert.That(launch.ArgumentsSha256, Is.EqualTo(NativeSchedulerRosterPolicy.sha256 launch.Arguments))
        use args = JsonDocument.Parse launch.Arguments
        Assert.That(args.RootElement.GetProperty("prompt").GetString(), Is.EqualTo(rendered.Prompt))
        Assert.That(args.RootElement.GetProperty("timeoutMs").GetInt32(), Is.EqualTo(27000))
        Assert.That(Client.BrowserArguments.fits launch.Arguments, Is.True)
        Assert.That((renderReviewerArgumentsAt f.ProjectTwo snapshot "Disposable" 27001 claim).IsError, Is.True)
        let state =
            NativeSchedulerAccountReservation.readVerified
                f.AccountTwo [| f.ProjectTwo |]
                (ConfiguredFixture.coverage f f.AccountTwo [| f.ProjectTwo |])
            |> ConfiguredFixture.success |> snd
        Assert.That(state.Records.Length, Is.EqualTo(1))
        Assert.That(state.Records[0].Candidate, Is.EqualTo(c))
        Assert.That(state.Records[0].Status, Is.EqualTo("Reserved"))
        PairedBrowser.withPaired (fun bridge ->
            let wrongPool = asWorkerForNegativeTest claim
            Assert.That((prepare f f.AccountTwo f.ProjectTwo bridge 27000 wrongPool).IsError, Is.True)
            assertNoIntent f.ProjectTwo wrongPool))

[<Test>]
let ``released reviewer claim leaves account occupancy but cannot prove current project authority`` () =
    ConfiguredFixture.withGit (fun f ->
        let taskId, reviewId, claim = reservedReviewer f
        ReviewWorkflow.releaseBranchReview f.ProjectOne (Guid.NewGuid()) taskId reviewId
            claim.ReviewCandidate.OwnerAgentId 2 |> ConfiguredFixture.success |> ignore
        let afterRelease = LedgerStore.fetch f.ProjectTwo |> ConfiguredFixture.success
        Assert.That(afterRelease.Commit, Is.Not.EqualTo(claim.ExpectedProjectHead))
        Assert.That((renderReviewerArgumentsAt f.ProjectTwo afterRelease "Disposable" 27000 claim).IsError, Is.True)
        // The account read is a valid occupancy observation, not a current
        // review claim verifier. The eventual project CAS must revalidate.
        let state = ConfiguredFixture.state f
        Assert.That(state.Records[0].Candidate, Is.EqualTo(claim.ReviewCandidate))
        Assert.That(state.Records[0].Status, Is.EqualTo("Reserved"))
        let changedHead = { claim with ExpectedProjectHead = afterRelease.Commit }
        PairedBrowser.withPaired (fun bridge ->
            let projects = [| f.ProjectTwo |]
            let cover = ConfiguredFixture.coverage f f.AccountTwo projects
            Assert.That((prepareReviewer f.AccountTwo projects cover bridge "Disposable" 27000 changedHead).IsError, Is.True)
            assertNoIntent f.ProjectTwo (asWorkerForNegativeTest changedHead)))

[<Test>]
let ``reviewer fence identity binds review version and original claim operation without worker fields`` () =
    ConfiguredFixture.withGit (fun f ->
        let _, _, claim = claimedReviewer f
        let c = claim.ReviewCandidate
        let path = reviewerFencePath claim |> ConfiguredFixture.success
        Assert.That(path, Does.StartWith("records/native-account-reviewer-effect-v1/"))
        let withCandidate candidate = { claim with ReviewCandidate = candidate }
        let changedVersion = withCandidate { c with ReviewVersion = c.ReviewVersion + 1L }
        let changedClaim = withCandidate { c with ClaimOperationId = string (Guid.NewGuid()) }
        let changedReview = withCandidate { c with ReviewId = Guid.NewGuid().ToString("N") }
        for different in [changedVersion; changedClaim; changedReview] do
            Assert.That(reviewerFencePath different |> ConfiguredFixture.success, Is.Not.EqualTo(path))
        // Retry UUIDs do not produce another path for the same canonical claim.
        Assert.That(reviewerFencePath { claim with OperationId = Guid.NewGuid() } |> ConfiguredFixture.success,
                    Is.EqualTo(path))
        for invalid in
            [ withCandidate { c with Kind = "Worker" }
              withCandidate { c with TaskId = "fake-attempt" }
              withCandidate { c with SourceLeaseSha256 = "fake-lease" }
              withCandidate { c with ClaimOperationId = c.EffectOperationId }
              { claim with OperationId = claim.OriginalReservationOperationId } ] do
            Assert.That((reviewerFencePath invalid).IsError, Is.True))

[<TestCase(true)>]
[<TestCase(false)>]
let ``reviewer journal reader binds original operation bytes and distinct fence`` includeFence =
    ConfiguredFixture.withGit (fun f ->
        // Reader fixture: seed a project-only journal using the ordinary Git
        // CAS. Deliberately no account reservation or signing. This tests
        // historical readback, not authorization of a real reviewer send.
        let _, _, claim = claimedReviewer f
        let snapshot = LedgerStore.fetch f.ProjectOne |> ConfiguredFixture.success
        PairedBrowser.withPaired (fun bridge ->
            let rendered = renderReviewerArgumentsAt f.ProjectOne snapshot "Disposable" 27000 claim
                           |> ConfiguredFixture.success
            let owner, generation = bridge.TryGetExactWindowOwner(claim.WindowRegistration)
                                    |> ConfiguredFixture.success
            let effectId = Guid.Parse claim.ReviewCandidate.EffectOperationId
            let pinned : OriginalReviewerLaunchIntent =
                { SchemaVersion = 1; ProjectName = "Disposable"; Claim = claim
                  AccountGeneration = 1L; CanonicalKey = String.replicate 64 "a"
                  TemplateVersion = rendered.Rendered.TemplateVersion
                  TemplateBlobOid = rendered.Rendered.TemplateBlobOid
                  PromptSha256 = rendered.Rendered.PromptSha256
                  ArgumentsSha256 = rendered.ArgumentsSha256
                  AgentConfigVersion = rendered.AgentConfigVersion
                  InstructionProvenanceSha256 = rendered.InstructionProvenanceSha256
                  ObservedConnectionId = owner; ObservedWindowGeneration = generation
                  State = "HeldBeforeSend" }
            let opts = JsonSerializerOptions(PropertyNamingPolicy=JsonNamingPolicy.CamelCase)
            let raw = JsonSerializer.Serialize(pinned, opts)
            let path = $"records/native-scheduler/launch-arguments/{effectId:N}.json"
            let fence = reviewerFencePath claim |> ConfiguredFixture.success
            let write =
                LedgerStore.transact f.ProjectOne
                    { OperationId=claim.OperationId; Actor=claim.ReviewCandidate.OwnerAgentId
                      Kind="NativeReviewerLaunchIntent"
                      RequestJson=JsonSerializer.Serialize(
                        {| claim=claim
                           launchIntentSha256=NativeSchedulerRosterPolicy.sha256 raw
                           argumentsSha256=rendered.ArgumentsSha256 |}, opts) }
                    (fun current ->
                        Assert.That(current.Commit, Is.EqualTo(claim.ExpectedProjectHead))
                        Ok [ { Path = $"records/native-scheduler/launch-intents/{effectId:N}.json"
                               ExpectedBlobOid=None; Content=raw }
                             { Path=path; ExpectedBlobOid=None; Content=rendered.Arguments }
                             if includeFence then
                                 { Path=fence; ExpectedBlobOid=None; Content="{}" } ])
                |> ConfiguredFixture.success
            Assert.That((readOriginalIntent f.ProjectTwo claim.OperationId effectId).IsError, Is.True)
            if not includeFence then
                Assert.That((readOriginalReviewerIntent f.ProjectTwo claim.OperationId effectId).IsError, Is.True)
            else
                let origin, _, read, args =
                    readOriginalReviewerIntent f.ProjectTwo claim.OperationId effectId |> ConfiguredFixture.success
                Assert.That(origin, Is.EqualTo(write.CurrentHead))
                Assert.That(read, Is.EqualTo(pinned))
                Assert.That(args, Is.EqualTo(rendered.Arguments))
                Assert.That((readOriginalReviewerIntent f.ProjectTwo (Guid.NewGuid()) effectId).IsError, Is.True)
                // A newer launch template cannot change a pinned intent.
                Client.RoleInstructions.RoleInstructions.setLaunchTemplate f.ProjectOne "Fixture" (Guid.NewGuid()) 1
                    "{{agentId}} revised {{projectName}}" |> ConfiguredFixture.success |> ignore
                let _, _, _, reread =
                    readOriginalReviewerIntent f.ProjectTwo claim.OperationId effectId |> ConfiguredFixture.success
                Assert.That(reread, Is.EqualTo(args))
                LedgerStore.transact f.ProjectOne
                    { OperationId=Guid.NewGuid(); Actor="Disposable tamper fixture"
                      Kind="TestRewrittenReviewerArguments"; RequestJson="{}" }
                    (fun current ->
                        LedgerStore.readAt f.ProjectOne current path
                        |> Result.map (fun (_, oid) ->
                            [ { Path=path; ExpectedBlobOid=oid
                                Content="""{"prompt":"rewritten","timeoutMs":27000}""" } ]))
                |> ConfiguredFixture.success |> ignore
                Assert.That((readOriginalReviewerIntent f.ProjectTwo claim.OperationId effectId).IsError, Is.True)))

[<Test>]
let ``real configured reviewer reservation prepares and journals once under paired bridge`` () =
    ConfiguredFixture.withGit (fun f ->
        let _, _, claim = reservedReviewer f
        let c = claim.ReviewCandidate
        PairedBrowser.withPaired (fun bridge ->
            let prepare account project original =
                let projects = [| project |]
                prepareReviewer account projects (ConfiguredFixture.coverage f account projects)
                    bridge "Disposable" 27000 original
            let second = { claim with OperationId = Guid.NewGuid() }
            use barrier = new Barrier(2)
            let race account project original =
                Task.Run<Result<LedgerWrite,LedgerFailure>>(fun () ->
                    if not (barrier.SignalAndWait(TimeSpan.FromSeconds 10.0)) then failwith "Race fixture timed out"
                    prepare account project original)
            let first = race f.AccountOne f.ProjectOne claim
            let other = race f.AccountTwo f.ProjectTwo second
            Task.WaitAll([| first :> Task; other :> Task |])
            Assert.That([first.Result; other.Result] |> List.filter Result.isOk |> List.length, Is.EqualTo(1))
            let winner, loser = if first.Result.IsOk then claim, second else second, claim
            let effectId = Guid.Parse c.EffectOperationId
            let origin, _, pinned, args =
                readOriginalReviewerIntent f.ProjectTwo winner.OperationId effectId |> ConfiguredFixture.success
            Assert.That(origin, Is.Not.Empty)
            Assert.That(pinned.Claim, Is.EqualTo(winner))
            Assert.That(pinned.ArgumentsSha256, Is.EqualTo(NativeSchedulerRosterPolicy.sha256 args))
            Assert.That((readOriginalReviewerIntent f.ProjectOne loser.OperationId effectId).IsError, Is.True)
            Assert.That((prepare f.AccountOne f.ProjectOne winner).IsError, Is.True)
            Assert.That((ConfiguredFixture.state f).Records[0].Status, Is.EqualTo("Reserved"))))

[<Test>]
let ``reclaimed review cannot reuse original reserved claim even for same agent`` () =
    ConfiguredFixture.withGit (fun f ->
        let taskId, reviewId, claim = reservedReviewer f
        let agent = claim.ReviewCandidate.OwnerAgentId
        ReviewWorkflow.releaseBranchReview f.ProjectOne (Guid.NewGuid()) taskId reviewId agent 2
        |> ConfiguredFixture.success |> ignore
        let replacement = Guid.NewGuid()
        ReviewWorkflow.claimBranchReview f.ProjectOne replacement taskId reviewId agent 3
        |> ConfiguredFixture.success |> ignore
        let snapshot = LedgerStore.fetch f.ProjectTwo |> ConfiguredFixture.success
        let old = { claim with ExpectedProjectHead = snapshot.Commit }
        let newer =
            { old with ReviewCandidate =
                        { old.ReviewCandidate with ReviewVersion = 4L; ClaimOperationId = string replacement } }
        PairedBrowser.withPaired (fun bridge ->
            let cover = ConfiguredFixture.coverage f f.AccountTwo [| f.ProjectTwo |]
            for candidate in [old; newer] do
                Assert.That((prepareReviewer f.AccountTwo [| f.ProjectTwo |] cover bridge "Disposable" 27000 candidate).IsError, Is.True)
                assertNoIntent f.ProjectTwo (asWorkerForNegativeTest candidate)
            Assert.That((ConfiguredFixture.state f).Records[0].Status, Is.EqualTo("Reserved"))))

[<Test>]
let ``reviewer reservation cannot be repurposed for another owner source or claim`` () =
    ConfiguredFixture.withGit (fun f ->
        let _, _, claim = reservedReviewer f
        let c = claim.ReviewCandidate
        let mutated =
            [ { c with OwnerAgentId = "Other reviewer" }
              { c with ReviewedHeadOid = String.replicate 40 "a" }
              { c with ReviewRef = c.ReviewRef + "-foreign" }
              { c with ReviewVersion = c.ReviewVersion + 1L }
              { c with ClaimOperationId = string (Guid.NewGuid()) } ]
        PairedBrowser.withPaired (fun bridge ->
            let cover = ConfiguredFixture.coverage f f.AccountTwo [| f.ProjectTwo |]
            for candidate in mutated do
                let altered = { claim with ReviewCandidate = candidate }
                Assert.That((prepareReviewer f.AccountTwo [| f.ProjectTwo |] cover bridge "Disposable" 27000 altered).IsError, Is.True)
                assertNoIntent f.ProjectTwo (asWorkerForNegativeTest altered)
            Assert.That((ConfiguredFixture.state f).Records[0].Candidate, Is.EqualTo(c))))

[<Test>]
let ``removing Reviewer role invalidates the same canonical claim`` () =
    ConfiguredFixture.withGit (fun f ->
        let _, _, claim = reservedReviewer f
        Client.RoleInstructions.RoleInstructions.detachRole f.ProjectOne "Disposable fixture"
            (Guid.NewGuid()) claim.ReviewCandidate.OwnerAgentId "Reviewer" 1
        |> ConfiguredFixture.success |> ignore
        let snapshot = LedgerStore.fetch f.ProjectTwo |> ConfiguredFixture.success
        let updated = { claim with ExpectedProjectHead = snapshot.Commit }
        PairedBrowser.withPaired (fun bridge ->
            let cover = ConfiguredFixture.coverage f f.AccountTwo [| f.ProjectTwo |]
            Assert.That((prepareReviewer f.AccountTwo [| f.ProjectTwo |] cover bridge "Disposable" 27000 updated).IsError, Is.True)
            assertNoIntent f.ProjectTwo (asWorkerForNegativeTest updated)
            Assert.That((ConfiguredFixture.state f).Records[0].Status, Is.EqualTo("Reserved"))))

[<TestCase(0,1)>]
[<TestCase(1,0)>]
let ``current reviewer quota revocation holds review while Worker remains independently eligible`` accountReviewerLimit projectReviewerLimit =
    ConfiguredFixture.withGit (fun f ->
        let _, _, review = reservedReviewer f
        ConfiguredFixture.configureLimits f 2L 1L 1 1 accountReviewerLimit projectReviewerLimit
        let agent = "Worker fixture"
        ReviewWorkflow.registerAgent f.ProjectOne (Guid.NewGuid()) agent |> ConfiguredFixture.success |> ignore
        let worker = ConfiguredFixture.startedWorker f agent
        let original = Guid.NewGuid()
        let projects = [| f.ProjectOne |]
        NativeSchedulerAccountReservation.reserve f.AccountOne projects
            (ConfiguredFixture.coverage f f.AccountOne projects) original "Configured fixture" worker
        |> ConfiguredFixture.success |> ignore
        let cover = ConfiguredFixture.coverage f f.AccountOne projects
        let head = (LedgerStore.fetch f.ProjectOne |> ConfiguredFixture.success).Commit
        let review = { review with AccountHead = cover.AccountHead; ExpectedProjectHead = head }
        let claim : StartedProjectClaim =
            { OperationId = Guid.NewGuid(); ExpectedProjectHead = head; AccountHead = cover.AccountHead
              OriginalReservationOperationId = original; Candidate = worker; DeviceId = review.DeviceId
              WindowRegistration = review.WindowRegistration }
        PairedBrowser.withPaired (fun bridge ->
            Assert.That((prepareReviewer f.AccountOne projects cover bridge "Disposable" 27000 review).IsError, Is.True)
            assertNoIntent f.ProjectOne (asWorkerForNegativeTest review)
            let write = prepareWorker f.AccountOne projects cover bridge "Disposable" 27000 claim
                        |> ConfiguredFixture.success
            Assert.That(write.AlreadyApplied, Is.False)
            let records = (ConfiguredFixture.state f).Records
            Assert.That(records.Length, Is.EqualTo(2))
            Assert.That(records |> Array.forall (fun r -> r.Status = "Reserved"), Is.True)))

[<TestCase("Worker")>]
[<TestCase("Reviewer")>]
let ``an existing original-effect path can never be claimed as an empty slot`` kind =
    ConfiguredFixture.withGit (fun f ->
        let projects = [| f.ProjectOne |]
        // This is a negative-only occupied-path fixture, not a verified
        // no-send issuer or a valid release receipt.
        let candidate, attempt =
            if kind = "Worker" then
                let c, _, claim = ConfiguredFixture.reserved f (Some template)
                c, Choice1Of2 claim
            else
                let _, _, claim = reservedReviewer f
                claim.ReviewCandidate, Choice2Of2 claim
        let fence = NativeSchedulerAccountReservation.noSendFencePath candidate
        let raw = """{"status":"occupied-negative-fixture"}"""
        LedgerStore.transact f.ProjectOne
            { OperationId = Guid.NewGuid(); Actor = "Disposable occupied-path fixture"
              Kind = "TestOccupiedOriginalEffectPath"; RequestJson = "{}" }
            (fun _ -> Ok [ { Path = fence; ExpectedBlobOid = None; Content = raw } ])
        |> ConfiguredFixture.success |> ignore
        let snapshot = LedgerStore.fetch f.ProjectTwo |> ConfiguredFixture.success
        let bindings = [| f.ProjectTwo |]
        let cover = ConfiguredFixture.coverage f f.AccountTwo bindings
        PairedBrowser.withPaired (fun bridge ->
            match attempt with
            | Choice1Of2 claim ->
                let changed = { claim with ExpectedProjectHead = snapshot.Commit }
                Assert.That((prepareWorker f.AccountTwo bindings cover bridge "Disposable" 27000 changed).IsError, Is.True)
                assertNoIntent f.ProjectTwo changed
            | Choice2Of2 claim ->
                let changed = { claim with ExpectedProjectHead = snapshot.Commit }
                Assert.That((prepareReviewer f.AccountTwo bindings cover bridge "Disposable" 27000 changed).IsError, Is.True)
                assertNoIntent f.ProjectTwo (asWorkerForNegativeTest changed)
            let current = LedgerStore.fetch f.ProjectTwo |> ConfiguredFixture.success
            Assert.That(current.Commit, Is.EqualTo(snapshot.Commit))
            let actual, _ = LedgerStore.readAt f.ProjectTwo current fence |> ConfiguredFixture.success
            Assert.That(actual, Is.EqualTo(Some raw))
            Assert.That((ConfiguredFixture.state f).Records[0].Status, Is.EqualTo("Reserved"))))
