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

type private SignedFixture =
    { Root: string
      AccountOne: AccountBinding
      AccountTwo: AccountBinding
      ProjectOne: LedgerBinding
      ProjectTwo: LedgerBinding
      PublicKey: byte array
      Signer: ECDsa
      Subject: string }

module private SignedFixture =
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
        File.WriteAllText(Path.Combine(root, ".wq10-disposable-fixture"), "WQ10_DISPOSABLE_FIXTURE_V1")
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
            use signer = ECDsa.Create(ECCurve.NamedCurves.nistP256)
            let f =
                { Root = root; AccountOne = account acc1Path; AccountTwo = account acc2Path
                  ProjectOne = project projectPath; ProjectTwo = project otherProject
                  Signer = signer; PublicKey = signer.ExportSubjectPublicKeyInfo()
                  Subject = "auth0|native-started-disposable" }
            action f
        finally
            try Directory.Delete(root, true) with _ -> ()

    let projectBindings f account =
        if account = 1 then [| f.ProjectOne |] else [| f.ProjectTwo |]

    let signedVersion f version previous accountWorkerLimit projectWorkerLimit =
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
              AuthenticatedSubject = f.Subject
              Version = version; MembershipComplete = true
              AccountLimits = { Worker = accountWorkerLimit; Reviewer = 1 }
              Projects =
                  [| { ProjectId = project.ProjectId.ToString("D")
                       Alias = "Disposable project"
                       LedgerId = project.LedgerId.ToString("D")
                       Epoch = project.Epoch
                       SourceRef = NativeSchedulerRosterPolicy.ProjectSourceRef
                       RemoteAuthoritySha256 = AccountCapacityLedger.projectAuthoritySha256 authority
                       Limits = { Worker = projectWorkerLimit; Reviewer = 1 } } |] }
        let approval =
            { SchemaVersion = 1
              OperationId = Guid.NewGuid().ToString("D")
              Actor = "Disposable signed roster issuer"
              PreviousVersion = previous
              ExpectedAccountHead = initial
              Policy = policy
              SignerPublicKeySha256 = NativeSchedulerRosterPolicy.keySha256 f.PublicKey
              SignatureBase64 = "" }
        let signed =
            { approval with
                SignatureBase64 =
                    Convert.ToBase64String(
                        f.Signer.SignData(NativeSchedulerRosterPolicy.approvalMessage approval,
                                          HashAlgorithmName.SHA256)) }
        AccountCapacityLedger.setVerifiedDisposableNativeRosterPolicy
            f.Root account f.PublicKey f.Subject [| project |] signed
        |> success |> ignore

    let signed f = signedVersion f 1L 0L 1 1

    let coverage f account projects =
        let projection =
            AccountCapacityLedger.readVerifiedDisposableNativeRosterPolicy
                f.Root account f.PublicKey f.Subject projects
        Assert.That(projection.Status, Is.EqualTo("PolicyVerified"),
                    String.concat ";" projection.HoldReasons)
        { AccountHead = projection.VerifiedAccountHead.Value
          ProjectHeads = projection.VerifiedProjectHeads
          ObservedAtUtc = DateTimeOffset.UtcNow
          Complete = true }

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
        signed f
        ReviewWorkflow.registerAgent f.ProjectOne (Guid.NewGuid()) agent |> success |> ignore
        template |> Option.iter (fun body ->
            Client.RoleInstructions.RoleInstructions.setLaunchTemplate f.ProjectOne "Disposable fixture" (Guid.NewGuid()) 0 body
            |> success |> ignore)
        let c = startedWorker f agent
        let original = Guid.NewGuid()
        NativeSchedulerAccountReservation.reserveVerifiedDisposable
            f.Root f.AccountOne f.PublicKey f.Subject [| f.ProjectOne |]
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
        NativeSchedulerAccountReservation.readVerifiedDisposable
            f.Root f.AccountOne f.PublicKey f.Subject [| f.ProjectOne |] cover
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
    prepareVerifiedDisposable f.Root account f.PublicKey f.Subject projects
        (SignedFixture.coverage f account projects) bridge "Disposable" timeout claim

let private assertNoIntent project claim =
    let snapshot = LedgerStore.fetch project |> SignedFixture.success
    let operation = LedgerStore.getOperationAt project snapshot claim.OperationId |> SignedFixture.success
    Assert.That(operation.IsNone, Is.True)
    let id = Guid.Parse claim.Candidate.EffectOperationId
    let path = $"records/native-scheduler/launch-intents/{id:N}.json"
    let value, _ = LedgerStore.readAt project snapshot path |> SignedFixture.success
    Assert.That(value.IsNone, Is.True)
    let arguments, _ = LedgerStore.readAt project snapshot $"records/native-scheduler/launch-arguments/{id:N}.json" |> SignedFixture.success
    Assert.That(arguments.IsNone, Is.True)

[<Test>]
let ``paired browser and verified Started CAS pins exact immutable prompt and effect`` () =
    SignedFixture.withGit (fun f ->
        let c, original, claim = SignedFixture.reserved f (Some template)
        PairedBrowser.withPaired (fun bridge ->
            let before = LedgerStore.fetch f.ProjectOne |> SignedFixture.success
            let rendered =
                Client.RoleInstructions.RoleInstructions.renderLaunchPromptAt f.ProjectOne before "Disposable" c.OwnerAgentId
                |> SignedFixture.success
            let write = prepare f f.AccountOne f.ProjectOne bridge 27000 claim |> SignedFixture.success
            let origin, blob, pinned, arguments =
                readOriginalIntent f.ProjectOne claim.OperationId (Guid.Parse c.EffectOperationId)
                |> SignedFixture.success
            Assert.That(write.AlreadyApplied, Is.False)
            Assert.That(origin, Is.EqualTo(write.CurrentHead))
            Assert.That(blob.Length, Is.GreaterThan(0))
            Assert.That(pinned.Claim, Is.EqualTo(claim))
            Assert.That(pinned.AccountGeneration, Is.EqualTo(1L))
            Assert.That(pinned.TemplateVersion, Is.EqualTo(rendered.TemplateVersion))
            Assert.That(pinned.TemplateBlobOid, Is.EqualTo(rendered.TemplateBlobOid))
            Assert.That(pinned.PromptSha256, Is.EqualTo(rendered.PromptSha256))
            Assert.That(pinned.ObservedWindowGeneration, Is.EqualTo(7))
            use args = JsonDocument.Parse arguments
            Assert.That(args.RootElement.GetProperty("prompt").GetString(), Is.EqualTo(rendered.Prompt))
            Assert.That(args.RootElement.GetProperty("timeoutMs").GetInt32(), Is.EqualTo(27000))
            Assert.That(Client.BrowserArguments.fits arguments, Is.True)
            let originalRecord = (SignedFixture.state f).Records[0]
            Assert.That(originalRecord.OriginalOperationId, Is.EqualTo(string original))
            Assert.That(originalRecord.Status, Is.EqualTo("Reserved"), "Preparation cannot send or release")
            Assert.That(authorizeProviderDispatch().IsError, Is.True)
            Assert.That(releaseFromProviderTerminal().IsError, Is.True)
            // The original UUID is not reusable, even with a new timeout.
            Assert.That((prepare f f.AccountOne f.ProjectOne bridge 26000 claim).IsError, Is.True)
            let newHead = LedgerStore.fetch f.ProjectTwo |> SignedFixture.success
            let second = { claim with OperationId = Guid.NewGuid(); ExpectedProjectHead = newHead.Commit }
            Assert.That((prepare f f.AccountTwo f.ProjectTwo bridge 27000 second).IsError, Is.True)
            // Template change after commitment cannot change the original bytes.
            Client.RoleInstructions.RoleInstructions.setLaunchTemplate f.ProjectOne "Fixture" (Guid.NewGuid()) 1
                "{{agentId}} revised {{projectName}}" |> SignedFixture.success |> ignore
            let _, _, _, reread =
                readOriginalIntent f.ProjectTwo claim.OperationId (Guid.Parse c.EffectOperationId)
                |> SignedFixture.success
            Assert.That(reread, Is.EqualTo(arguments))))

[<Test>]
let ``missing actual paired window leaves original occupancy and project untouched`` () =
    SignedFixture.withGit (fun f ->
        let _, _, claim = SignedFixture.reserved f (Some template)
        use bridge = new Client.BrowserBridge.Service(PairedBrowser.port(), Guid.NewGuid().ToString("N"))
        bridge.Start()
        Assert.That((prepare f f.AccountOne f.ProjectOne bridge 27000 claim).IsError, Is.True)
        assertNoIntent f.ProjectOne claim
        Assert.That((SignedFixture.state f).Records[0].Status, Is.EqualTo("Reserved")))

[<Test>]
let ``template revision between reservation and claim refuses stale project head`` () =
    SignedFixture.withGit (fun f ->
        let _, _, claim = SignedFixture.reserved f (Some template)
        Client.RoleInstructions.RoleInstructions.setLaunchTemplate f.ProjectOne "Fixture" (Guid.NewGuid()) 1
            "{{agentId}} revised {{projectName}}" |> SignedFixture.success |> ignore
        PairedBrowser.withPaired (fun bridge ->
            Assert.That((prepare f f.AccountOne f.ProjectOne bridge 27000 claim).IsError, Is.True)
            assertNoIntent f.ProjectOne claim))

[<Test>]
let ``missing canonical template refuses preparation before original CAS`` () =
    SignedFixture.withGit (fun f ->
        let _, _, claim = SignedFixture.reserved f None
        PairedBrowser.withPaired (fun bridge ->
            Assert.That((prepare f f.AccountOne f.ProjectOne bridge 27000 claim).IsError, Is.True)
            assertNoIntent f.ProjectOne claim))

[<Test>]
let ``reviewer identity cannot be reinterpreted as Worker ingress`` () =
    SignedFixture.withGit (fun f ->
        let _, _, claim = SignedFixture.reserved f (Some template)
        let reviewer = { claim with Candidate = { claim.Candidate with Kind = "Reviewer" } }
        PairedBrowser.withPaired (fun bridge ->
            Assert.That((prepare f f.AccountOne f.ProjectOne bridge 27000 reviewer).IsError, Is.True)
            assertNoIntent f.ProjectOne claim))

[<Test>]
let ``full envelope beyond bridge budget is rejected despite valid minimal prompt`` () =
    SignedFixture.withGit (fun f ->
        let agent = String.replicate 40 "界"
        let body = String.replicate 545 "{{agentId}}" + "{{projectName}}" + String.replicate 113 "x"
        let c, _, claim = SignedFixture.reservedWithAgent f agent (Some body)
        let snapshot = LedgerStore.fetch f.ProjectOne |> SignedFixture.success
        let rendered = Client.RoleInstructions.RoleInstructions.renderLaunchPromptAt
                            f.ProjectOne snapshot "Disposable" c.OwnerAgentId |> SignedFixture.success
        Assert.That(Client.BrowserArguments.serializeMinimalLaunchPrompt rendered.Prompt
                    |> Client.BrowserArguments.byteCount, Is.EqualTo(Client.BrowserArguments.MaxBytes))
        PairedBrowser.withPaired (fun bridge ->
            match prepare f f.AccountOne f.ProjectOne bridge 27000 claim with
            | Error(InvalidBinding reason) -> Assert.That(reason, Does.Contain("Full serialized browser launch arguments"))
            | other -> Assert.Fail(sprintf "Expected full-envelope rejection, received %A" other)
            assertNoIntent f.ProjectOne claim
            Assert.That((SignedFixture.state f).Records[0].Status, Is.EqualTo("Reserved"))))

[<Test>]
let ``duplicate window registration from a second authenticated connection cannot claim`` () =
    SignedFixture.withGit (fun f ->
        let _, _, claim = SignedFixture.reserved f (Some template)
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
        Assert.That((SignedFixture.state f).Records[0].Status, Is.EqualTo("Reserved")))

[<Test>]
let ``full wire envelope at exact bridge limit survives durable Git round trip`` () =
    SignedFixture.withGit (fun f ->
        let agent = String.replicate 40 "界"
        // The minimal 65536-byte fixture above loses exactly 18 bytes to
        // accommodate the comma, timeout property and value.
        let body = String.replicate 545 "{{agentId}}" + "{{projectName}}" + String.replicate 95 "x"
        let c, _, claim = SignedFixture.reservedWithAgent f agent (Some body)
        PairedBrowser.withPaired (fun bridge ->
            prepare f f.AccountOne f.ProjectOne bridge 27000 claim |> SignedFixture.success |> ignore
            let _, _, pinned, arguments =
                readOriginalIntent f.ProjectTwo claim.OperationId (Guid.Parse c.EffectOperationId)
                |> SignedFixture.success
            Assert.That(Client.BrowserArguments.byteCount arguments, Is.EqualTo(Client.BrowserArguments.MaxBytes))
            Assert.That(pinned.ArgumentsSha256, Is.EqualTo(NativeSchedulerRosterPolicy.sha256 arguments))
            Assert.That((SignedFixture.state f).Records[0].Status, Is.EqualTo("Reserved"))))

[<Test>]
let ``rewritten original argument blob is denied even if JSON remains valid`` () =
    SignedFixture.withGit (fun f ->
        let c, _, claim = SignedFixture.reserved f (Some template)
        PairedBrowser.withPaired (fun bridge ->
            prepare f f.AccountOne f.ProjectOne bridge 27000 claim |> SignedFixture.success |> ignore
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
            |> SignedFixture.success |> ignore
            Assert.That((readOriginalIntent f.ProjectTwo claim.OperationId id).IsError, Is.True)
            Assert.That((SignedFixture.state f).Records[0].Status, Is.EqualTo("Reserved"))))

[<Test>]
let ``two separately cloned clients can durably claim original launch only once`` () =
    SignedFixture.withGit (fun f ->
        let c, _, claim = SignedFixture.reserved f (Some template)
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
            Assert.That((SignedFixture.state f).Records[0].Status, Is.EqualTo("Reserved"))))
