module Client.Tests.NativeSchedulerRosterPolicyTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Client.AccountCapacityLedger
open Client.LedgerStore
open Client.NativeSchedulerRosterPolicy
open Client.RoleInstructions
open NUnit.Framework

type private NativeFixture =
    { Root: string
      AccountOne: AccountBinding
      AccountTwo: AccountBinding
      ProjectsOne: LedgerBinding array
      ProjectsTwo: LedgerBinding array
      AccountRemote: string
      AccountCheckoutOne: string
      AccountCheckoutTwo: string
      InitialSourceHead: string }

module private NativeFixture =
    let git directory args =
        use p = new Process()
        p.StartInfo.FileName <- "git"
        p.StartInfo.WorkingDirectory <- directory
        p.StartInfo.RedirectStandardOutput <- true
        p.StartInfo.RedirectStandardError <- true
        p.StartInfo.Environment["GIT_TERMINAL_PROMPT"] <- "0"
        p.StartInfo.Environment["GIT_NO_REPLACE_OBJECTS"] <- "1"
        args |> List.iter p.StartInfo.ArgumentList.Add
        Assert.That(p.Start(), Is.True)
        let output = p.StandardOutput.ReadToEnd()
        let errors = p.StandardError.ReadToEnd()
        Assert.That(p.WaitForExit(30000), Is.True, "Git fixture exceeded 30 seconds.")
        Assert.That(p.ExitCode, Is.EqualTo(0), errors)
        output.Trim()

    let success result =
        match result with
        | Ok value -> value
        | Error e -> failwithf "Expected successful account operation, got %A" e

    let private setPrivate (path: string) =
        if not (OperatingSystem.IsWindows()) then
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

    let private createRemote root folder =
        let bare = Path.Combine(root, folder + ".git")
        let checkout = Path.Combine(root, folder + "-checkout")
        git root [ "init"; "--bare"; "--initial-branch=main"; bare ] |> ignore
        git root [ "clone"; bare; checkout ] |> ignore
        git checkout [ "config"; "user.name"; "Native Fixture" ] |> ignore
        git checkout [ "config"; "user.email"; "fixture@example.test" ] |> ignore
        File.WriteAllText(Path.Combine(checkout, "seed.txt"), folder)
        git checkout [ "add"; "seed.txt" ] |> ignore
        git checkout [ "commit"; "-m"; "immutable fixture source" ] |> ignore
        git checkout [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
        bare, checkout

    let withGit action =
        let root =
            Path.Combine(Path.GetTempPath(), "jarvis-native-roster-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        setPrivate root
        File.WriteAllText(Path.Combine(root, ".wq10-disposable-fixture"),
                          "WQ10_DISPOSABLE_FIXTURE_V1")
        try
            let scopeOne = Path.Combine(root, "wq10-private-client-one")
            let scopeTwo = Path.Combine(root, "wq10-private-client-two")
            Directory.CreateDirectory(scopeOne) |> ignore
            Directory.CreateDirectory(scopeTwo) |> ignore
            setPrivate scopeOne
            setPrivate scopeTwo
            let remote, initial = createRemote root "account-remote"
            let one, two = Path.Combine(scopeOne, "account"), Path.Combine(scopeTwo, "account")
            git root [ "clone"; remote; one ] |> ignore
            git root [ "clone"; remote; two ] |> ignore
            let sourceHead = git one [ "rev-parse"; "HEAD" ]
            let accountId, ledgerId = Guid.NewGuid(), Guid.NewGuid()
            let account path =
                { RepositoryPath = path; RemoteName = "origin"
                  ApprovedFetchUrl = remote; ApprovedPushUrl = remote
                  AccountId = accountId; LedgerId = ledgerId; Epoch = 1 }
            AccountCapacityLedger.initialize (account one) 2 |> success |> ignore
            let projects =
                [| for name in [ "first-project"; "second-project" ] do
                       let pRemote, checkout = createRemote root name
                       let pId, pLedger = Guid.NewGuid(), Guid.NewGuid()
                       let project =
                           { RepositoryPath = checkout; RemoteName = "origin"
                             ApprovedFetchUrl = pRemote; ApprovedPushUrl = pRemote
                             ProjectId = pId; LedgerId = pLedger; Epoch = 1 }
                       LedgerStore.initialize project true |> success |> ignore
                       Client.ReviewWorkflow.ReviewWorkflow.registerAgent project (Guid.NewGuid()) "Reviewer 1"
                       |> success |> ignore
                       RoleInstructions.createRole project "AuthenticatedUser" (Guid.NewGuid()) "Reviewer"
                           "Independent review" "Review independently." |> success |> ignore
                       RoleInstructions.attachRole project "AuthenticatedUser" (Guid.NewGuid()) "Reviewer 1" "Reviewer" 0
                       |> success |> ignore
                       yield project |]
            let secondClientProjects =
                projects |> Array.mapi (fun i p ->
                    let checkout = Path.Combine(root, $"project-second-{i}")
                    git root [ "clone"; p.ApprovedFetchUrl; checkout ] |> ignore
                    { p with RepositoryPath = checkout })
            let f =
                { Root = root; AccountOne = account one; AccountTwo = account two
                  ProjectsOne = projects; ProjectsTwo = secondClientProjects
                  AccountRemote = remote; AccountCheckoutOne = one
                  AccountCheckoutTwo = two; InitialSourceHead = sourceHead }
            action f
            Assert.That(git one [ "rev-parse"; "HEAD" ], Is.EqualTo(sourceHead))
            Assert.That(git two [ "rev-parse"; "HEAD" ], Is.EqualTo(sourceHead))
            Assert.That(git one [ "status"; "--porcelain" ], Is.Empty)
            Assert.That(git two [ "status"; "--porcelain" ], Is.Empty)
        finally
            Directory.Delete(root, true)

    let update (f: NativeFixture) (version: int64) (previous: int64)
                 (head: string) (operationId: Guid) (accountLimits: NativePoolLimits)
                 (projectLimits: NativePoolLimits array) =
        let projects =
            f.ProjectsOne
            |> Array.mapi (fun i p ->
                let authority =
                    { ProjectId = p.ProjectId; LedgerId = p.LedgerId; Epoch = p.Epoch
                      ApprovedFetchUrl = p.ApprovedFetchUrl
                      ApprovedPushUrl = p.ApprovedPushUrl }
                { ProjectId = p.ProjectId.ToString("D")
                  Alias = if i = 0 then "First Project" else "Second Project"
                  LedgerId = p.LedgerId.ToString("D"); Epoch = p.Epoch
                  SourceRef = NativeSchedulerRosterPolicy.ProjectSourceRef
                  RemoteAuthoritySha256 =
                      AccountCapacityLedger.projectAuthoritySha256 authority
                  Limits = projectLimits[i] })
        let b = f.AccountOne
        let policy =
            { SchemaVersion = 1
              AccountId = b.AccountId.ToString("D")
              AccountLedgerId = b.LedgerId.ToString("D")
              AccountEpoch = b.Epoch
              AccountAuthoritySha256 =
                  NativeSchedulerRosterPolicy.accountAuthoritySha256
                      (b.AccountId.ToString("D")) (b.LedgerId.ToString("D"))
                      b.Epoch b.ApprovedFetchUrl b.ApprovedPushUrl
              Version = version
              MembershipComplete = true; AccountLimits = accountLimits
              Projects = projects }
        { SchemaVersion = 1; OperationId = operationId.ToString("D")
          Actor = "AuthenticatedUser"; PreviousVersion = previous
          ExpectedAccountHead = head; Policy = policy }

    let writer _f account projects update =
        AccountCapacityLedger.setNativeRosterPolicy account projects update

    let reader _f account projects =
        AccountCapacityLedger.readNativeRosterPolicy account projects

    let head account = (AccountCapacityLedger.fetch account |> success).Head
    let limits w r = { Worker = w; Reviewer = r }

    // Adversarial fixture only: publish a directly constructed descendant to
    // the disposable bare account remote without changing any source checkout.
    // It cannot create a matching canonical account operation receipt.
    let injectAccountFile (f: NativeFixture) (path: string) (content: string) =
        let checkout = f.AccountCheckoutOne
        let before = head f.AccountOne
        let file = Path.Combine(f.Root, "injected-" + Guid.NewGuid().ToString("N") + ".json")
        File.WriteAllText(file, content)
        let blob = git checkout [ "hash-object"; "-w"; file ]
        let index = Path.Combine(f.Root, "inject-" + Guid.NewGuid().ToString("N") + ".index")
        let gitIndex args =
            use p = new Process()
            p.StartInfo.FileName <- "git"
            p.StartInfo.WorkingDirectory <- checkout
            p.StartInfo.RedirectStandardOutput <- true
            p.StartInfo.RedirectStandardError <- true
            p.StartInfo.Environment["GIT_INDEX_FILE"] <- index
            p.StartInfo.Environment["GIT_NO_REPLACE_OBJECTS"] <- "1"
            p.StartInfo.Environment["GIT_TERMINAL_PROMPT"] <- "0"
            args |> List.iter p.StartInfo.ArgumentList.Add
            Assert.That(p.Start(), Is.True)
            let output = p.StandardOutput.ReadToEnd().Trim()
            let error = p.StandardError.ReadToEnd()
            Assert.That(p.WaitForExit(30000), Is.True)
            Assert.That(p.ExitCode, Is.EqualTo(0), error)
            output
        gitIndex [ "read-tree"; before ] |> ignore
        gitIndex [ "update-index"; "--add"; "--cacheinfo"; $"100644,{blob},{path}" ] |> ignore
        let tree = gitIndex [ "write-tree" ]
        let commit = git checkout
                         [ "-c"; "user.name=Fixture"; "-c"; "user.email=fixture@example.test"
                           "commit-tree"; tree; "-p"; before; "-m"; "adversarial unsigned account file update" ]
        git checkout [ "push"; "--porcelain"; f.AccountRemote;
                       $"{commit}:refs/heads/jarvis-account-admission" ] |> ignore
        commit

[<TestFixture>]
type NativeSchedulerRosterPolicyTests() =
    [<Test>]
    member _.CompleteConfiguredPolicyUsesSameAccountHeadButNeverAuthorizesLaunch() =
        NativeFixture.withGit (fun f ->
            let before = AccountCapacityLedger.fetch f.AccountOne |> NativeFixture.success
            let record =
                NativeFixture.update f 1L 0L before.Head (Guid.NewGuid())
                    (NativeFixture.limits 3 2)
                    [| NativeFixture.limits 0 1; NativeFixture.limits 2 0 |]
            Assert.That((NativeFixture.reader f f.AccountOne f.ProjectsOne).Status,
                        Is.EqualTo("NotAuthoritative"))
            let write = NativeFixture.writer f f.AccountOne f.ProjectsOne record
                        |> NativeFixture.success
            Assert.That(write.AlreadyApplied, Is.False)
            let after = AccountCapacityLedger.fetch f.AccountTwo |> NativeFixture.success
            Assert.That(after.Head, Is.Not.EqualTo(before.Head))
            Assert.That(after.State.SchemaVersion, Is.EqualTo(1))
            Assert.That(after.State.MigrationPhase, Is.EqualTo(before.State.MigrationPhase))
            Assert.That(after.State.ConfigVersion, Is.EqualTo(before.State.ConfigVersion))
            Assert.That(after.State.MaxConcurrentAgents, Is.EqualTo(before.State.MaxConcurrentAgents))
            Assert.That(after.State.Tokens, Is.Empty)
            let projections = [ NativeFixture.reader f f.AccountOne f.ProjectsOne
                                NativeFixture.reader f f.AccountTwo f.ProjectsTwo ]
            for projection in projections do
                Assert.That(projection.Status, Is.EqualTo("PolicyVerified"))
                Assert.That(projection.MembershipComplete, Is.True)
                Assert.That(projection.PolicyComplete, Is.True)
                Assert.That(projection.Policy.Value.AccountLimits.Worker, Is.EqualTo(3))
                Assert.That(projection.VerifiedAccountHead, Is.EqualTo(Some after.Head))
                Assert.That(projection.ObservedAtUtc.IsSome, Is.True)
                Assert.That(projection.VerifiedProjectHeads.Length, Is.EqualTo(2))
                for head in projection.VerifiedProjectHeads do
                    Assert.That(head.VerifiedHead.Length, Is.EqualTo(40))
                    Assert.That(head.Epoch, Is.EqualTo(1))
                    Assert.That(projection.Policy.Value.Projects |> Array.exists (fun p -> p.ProjectId = head.ProjectId), Is.True)
                Assert.That(projection.Policy.Value.Projects[0].Limits.Worker, Is.EqualTo(0))
                Assert.That(projection.Policy.Value.Projects[1].Limits.Reviewer, Is.EqualTo(0))
                Assert.That(projection.OccupancyComplete, Is.False)
                Assert.That(projection.AccountCapacityComplete, Is.False)
                Assert.That(projection.LaunchAuthorized, Is.False)
            // A legacy WQ07 token remains an independent semantic pool and
            // cannot be mistaken for a native Worker session or available slot.
            let p = f.ProjectsOne[0]
            AccountCapacityLedger.reserve f.AccountOne (Guid.NewGuid()) "WQ07"
                (Guid.NewGuid())
                { ProjectId = p.ProjectId; LedgerId = p.LedgerId; Epoch = p.Epoch
                  ApprovedFetchUrl = p.ApprovedFetchUrl; ApprovedPushUrl = p.ApprovedPushUrl }
                "Worker" 1L |> NativeFixture.success |> ignore
            let occupied = AccountCapacityLedger.fetch f.AccountTwo |> NativeFixture.success
            Assert.That(AccountCapacityLedger.occupied occupied.State, Is.EqualTo(1))
            let stillNoNativeOccupancy = NativeFixture.reader f f.AccountTwo f.ProjectsTwo
            Assert.That(stillNoNativeOccupancy.Status, Is.EqualTo("PolicyVerified"))
            Assert.That(stillNoNativeOccupancy.AccountCapacityComplete, Is.False)
            Assert.That(stillNoNativeOccupancy.LaunchAuthorized, Is.False)
            let refs =
                NativeFixture.git f.AccountCheckoutOne [ "ls-remote"; f.AccountRemote; "refs/heads/jarvis-account-admission" ]
            Assert.That(refs, Does.Contain("refs/heads/jarvis-account-admission")))

    [<Test>]
    member _.HistoricalSignedFormatRetainsExactReceiptWithoutSignatureAuthority() =
        NativeFixture.withGit (fun f ->
            let limits = NativeFixture.limits 1 1
            let projectLimits = [| limits; limits |]
            let op = Guid.NewGuid()
            let previous = NativeFixture.head f.AccountOne
            let update = NativeFixture.update f 1L 0L previous op limits projectLimits
            let options = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
            let node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(update, options))
            node["policy"]["authenticatedSubject"] <- System.Text.Json.Nodes.JsonValue.Create("historical-subject")
            node["signerPublicKeySha256"] <- System.Text.Json.Nodes.JsonValue.Create(String.replicate 64 "a")
            node["signatureBase64"] <- System.Text.Json.Nodes.JsonValue.Create("historical-format-only")
            let raw = node.ToJsonString()
            // Simulate the old persisted v1 JSON shape through the normal
            // account CAS; no claim is made about cryptographic validity.
            let historical =
                AccountCapacityLedger.transactFiles f.AccountOne op update.Actor
                    "SetNativeSchedulerRosterPolicy" raw (Some previous)
                    (fun snapshot ->
                        Ok(snapshot.State,
                           JsonSerializer.Serialize(
                               {| version = 1L; previousVersion = 0L
                                  policySha256 = NativeSchedulerRosterPolicy.sha256 raw |}, options),
                           [ NativeSchedulerRosterPolicy.PolicyPath, raw ]))
                |> NativeFixture.success
            let oldHead = NativeFixture.head f.AccountTwo
            Assert.That((NativeFixture.reader f f.AccountTwo f.ProjectsTwo).Status,
                        Is.EqualTo("PolicyVerified"))
            let successor = NativeFixture.update f 2L 1L oldHead (Guid.NewGuid()) limits projectLimits
            NativeFixture.writer f f.AccountTwo f.ProjectsTwo successor |> NativeFixture.success |> ignore
            Assert.That((NativeFixture.reader f f.AccountOne f.ProjectsOne).Policy.Value.Version,
                        Is.EqualTo(2L))
            Assert.That(NativeFixture.git f.AccountCheckoutOne
                            [ "show"; oldHead + ":" + NativeSchedulerRosterPolicy.PolicyPath ],
                        Is.EqualTo(raw))
            let _, original = AccountCapacityLedger.readOperation f.AccountOne op |> NativeFixture.success
            Assert.That(original, Is.EqualTo(Some historical.Receipt)))

    [<Test>]
    member _.ExactReplayChangedPayloadAndLostAcknowledgmentRemainReceiptBound() =
        NativeFixture.withGit (fun f ->
            let initial = NativeFixture.head f.AccountOne
            let original =
                NativeFixture.update f 1L 0L initial (Guid.NewGuid())
                    (NativeFixture.limits 1 2)
                    [| NativeFixture.limits 0 1; NativeFixture.limits 1 0 |]
            let hooks =
                { BeforeLeasePush = fun _ _ -> ()
                  GitCommand = function
                    | "push" :: _ -> AccountGitTestFault.ExecuteThenLoseAcknowledgment
                    | _ -> AccountGitTestFault.PassThrough }
            let won =
                AccountCapacityLedger.withTestInterposition hooks (fun () ->
                    NativeFixture.writer f f.AccountOne f.ProjectsOne original)
                |> NativeFixture.success
            Assert.That(won.AlreadyApplied, Is.False)
            let replay = NativeFixture.writer f f.AccountTwo f.ProjectsTwo original
                         |> NativeFixture.success
            Assert.That(replay.AlreadyApplied, Is.True)
            Assert.That(replay.Receipt.RequestSha256, Is.EqualTo(won.Receipt.RequestSha256))
            let changed =
                { original with Policy = { original.Policy with
                                               AccountLimits = NativeFixture.limits 5 2 } }
            match NativeFixture.writer f f.AccountTwo f.ProjectsTwo changed with
            | Error AccountOperationIdReused -> ()
            | other -> failwithf "Changed request must not inherit receipt: %A" other
            let projection = NativeFixture.reader f f.AccountTwo f.ProjectsTwo
            Assert.That(projection.Status, Is.EqualTo("PolicyVerified"))
            Assert.That(projection.Policy.Value.AccountLimits.Worker, Is.EqualTo(1)))

    [<Test>]
    member _.TwoClientCompetingVersionedUpdatesHaveExactlyOneWinner() =
        NativeFixture.withGit (fun f ->
            let first =
                NativeFixture.update f 1L 0L (NativeFixture.head f.AccountOne)
                    (Guid.NewGuid()) (NativeFixture.limits 2 2)
                    [| NativeFixture.limits 1 1; NativeFixture.limits 1 1 |]
            NativeFixture.writer f f.AccountOne f.ProjectsOne first
            |> NativeFixture.success |> ignore
            let head = NativeFixture.head f.AccountOne
            let a = NativeFixture.update f 2L 1L head (Guid.NewGuid())
                        (NativeFixture.limits 4 1)
                        [| NativeFixture.limits 1 1; NativeFixture.limits 2 0 |]
            let b = NativeFixture.update f 2L 1L head (Guid.NewGuid())
                        (NativeFixture.limits 1 4)
                        [| NativeFixture.limits 0 2; NativeFixture.limits 1 2 |]
            use start = new ManualResetEventSlim(false)
            let t1 = Task.Run(fun () -> start.Wait(); NativeFixture.writer f f.AccountOne f.ProjectsOne a)
            let t2 = Task.Run(fun () -> start.Wait(); NativeFixture.writer f f.AccountTwo f.ProjectsTwo b)
            start.Set()
            Task.WaitAll([| t1 :> Task; t2 :> Task |])
            Assert.That([ t1.Result; t2.Result ] |> List.filter Result.isOk |> List.length,
                        Is.EqualTo(1))
            let result = NativeFixture.reader f f.AccountOne f.ProjectsOne
            Assert.That(result.Status, Is.EqualTo("PolicyVerified"))
            Assert.That(result.Policy.Value.Version, Is.EqualTo(2))
            let third =
                NativeFixture.update f 4L 2L (NativeFixture.head f.AccountTwo)
                    (Guid.NewGuid()) (NativeFixture.limits 1 1)
                    [| NativeFixture.limits 1 1; NativeFixture.limits 0 0 |]
            match NativeFixture.writer f f.AccountTwo f.ProjectsTwo third with
            | Error(AccountPolicyNotAuthoritative _) -> ()
            | other -> failwithf "Version skip accepted: %A" other)

    [<Test>]
    member _.MissingUnapprovedDuplicateForeignAndInconsistentMembershipFailClosed() =
        NativeFixture.withGit (fun f ->
            let missing = NativeFixture.reader f f.AccountOne f.ProjectsOne
            Assert.That(missing.Status, Is.EqualTo("NotAuthoritative"))
            Assert.That(missing.LaunchAuthorized, Is.False)
            let original =
                NativeFixture.update f 1L 0L (NativeFixture.head f.AccountOne)
                    (Guid.NewGuid()) (NativeFixture.limits 0 0)
                    [| NativeFixture.limits 0 0; NativeFixture.limits 0 0 |]
            let invalidCases =
                [ { original with Policy = { original.Policy with
                                               MembershipComplete = false } }
                  { original with Policy = { original.Policy with Projects = [||] } }
                  { original with Policy = { original.Policy with
                                               Projects = [| original.Policy.Projects[0];
                                                             { original.Policy.Projects[1] with
                                                                 Alias = original.Policy.Projects[0].Alias } |] } }
                  { original with Policy = { original.Policy with
                                               Projects = [| original.Policy.Projects[0];
                                                             { original.Policy.Projects[1] with
                                                                 ProjectId = original.Policy.Projects[0].ProjectId } |] } }
                  { original with Policy = { original.Policy with AccountEpoch = 9 } }
                  { original with Policy = { original.Policy with
                                               AccountId = string (Guid.NewGuid()) } }
                  { original with Policy = { original.Policy with
                                               Projects = [| { original.Policy.Projects[0] with
                                                                 RemoteAuthoritySha256 = String.replicate 64 "0" };
                                                             original.Policy.Projects[1] |] } }
                  { original with Policy = { original.Policy with
                                               Projects = [| { original.Policy.Projects[0] with
                                                                 Epoch = 2 };
                                                             original.Policy.Projects[1] |] } }
                  { original with Policy = { original.Policy with
                                               AccountLimits = NativeFixture.limits (-1) 0 } } ]
            for invalid in invalidCases do
                match NativeFixture.writer f f.AccountOne f.ProjectsOne invalid with
                | Error _ -> ()
                | Ok _ -> failwith "Unapproved or inconsistent policy was committed."
            Assert.That(NativeFixture.head f.AccountOne, Is.EqualTo(original.ExpectedAccountHead))
            // An incomplete client-configured binding set is
            // not a complete roster even when the policy says complete.
            NativeFixture.writer f f.AccountOne f.ProjectsOne original
            |> NativeFixture.success |> ignore
            let subset = NativeFixture.reader f f.AccountTwo [| f.ProjectsTwo[0] |]
            Assert.That(subset.Status, Is.EqualTo("NotAuthoritative"))
            let foreign = { f.ProjectsTwo[0] with Epoch = 7 }
            let untrusted = NativeFixture.reader f f.AccountTwo [| foreign; f.ProjectsTwo[1] |]
            Assert.That(untrusted.Status, Is.EqualTo("NotAuthoritative"))
            let remoteMismatch = { f.AccountTwo with ApprovedPushUrl = f.ProjectsTwo[1].ApprovedPushUrl }
            Assert.That((AccountCapacityLedger.readNativeRosterPolicy remoteMismatch f.ProjectsTwo).Status,
                        Is.EqualTo("NotAuthoritative")))

    [<Test>]
    member _.PolicyProofCannotBeReusedAfterOutOfBandGitRewrite() =
        NativeFixture.withGit (fun f ->
            let original =
                NativeFixture.update f 1L 0L (NativeFixture.head f.AccountOne)
                    (Guid.NewGuid()) (NativeFixture.limits 1 1)
                    [| NativeFixture.limits 1 0; NativeFixture.limits 0 1 |]
            NativeFixture.writer f f.AccountOne f.ProjectsOne original
            |> NativeFixture.success |> ignore
            // A direct non-ledger Git commit with another unreceipted
            // policy value must not borrow the previous account operation receipt.
            let forged = { original with Policy = { original.Policy with Version = 2L } }
            NativeFixture.injectAccountFile f NativeSchedulerRosterPolicy.PolicyPath
                (JsonSerializer.Serialize(forged)) |> ignore
            let snapshot = NativeFixture.reader f f.AccountTwo f.ProjectsTwo
            Assert.That(snapshot.Status, Is.EqualTo("NotAuthoritative"))
            Assert.That(snapshot.LaunchAuthorized, Is.False))

    [<Test>]
    member _.FrozenWq07StateFencesNativePolicyEvenWithMatchingAccountHead() =
        NativeFixture.withGit (fun f ->
            let original =
                NativeFixture.update f 1L 0L (NativeFixture.head f.AccountOne)
                    (Guid.NewGuid()) (NativeFixture.limits 1 1)
                    [| NativeFixture.limits 1 0; NativeFixture.limits 0 1 |]
            NativeFixture.writer f f.AccountOne f.ProjectsOne original
            |> NativeFixture.success |> ignore
            // Simulate the Frozen WQ07 state in a disposable bare remote;
            // the roster has not changed.
            let oldState = (AccountCapacityLedger.fetch f.AccountOne |> NativeFixture.success).State
            NativeFixture.injectAccountFile f "state.json"
                (JsonSerializer.Serialize({ oldState with MigrationPhase = "Frozen" })) |> ignore
            let snapshot = NativeFixture.reader f f.AccountTwo f.ProjectsTwo
            Assert.That(snapshot.Status, Is.EqualTo("NotAuthoritative"))
            Assert.That(snapshot.AccountCapacityComplete, Is.False)
            let next =
                NativeFixture.update f 2L 1L (NativeFixture.head f.AccountOne)
                    (Guid.NewGuid()) (NativeFixture.limits 2 1)
                    [| NativeFixture.limits 1 0; NativeFixture.limits 1 1 |]
            match NativeFixture.writer f f.AccountOne f.ProjectsOne next with
            | Error(AccountMigrationWritesFenced "Frozen") -> ()
            | other -> failwithf "Frozen WQ07 migration permitted a native policy write: %A" other)


    [<Test>]
    member _.ForgedLocalProjectReplacementCannotAuthenticateForeignBareRemote() =
        NativeFixture.withGit (fun f ->
            let original =
                NativeFixture.update f 1L 0L (NativeFixture.head f.AccountOne)
                    (Guid.NewGuid()) (NativeFixture.limits 2 2)
                    [| NativeFixture.limits 1 1; NativeFixture.limits 1 1 |]
            NativeFixture.writer f f.AccountOne f.ProjectsOne original
            |> NativeFixture.success |> ignore
            Assert.That((NativeFixture.reader f f.AccountTwo f.ProjectsTwo).Status,
                        Is.EqualTo("PolicyVerified"))
            let projectOwner, projectClient = f.ProjectsOne[0], f.ProjectsTwo[0]
            let oldHead =
                (LedgerStore.fetch projectClient |> NativeFixture.success).Commit
            let originalHeader =
                NativeFixture.git projectOwner.RepositoryPath
                    [ "show"; oldHead + ":ledger.json" ]
            let foreignLedgerId = Guid.NewGuid().ToString("D")
            let tamperedHeader =
                originalHeader.Replace(projectOwner.LedgerId.ToString("D"),
                                       foreignLedgerId, StringComparison.Ordinal)
            Assert.That(tamperedHeader, Is.Not.EqualTo(originalHeader))
            // Only the disposable BARE project remote changes. The configured
            // account policy still binds the original approved project ID.
            NativeFixture.git projectOwner.RepositoryPath
                [ "checkout"; "--detach"; oldHead ] |> ignore
            File.WriteAllText(Path.Combine(projectOwner.RepositoryPath, "ledger.json"),
                              tamperedHeader)
            NativeFixture.git projectOwner.RepositoryPath [ "add"; "ledger.json" ] |> ignore
            NativeFixture.git projectOwner.RepositoryPath
                [ "commit"; "-m"; "Disposable foreign project ledger header" ] |> ignore
            let changedHead =
                NativeFixture.git projectOwner.RepositoryPath [ "rev-parse"; "HEAD" ]
            NativeFixture.git projectOwner.RepositoryPath
                [ "push"; "origin";
                  "HEAD:" + NativeSchedulerRosterPolicy.ProjectSourceRef ] |> ignore
            let remoteRef () =
                NativeFixture.git projectOwner.RepositoryPath
                    [ "ls-remote"; projectOwner.ApprovedFetchUrl
                      NativeSchedulerRosterPolicy.ProjectSourceRef ]
            Assert.That(remoteRef(), Does.StartWith(changedHead))
            // Fetch the new authentic object, then make only the SECOND clone
            // interpret it as a locally forged descendant carrying OLD data.
            NativeFixture.git projectClient.RepositoryPath
                [ "fetch"; "--no-tags"; "origin"
                  NativeSchedulerRosterPolicy.ProjectSourceRef ] |> ignore
            let oldTree =
                NativeFixture.git projectClient.RepositoryPath
                    [ "rev-parse"; oldHead + "^{tree}" ]
            let replacement =
                NativeFixture.git projectClient.RepositoryPath
                    [ "-c"; "user.name=Native Fixture"
                      "-c"; "user.email=fixture@example.test"
                      "commit-tree"; oldTree; "-p"; oldHead
                      "-m"; "Forged local project ledger identity" ]
            NativeFixture.git projectClient.RepositoryPath
                [ "replace"; changedHead; replacement ] |> ignore
            let localGitDir = Path.Combine(projectClient.RepositoryPath, ".git")
            let replacedRef =
                NativeFixture.git projectClient.RepositoryPath
                    [ "show-ref"; "--verify"; "--hash"; "refs/replace/" + changedHead ]
            Assert.That(replacedRef, Is.EqualTo(replacement))
            // Poison other clone-local evidence as well: neither grafts nor
            // object alternates may become evidence for the approved remote.
            File.WriteAllText(Path.Combine(localGitDir, "info", "grafts"),
                              changedHead + " " + oldHead + Environment.NewLine)
            File.WriteAllText(Path.Combine(localGitDir, "objects", "info", "alternates"),
                              Path.Combine(projectOwner.RepositoryPath, ".git", "objects")
                              + Environment.NewLine)
            let checkedProjection =
                NativeFixture.reader f f.AccountTwo f.ProjectsTwo
            Assert.That(checkedProjection.Status, Is.EqualTo("NotAuthoritative"))
            Assert.That(checkedProjection.MembershipComplete, Is.False)
            Assert.That(checkedProjection.PolicyComplete, Is.False)
            Assert.That(checkedProjection.OccupancyComplete, Is.False)
            Assert.That(checkedProjection.AccountCapacityComplete, Is.False)
            Assert.That(checkedProjection.LaunchAuthorized, Is.False)
            Assert.That(checkedProjection.VerifiedProjectHeads, Is.Empty)
            let independentlyUnforged =
                NativeFixture.reader f f.AccountOne f.ProjectsOne
            Assert.That(independentlyUnforged.Status, Is.EqualTo("NotAuthoritative"))
            Assert.That(independentlyUnforged.LaunchAuthorized, Is.False)
            Assert.That(remoteRef(), Does.StartWith(changedHead))
            let genuineHeader =
                NativeFixture.git projectOwner.RepositoryPath
                    [ "show"; changedHead + ":ledger.json" ]
            Assert.That(genuineHeader, Does.Contain(foreignLedgerId)))

    [<Test>]
    member _.RosterReceiptCannotBeTransplantedToDifferentGitPredecessor() =
        NativeFixture.withGit (fun f ->
            let initial = NativeFixture.head f.AccountOne
            let original =
                NativeFixture.update f 1L 0L initial (Guid.NewGuid())
                    (NativeFixture.limits 1 1)
                    [| NativeFixture.limits 1 0; NativeFixture.limits 0 1 |]
            let written = NativeFixture.writer f f.AccountOne f.ProjectsOne original
                          |> NativeFixture.success
            let committed = written.CurrentHead
            let localGit = NativeFixture.git f.AccountCheckoutOne
            let accountBranchParent = localGit [ "rev-parse"; $"{committed}^" ]
            Assert.That(accountBranchParent, Is.EqualTo(initial))
            let beforeTree = localGit [ "rev-parse"; $"{initial}^{{tree}}" ]
            let rosterTree = localGit [ "rev-parse"; $"{committed}^{{tree}}" ]
            // The receipt belongs to initial. A raw Git actor grafts the
            // byte-identical configured policy and receipt onto another predecessor,
            // preserving the forged receipt's commit trailers. A NEW account
            // client has no pre-existing local trusted head to detect the remote
            // rewind; checking the exact expected parent is essential.
            let alteredParent =
                localGit [ "-c"; "user.name=Fixture"; "-c"; "user.email=fixture@example.test"
                           "commit-tree"; beforeTree; "-p"; initial
                           "-m"; "Disposable unrelated account descendant" ]
            let fakeMessage =
                $"Account SetNativeSchedulerRosterPolicy\n\nOperation-Id: {original.OperationId}\nRequest-SHA256: {written.Receipt.RequestSha256}"
            let graft =
                localGit [ "-c"; "user.name=Fixture"; "-c"; "user.email=fixture@example.test"
                           "commit-tree"; rosterTree; "-p"; alteredParent
                           "-m"; fakeMessage ]
            localGit [ "push"; "--porcelain";
                       $"--force-with-lease=refs/heads/jarvis-account-admission:{committed}"
                       f.AccountRemote; $"{graft}:refs/heads/jarvis-account-admission" ] |> ignore
            // Read from a newly created, empty trusted-ref clone; do not count
            // an old client's anti-rewind error as evidence of origin protection.
            let scope = Path.Combine(f.Root, "wq10-private-fresh-third-client")
            Directory.CreateDirectory(scope) |> ignore
            if not (OperatingSystem.IsWindows()) then
                File.SetUnixFileMode(scope,
                    UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
            let freshPath = Path.Combine(scope, "account")
            NativeFixture.git f.Root [ "clone"; f.AccountRemote; freshPath ] |> ignore
            let fresh = { f.AccountOne with RepositoryPath = freshPath }
            let freshHead = AccountCapacityLedger.fetch fresh |> NativeFixture.success
            Assert.That(freshHead.Head, Is.EqualTo(graft))
            let observed =
                AccountCapacityLedger.readNativeRosterPolicy fresh f.ProjectsTwo
            Assert.That(observed.Status, Is.EqualTo("NotAuthoritative"))
            Assert.That(observed.HoldReasons[0], Does.Contain("predecessor"))
            Assert.That(observed.LaunchAuthorized, Is.False))

open Client.NativeSchedulerAccountReservation
open Client.ReviewWorkflow

module private NativeReservationFixture =
    let success value = NativeFixture.success value
    let limits w r = NativeFixture.limits w r

    let establish f accountLimits projectLimits =
        let oldHead = NativeFixture.head f.AccountOne
        let approval = NativeFixture.update f 1L 0L oldHead (Guid.NewGuid())
                           accountLimits projectLimits
        NativeFixture.writer f f.AccountOne f.ProjectsOne approval |> success |> ignore

    let coverage f account projects =
        let policy = NativeFixture.reader f account projects
        Assert.That(policy.Status, Is.EqualTo("PolicyVerified"),
                    String.concat ";" policy.HoldReasons)
        { AccountHead = policy.VerifiedAccountHead.Value
          ProjectHeads = policy.VerifiedProjectHeads
          ObservedAtUtc = DateTimeOffset.UtcNow }

    let private json value =
        JsonSerializer.Serialize(value,
            JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase))

    // Positive candidates are issued as actual immutable project-ledger
    // task/attempt or independent claimed-review records, never random IDs.
    let worker f projectIndex =
        let p = f.ProjectsOne[projectIndex]
        let head = (LedgerStore.fetch p |> success).Commit
        let taskId = "native-fixture-" + Guid.NewGuid().ToString("N")
        let attemptId = Guid.NewGuid().ToString("N")
        let sourceRef = "refs/heads/fixture-isolated"
        let spec = "<h1>Native disposable Worker issuer</h1>"
        let specOid = LedgerStore.computeBlobOid p spec |> success
        let task: ReviewTask =
            { TaskId = taskId; Title = "Native disposable Worker issuer"
              Version = 2; Lifecycle = "InProgress"; SpecVersion = 1
              AttemptId = attemptId }
        let attempt: ReviewAttempt =
            { TaskId = taskId; AttemptId = attemptId; Version = 3
              OwnerAgentId = "Worker 4"; Phase = "Started"
              AssignmentGeneration = 1; Priority = 0; IssuedSequence = 1L
              SpecBlobOid = specOid; BaseCommit = head
              TargetRef = "refs/heads/main"
              SourceRepositoryId = String.replicate 64 "a"
              SourceRemote = p.ApprovedFetchUrl; SourcePushRemote = p.ApprovedPushUrl
              SourceRef = sourceRef }
        let attemptRaw = json attempt
        let changes =
            [ { Path = $"tasks/{taskId}/task.json"; ExpectedBlobOid = None
                Content = json task }
              { Path = $"tasks/{taskId}/spec.html"; ExpectedBlobOid = None
                Content = spec }
              { Path = $"tasks/{taskId}/attempts/{attemptId}.json"; ExpectedBlobOid = None
                Content = attemptRaw } ]
        LedgerStore.transact p
            { OperationId = Guid.NewGuid(); Actor = "Native disposable project issuer"
              Kind = "NativeWorkerIssued"; RequestJson = json {| taskId = taskId; attemptId = attemptId |} }
            (fun _ -> Ok changes)
        |> success |> ignore
        { DispatchId = Guid.NewGuid().ToString("D")
          EffectOperationId = Guid.NewGuid().ToString("D")
          Kind = "Worker"
          ProjectId = p.ProjectId.ToString("D")
          ProjectLedgerId = p.LedgerId.ToString("D")
          ProjectEpoch = p.Epoch
          OwnerAgentId = attempt.OwnerAgentId
          TaskId = taskId
          AttemptId = attemptId
          AssignmentGeneration = int64 attempt.AssignmentGeneration
          SourceRepositoryId = attempt.SourceRepositoryId
          SourceBaseOid = head
          SourceRef = sourceRef
          SourceLeaseSha256 = NativeSchedulerRosterPolicy.sha256 attemptRaw
          ReviewId = ""; ReviewVersion = 0L; ReviewedHeadOid = ""
          ReviewRef = ""; ClaimOperationId = "" }

    let reviewer f projectIndex =
        let p = f.ProjectsOne[projectIndex]
        let head = (LedgerStore.fetch p |> success).Commit
        let reviewId = Guid.NewGuid().ToString("N")
        let taskId = "fixture"
        let reviewRef = $"refs/heads/jarvis-review/{taskId}/{reviewId}/r1"
        ReviewWorkflow.submitBranchReview p (Guid.NewGuid()) taskId
            "Worker fixture" (Guid.Parse reviewId) "" head
            "Disposable native Reviewer claimed-ref fixture"
            "Disposable independent reviewer claim" ""
        |> success |> ignore
        let claimId = Guid.NewGuid()
        ReviewWorkflow.claimBranchReview p claimId taskId
            (Guid.Parse reviewId) "Reviewer 1" 1
        |> success |> ignore
        { DispatchId = Guid.NewGuid().ToString("D")
          EffectOperationId = Guid.NewGuid().ToString("D")
          Kind = "Reviewer"
          ProjectId = p.ProjectId.ToString("D")
          ProjectLedgerId = p.LedgerId.ToString("D")
          ProjectEpoch = p.Epoch
          OwnerAgentId = "Reviewer 1"
          TaskId = ""; AttemptId = ""; AssignmentGeneration = 0L
          SourceRepositoryId = ""; SourceBaseOid = ""; SourceRef = ""; SourceLeaseSha256 = ""
          ReviewId = reviewId; ReviewVersion = 2L; ReviewedHeadOid = head
          ReviewRef = reviewRef; ClaimOperationId = claimId.ToString("D") }

    // A provider-terminal Ok alone is never sufficient: an immutable original
    // project operation and its session terminal blob must be on verified Git.
    let terminalProof f (candidate: NativeDispatchCandidate) (entry: NativeReservationRecord)
                      (locator: NativeTerminalLocator) fence session =
        let p = f.ProjectsOne |> Array.find (fun x ->
            x.ProjectId.ToString("D") = candidate.ProjectId)
        let path = $"records/native-scheduler/terminals/{locator.TerminalOperationId:N}.json"
        let evidence =
            json {| schemaVersion = 1; state = "Terminated"
                    dispatchId = string locator.DispatchId
                    originalReservationId = string locator.OriginalReservationId
                    effectOperationId = string locator.EffectOperationId
                    terminalOperationId = string locator.TerminalOperationId
                    canonicalKey = entry.CanonicalKey
                    projectFenceOid = fence; providerSessionId = session |}
        LedgerStore.transact p
            { OperationId = locator.TerminalOperationId
              Actor = "Disposable canonical project provider terminal"
              Kind = "NativeProviderSessionTerminal"; RequestJson = evidence }
            (fun _ -> Ok [ { Path = path; ExpectedBlobOid = None; Content = evidence } ])
        |> success |> ignore
        let snapshot = LedgerStore.fetch p |> success
        let terminalOp = LedgerStore.getOperationAt p snapshot locator.TerminalOperationId
                         |> success |> Option.get
        let origin = LedgerStore.getOperationOriginAt p snapshot terminalOp |> success
        let _, blob = LedgerStore.readAt p snapshot path |> success
        { DispatchId = locator.DispatchId
          OriginalReservationId = locator.OriginalReservationId
          EffectOperationId = locator.EffectOperationId
          TerminalOperationId = locator.TerminalOperationId
          ProjectId = Guid.Parse candidate.ProjectId
          ProjectLedgerId = Guid.Parse candidate.ProjectLedgerId
          ProjectEpoch = candidate.ProjectEpoch
          CanonicalKey = entry.CanonicalKey; ProviderSessionId = session
          ProjectHead = snapshot.Commit; ProjectTerminalOid = origin
          ProviderTerminalOid = blob.Value }

    let reserve f account projects coverage op candidate =
        NativeSchedulerAccountReservation.reserve
            account projects coverage
            op "Verified disposable fixture" candidate

    let snapshot f account projects =
        NativeSchedulerAccountReservation.readVerified
            account projects (coverage f account projects)
        |> success |> snd

[<TestFixture>]
type NativeSchedulerAccountReservationTests() =
    [<Test>]
    member _.TwoIndependentClonesCannotOverbookOneWorkerAcrossProjects() =
        NativeFixture.withGit (fun f ->
            NativeReservationFixture.establish f
                (NativeReservationFixture.limits 1 1)
                [| NativeReservationFixture.limits 1 1
                   NativeReservationFixture.limits 1 1 |]
            let first = NativeReservationFixture.worker f 0
            let second = NativeReservationFixture.worker f 1
            let c1 = NativeReservationFixture.coverage f f.AccountOne f.ProjectsOne
            let c2 = NativeReservationFixture.coverage f f.AccountTwo f.ProjectsTwo
            use gate = new ManualResetEventSlim(false)
            let op1, op2 = Guid.NewGuid(), Guid.NewGuid()
            let t1 = Task.Run(fun () ->
                gate.Wait()
                NativeReservationFixture.reserve f f.AccountOne f.ProjectsOne c1 op1 first)
            let t2 = Task.Run(fun () ->
                gate.Wait()
                NativeReservationFixture.reserve f f.AccountTwo f.ProjectsTwo c2 op2 second)
            gate.Set()
            Task.WaitAll([| t1 :> Task; t2 :> Task |])
            let winners = [| t1.Result; t2.Result |] |> Array.filter Result.isOk
            Assert.That(winners.Length, Is.EqualTo(1))
            let state = NativeReservationFixture.snapshot f f.AccountOne f.ProjectsOne
            Assert.That(state.Records.Length, Is.EqualTo(1))
            Assert.That(state.Records[0].Status, Is.EqualTo("Reserved")))

    [<Test>]
    member _.ExactUUIDReplayIsImmutableAndDifferentUUIDCannotDuplicateCandidate() =
        NativeFixture.withGit (fun f ->
            NativeReservationFixture.establish f
                (NativeReservationFixture.limits 2 2)
                [| NativeReservationFixture.limits 2 2
                   NativeReservationFixture.limits 2 2 |]
            let c = NativeReservationFixture.worker f 0
            let op = Guid.NewGuid()
            let oldCoverage = NativeReservationFixture.coverage f f.AccountOne f.ProjectsOne
            let first =
                NativeReservationFixture.reserve f f.AccountOne f.ProjectsOne oldCoverage op c
                |> NativeReservationFixture.success
            Assert.That(first.AlreadyApplied, Is.False)
            let again =
                NativeReservationFixture.reserve f f.AccountTwo f.ProjectsTwo oldCoverage op c
                |> NativeReservationFixture.success
            Assert.That(again.AlreadyApplied, Is.True)
            Assert.That(again.Receipt.ResultSha256, Is.EqualTo(first.Receipt.ResultSha256))
            match NativeReservationFixture.reserve f f.AccountOne f.ProjectsOne
                    oldCoverage op { c with OwnerAgentId = "different" } with
            | Error AccountOperationIdReused -> ()
            | result -> failwithf "Changed payload replay was not rejected: %A" result
            let fresh = NativeReservationFixture.coverage f f.AccountTwo f.ProjectsTwo
            match NativeReservationFixture.reserve f f.AccountTwo f.ProjectsTwo
                    fresh (Guid.NewGuid()) { c with DispatchId = Guid.NewGuid().ToString("D")
                                                    EffectOperationId = Guid.NewGuid().ToString("D") } with
            | Error(AccountPolicyNotAuthoritative _) -> ()
            | result -> failwithf "Canonical duplicate reservation was not rejected: %A" result
            Assert.That((NativeReservationFixture.snapshot f f.AccountOne f.ProjectsOne).Records.Length,
                        Is.EqualTo(1)))

    [<Test>]
    member _.WorkerReviewerPoolsAndProjectZeroAreDistinct() =
        NativeFixture.withGit (fun f ->
            NativeReservationFixture.establish f
                (NativeReservationFixture.limits 1 2)
                [| NativeReservationFixture.limits 0 1
                   NativeReservationFixture.limits 1 0 |]
            let attempt c =
                NativeReservationFixture.reserve f f.AccountOne f.ProjectsOne
                    (NativeReservationFixture.coverage f f.AccountOne f.ProjectsOne)
                    (Guid.NewGuid()) c
            match attempt (NativeReservationFixture.worker f 0) with
            | Error(AccountCapacityFull(0,0)) -> ()
            | other -> failwithf "Project worker quota zero was bypassed: %A" other
            attempt (NativeReservationFixture.reviewer f 0)
                |> NativeReservationFixture.success |> ignore
            attempt (NativeReservationFixture.worker f 1)
                |> NativeReservationFixture.success |> ignore
            match attempt (NativeReservationFixture.reviewer f 1) with
            | Error(AccountCapacityFull(0,0)) -> ()
            | other -> failwithf "Project reviewer quota zero was bypassed: %A" other
            let entries = (NativeReservationFixture.snapshot f f.AccountOne f.ProjectsOne).Records
            Assert.That(entries.Length, Is.EqualTo(2))
            Assert.That(entries |> Array.filter (fun r -> r.Candidate.Kind = "Worker") |> Array.length,
                        Is.EqualTo(1)))

    [<Test>]
    member _.UnknownOccupiesExactlyOneSlotUntilOriginalVerifiedTerminal() =
        NativeFixture.withGit (fun f ->
            NativeReservationFixture.establish f
                (NativeReservationFixture.limits 1 1)
                [| NativeReservationFixture.limits 1 1
                   NativeReservationFixture.limits 1 1 |]
            let candidate = NativeReservationFixture.worker f 0
            let op = Guid.NewGuid()
            let fence = (LedgerStore.fetch f.ProjectsOne[0] |> NativeReservationFixture.success).Commit
            let reserved =
                NativeReservationFixture.reserve f f.AccountOne f.ProjectsOne
                    (NativeReservationFixture.coverage f f.AccountOne f.ProjectsOne) op candidate
                |> NativeReservationFixture.success
            let dispatchId = Guid.Parse(candidate.DispatchId)
            let effectId = Guid.Parse(candidate.EffectOperationId)
            let advance generation status session =
                let entry = (NativeReservationFixture.snapshot f f.AccountOne f.ProjectsOne).Records[0]
                let locator =
                    { ReservationOperationId = op
                      ReservationRequestSha256 = reserved.Receipt.RequestSha256
                      Candidate = candidate
                      ExpectedGeneration = generation
                      ExpectedProjectFenceOid = entry.ProjectFenceOid
                      ExpectedProviderSessionId = entry.ProviderSessionId }
                NativeSchedulerAccountReservation.transition
                    f.AccountOne f.ProjectsOne
                    (NativeReservationFixture.coverage f f.AccountOne f.ProjectsOne)
                    (Guid.NewGuid()) "Fixture actor" locator status
                    fence session
            advance 1L "Dispatching" "" |> NativeReservationFixture.success |> ignore
            advance 2L "Uncertain" "" |> NativeReservationFixture.success |> ignore
            advance 3L "Unknown" "provider-session-1" |> NativeReservationFixture.success |> ignore
            Assert.That((NativeReservationFixture.snapshot f f.AccountOne f.ProjectsOne).Records.Length,
                        Is.EqualTo(1))
            let competingWorker = NativeReservationFixture.worker f 1
            match NativeReservationFixture.reserve f f.AccountTwo f.ProjectsTwo
                    (NativeReservationFixture.coverage f f.AccountTwo f.ProjectsTwo)
                    (Guid.NewGuid()) competingWorker with
            | Error(AccountCapacityFull(1,1)) -> ()
            | other -> failwithf "Unknown session did not retain account credit: %A" other
            let entry = (NativeReservationFixture.snapshot f f.AccountOne f.ProjectsOne).Records[0]
            let locator =
                { DispatchId = dispatchId; OriginalReservationId = op
                  ReservationRequestSha256 = reserved.Receipt.RequestSha256
                  ExpectedGeneration = entry.Generation
                  EffectOperationId = effectId; TerminalOperationId = Guid.NewGuid() }
            let proof =
                NativeReservationFixture.terminalProof f candidate entry locator
                    fence "provider-session-1"
            let trusted =
                { new INativeTerminalVerifier with
                    member _.VerifyCurrentTerminal _ = Ok proof }
            let release uuid terminal =
                NativeSchedulerAccountReservation.release
                    f.AccountOne f.ProjectsOne
                    (NativeReservationFixture.coverage f f.AccountOne f.ProjectsOne)
                    trusted uuid "Fixture verifier" terminal
            match release (Guid.NewGuid()) { locator with OriginalReservationId = Guid.NewGuid() } with
            | Error(AccountPolicyNotAuthoritative _) -> ()
            | other -> failwithf "Foreign terminal was not rejected: %A" other
            for invalid in
                [ { locator with ReservationRequestSha256 = String.replicate 64 "f" }
                  { locator with ExpectedGeneration = locator.ExpectedGeneration - 1L } ] do
                match release (Guid.NewGuid()) invalid with
                | Error(AccountPolicyNotAuthoritative _) -> ()
                | other -> failwithf "Inexact original receipt or generation released: %A" other
            let expectDenied label (badProof: NativeTerminalProof) =
                let before = NativeFixture.head f.AccountOne
                let verifier =
                    { new INativeTerminalVerifier with
                        member _.VerifyCurrentTerminal _ = Ok badProof }
                match NativeSchedulerAccountReservation.release
                        f.AccountTwo f.ProjectsTwo
                        (NativeReservationFixture.coverage f f.AccountTwo f.ProjectsTwo)
                        verifier (Guid.NewGuid()) "Adversarial terminal" locator with
                | Error(AccountPolicyNotAuthoritative _) -> ()
                | other -> failwithf "%s source-unbound terminal accepted: %A" label other
                Assert.That(NativeFixture.head f.AccountOne, Is.EqualTo(before), label)
                Assert.That((NativeReservationFixture.snapshot f f.AccountOne f.ProjectsOne).Records[0].Status,
                            Is.EqualTo("Unknown"), label)
            expectDenied "Foreign syntactically valid project terminal" { proof with ProjectTerminalOid = f.InitialSourceHead }
            expectDenied "Foreign syntactically valid provider terminal" { proof with ProviderTerminalOid = f.InitialSourceHead }
            expectDenied "Stale foreign verified project head" { proof with ProjectHead = f.InitialSourceHead }
            let emptyLocator = { locator with TerminalOperationId = Guid.Empty }
            let emptyProof = { proof with TerminalOperationId = Guid.Empty }
            let emptyVerifier =
                { new INativeTerminalVerifier with
                    member _.VerifyCurrentTerminal _ = Ok emptyProof }
            let beforeEmpty = NativeFixture.head f.AccountOne
            match NativeSchedulerAccountReservation.release
                    f.AccountOne f.ProjectsOne
                    (NativeReservationFixture.coverage f f.AccountOne f.ProjectsOne)
                    emptyVerifier (Guid.NewGuid()) "Adversarial empty terminal" emptyLocator with
            | Error(AccountPolicyNotAuthoritative _) -> ()
            | other -> failwithf "Guid.Empty terminal reached account CAS: %A" other
            Assert.That(NativeFixture.head f.AccountOne, Is.EqualTo(beforeEmpty))
            Assert.That((NativeReservationFixture.snapshot f f.AccountOne f.ProjectsOne).Records[0].Status,
                        Is.EqualTo("Unknown"))
            let releaseId = Guid.NewGuid()
            let released = release releaseId locator |> NativeReservationFixture.success
            Assert.That(released.AlreadyApplied, Is.False)
            Assert.That((NativeReservationFixture.snapshot f f.AccountOne f.ProjectsOne).Records[0].Status,
                        Is.EqualTo("Released"))
            let replay = release releaseId locator |> NativeReservationFixture.success
            Assert.That(replay.AlreadyApplied, Is.True)
            Assert.That(replay.Receipt, Is.EqualTo(released.Receipt))
            let reusable = NativeReservationFixture.worker f 1
            NativeReservationFixture.reserve f f.AccountOne f.ProjectsOne
                (NativeReservationFixture.coverage f f.AccountOne f.ProjectsOne)
                (Guid.NewGuid()) reusable |> NativeReservationFixture.success |> ignore)

    [<Test>]
    member _.NoSendRequiresCanonicalExclusionAndNeverTreatsUnknownAsAbsence() =
        NativeFixture.withGit (fun f ->
            let limits = NativeReservationFixture.limits 1 1
            NativeReservationFixture.establish f limits [| limits; limits |]
            let candidate = NativeReservationFixture.worker f 0
            let reservationId = Guid.NewGuid()
            let receipt =
                NativeReservationFixture.reserve f f.AccountOne f.ProjectsOne
                    (NativeReservationFixture.coverage f f.AccountOne f.ProjectsOne)
                    reservationId candidate
                |> NativeReservationFixture.success
            let original =
                { ReservationOperationId = reservationId
                  ReservationRequestSha256 = receipt.Receipt.RequestSha256
                  Candidate = candidate; ExpectedGeneration = 1L
                  ExpectedProjectFenceOid = ""; ExpectedProviderSessionId = "" }
            let project = f.ProjectsOne[0]
            let context =
                NativeSchedulerAccountReservation.readOriginal f.AccountOne f.ProjectsOne
                    (NativeReservationFixture.coverage f f.AccountOne f.ProjectsOne) original
                |> NativeReservationFixture.success
            let operationId = Guid.NewGuid()
            let evidence =
                { SchemaVersion = 1; State = "VerifiedNoSend"
                  AccountId = string f.AccountOne.AccountId
                  AccountLedgerId = string f.AccountOne.LedgerId; AccountEpoch = f.AccountOne.Epoch
                  ReservationOrigin = context.ReservationOrigin
                  CanonicalKey = context.CurrentReservation.CanonicalKey
                  Original = original; NoSendOperationId = operationId }
                |> NativeSchedulerAccountReservation.serializeNoSendEvidence
            let blob = LedgerStore.computeBlobOid project evidence |> NativeReservationFixture.success
            let head () = (LedgerStore.fetch project |> NativeReservationFixture.success).Commit
            let initialHead = head ()
            let locator =
                { Original = original; NoSendOperationId = operationId
                  ProjectHead = initialHead; NoSendOrigin = initialHead; EvidenceBlobOid = blob }
            let deny (l: NativeNoSendLocator) =
                let before = NativeFixture.head f.AccountTwo
                match NativeSchedulerAccountReservation.releaseNoSend f.AccountTwo f.ProjectsTwo
                        (NativeReservationFixture.coverage f f.AccountTwo f.ProjectsTwo)
                        (Guid.NewGuid()) "Disposable denial fixture" l with
                | Error(AccountPolicyNotAuthoritative _) -> ()
                | other -> failwithf "Unverified no-send release was not denied: %A" other
                Assert.That(NativeFixture.head f.AccountTwo, Is.EqualTo(before))
            deny locator
            deny { locator with Original = { original with ExpectedGeneration = 2L } }
            deny { locator with Original = { original with ReservationRequestSha256 = String.replicate 64 "c" } }
            deny { locator with Original = { original with Candidate = { candidate with EffectOperationId = string (Guid.NewGuid()) } } }
            // Negative fixture deliberately writes only a receipt-shaped blob,
            // without the canonical fence mutation. Not a genuine issuer.
            let path = NativeSchedulerAccountReservation.noSendEvidencePath operationId
            let intent =
                { OperationId = operationId; Actor = "Disposable incomplete no-send fixture"
                  Kind = "NativeVerifiedNoSend"; RequestJson = evidence }
            LedgerStore.transact project intent
                (fun _ -> Ok [ { Path = path; ExpectedBlobOid = None; Content = evidence } ])
            |> NativeReservationFixture.success |> ignore
            let snapshot = LedgerStore.fetch project |> NativeReservationFixture.success
            let operation =
                LedgerStore.getOperationAt project snapshot operationId
                |> NativeReservationFixture.success |> Option.get
            // Normal ledger writes canonicalize the nested request; the raw
            // evidence remains byte-exact in the blob, not in the envelope.
            use envelope = JsonDocument.Parse(operation.CanonicalRequestJson)
            Assert.That(envelope.RootElement.GetProperty("request").GetString(), Is.Not.EqualTo(evidence))
            LedgerStore.verifyIntegrationOperationIntent project operation intent
            |> NativeReservationFixture.success
            let wrongIntent = { intent with RequestJson = "{}" }
            match LedgerStore.verifyIntegrationOperationIntent project operation wrongIntent with
            | Error(InvalidBinding _) -> ()
            | other -> failwithf "Wrong no-send intent was not denied: %A" other
            let current = snapshot.Commit
            deny { locator with ProjectHead = current; NoSendOrigin = current }
            let advance (l: NativeOriginalReservationLocator) status =
                NativeSchedulerAccountReservation.transition f.AccountOne f.ProjectsOne
                    (NativeReservationFixture.coverage f f.AccountOne f.ProjectsOne)
                    (Guid.NewGuid()) "Disposable status fixture" l status current ""
                |> NativeReservationFixture.success |> ignore
            advance original "Dispatching"
            let dispatching = { original with ExpectedGeneration = 2L; ExpectedProjectFenceOid = current }
            advance dispatching "Uncertain"
            deny { locator with ProjectHead = current; NoSendOrigin = current
                                Original = { dispatching with ExpectedGeneration = 3L } }
            let occupied = (NativeReservationFixture.snapshot f f.AccountTwo f.ProjectsTwo).Records[0]
            Assert.That(occupied.Status, Is.EqualTo("Uncertain"))
            let competing = NativeReservationFixture.worker f 1
            match NativeReservationFixture.reserve f f.AccountTwo f.ProjectsTwo
                    (NativeReservationFixture.coverage f f.AccountTwo f.ProjectsTwo)
                    (Guid.NewGuid()) competing with
            | Error(AccountCapacityFull(1,1)) -> ()
            | other -> failwithf "Uncertain no-send evidence freed capacity: %A" other)

    [<Test>]
    member _.OriginalProjectLineageSurvivesRosterSuccessionButRemovalHoldsSlot() =
        NativeFixture.withGit (fun f ->
            let limits = NativeReservationFixture.limits 1 1
            let projectLimits = [| limits; limits |]
            NativeReservationFixture.establish f limits projectLimits
            let candidate = NativeReservationFixture.worker f 0
            let op = Guid.NewGuid()
            let receipt =
                NativeReservationFixture.reserve f f.AccountOne f.ProjectsOne
                    (NativeReservationFixture.coverage f f.AccountOne f.ProjectsOne) op candidate
                |> NativeReservationFixture.success
            let locator =
                { ReservationOperationId = op
                  ReservationRequestSha256 = receipt.Receipt.RequestSha256
                  Candidate = candidate; ExpectedGeneration = 1L
                  ExpectedProjectFenceOid = ""; ExpectedProviderSessionId = "" }
            let read projects locator =
                NativeSchedulerAccountReservation.readOriginal f.AccountTwo projects
                    (NativeReservationFixture.coverage f f.AccountTwo projects) locator
            let initial = read f.ProjectsTwo locator |> NativeReservationFixture.success
            Assert.That(initial.ReservationReceipt, Is.EqualTo(receipt.Receipt))
            let successor =
                NativeFixture.update f 2L 1L (NativeFixture.head f.AccountOne)
                    (Guid.NewGuid()) limits projectLimits
            NativeFixture.writer f f.AccountOne f.ProjectsOne successor
            |> NativeFixture.success |> ignore
            let retained = read f.ProjectsTwo locator |> NativeReservationFixture.success
            Assert.That(retained.ReservationReceipt, Is.EqualTo(initial.ReservationReceipt))
            Assert.That(retained.ReservationOrigin, Is.EqualTo(initial.ReservationOrigin))
            Assert.That(retained.CurrentReservation, Is.EqualTo(initial.CurrentReservation))
            for invalid in
                [ { locator with ExpectedGeneration = 2L }
                  { locator with ReservationRequestSha256 = String.replicate 64 "c" }
                  { locator with Candidate = { candidate with ProjectEpoch = candidate.ProjectEpoch + 1 } } ] do
                match read f.ProjectsTwo invalid with
                | Error(AccountPolicyNotAuthoritative _) -> ()
                | other -> failwithf "Foreign original locator returned context: %A" other
            let removed =
                NativeFixture.update f 3L 2L (NativeFixture.head f.AccountOne)
                    (Guid.NewGuid()) limits projectLimits
            let reduced = { removed with Policy = { removed.Policy with Projects = [| removed.Policy.Projects[1] |] } }
            let survivingOne = [| f.ProjectsOne[1] |]
            let survivingTwo = [| f.ProjectsTwo[1] |]
            NativeFixture.writer f f.AccountOne survivingOne reduced |> NativeFixture.success |> ignore
            let beforeHold = NativeFixture.head f.AccountTwo
            match read survivingTwo locator with
            | Error(AccountPolicyNotAuthoritative _) -> ()
            | other -> failwithf "Removed original project returned a context: %A" other
            Assert.That(NativeFixture.head f.AccountTwo, Is.EqualTo(beforeHold))
            let state = NativeReservationFixture.snapshot f f.AccountTwo survivingTwo
            Assert.That(state.Records.Length, Is.EqualTo(1))
            Assert.That(state.Records[0], Is.EqualTo(initial.CurrentReservation))
            let competitor = NativeReservationFixture.worker f 1
            match NativeReservationFixture.reserve f f.AccountTwo survivingTwo
                    (NativeReservationFixture.coverage f f.AccountTwo survivingTwo)
                    (Guid.NewGuid()) competitor with
            | Error(AccountCapacityFull(1,1)) -> ()
            | other -> failwithf "Removed project's reservation lost occupancy: %A" other)

    [<Test>]
    member _.ReviewerClaimVerifierRequiresCurrentCanonicalReviewerRole() =
        NativeFixture.withGit (fun f ->
            let candidate = NativeReservationFixture.reviewer f 0
            let project = f.ProjectsTwo[0]
            let verified = LedgerStore.fetch project |> NativeReservationFixture.success
            NativeSchedulerAccountReservation.verifyReviewerClaim project verified candidate
            |> NativeReservationFixture.success |> ignore
            RoleInstructions.detachRole f.ProjectsOne[0] "AuthenticatedUser" (Guid.NewGuid())
                "Reviewer 1" "Reviewer" 1
            |> NativeReservationFixture.success |> ignore
            let current = LedgerStore.fetch project |> NativeReservationFixture.success
            match NativeSchedulerAccountReservation.verifyReviewerClaim project current candidate with
            | Error(AccountPolicyNotAuthoritative _) -> ()
            | other -> failwithf "Role removal did not invalidate reviewer claim: %A" other)

    [<Test>]
    member _.ForgedWorkerLeaseAndReviewerClaimRemainDeniedAcrossTwoClones() =
        NativeFixture.withGit (fun f ->
            NativeReservationFixture.establish f
                (NativeReservationFixture.limits 2 2)
                [| NativeReservationFixture.limits 2 2
                   NativeReservationFixture.limits 2 2 |]
            let worker = NativeReservationFixture.worker f 0
            let reviewer = NativeReservationFixture.reviewer f 1
            let assertDenied label (candidate: NativeDispatchCandidate) =
                let before = NativeFixture.head f.AccountOne
                let beforeState = NativeReservationFixture.snapshot f f.AccountOne f.ProjectsOne
                match NativeReservationFixture.reserve f f.AccountTwo f.ProjectsTwo
                        (NativeReservationFixture.coverage f f.AccountTwo f.ProjectsTwo)
                        (Guid.NewGuid()) candidate with
                | Error(AccountPolicyNotAuthoritative _) -> ()
                | other -> failwithf "%s was admitted without canonical proof: %A" label other
                Assert.That(NativeFixture.head f.AccountOne, Is.EqualTo(before), label)
                let afterState = NativeReservationFixture.snapshot f f.AccountOne f.ProjectsOne
                Assert.That(afterState.Records.Length, Is.EqualTo(beforeState.Records.Length), label)
                Assert.That(afterState.Records |> Array.map _.CanonicalKey, Is.EquivalentTo(beforeState.Records |> Array.map _.CanonicalKey), label)
            assertDenied "No such Worker task" { worker with TaskId = "forged-task" }
            assertDenied "No such Worker attempt" { worker with AttemptId = Guid.NewGuid().ToString("N") }
            assertDenied "Worker reassigned generation" { worker with AssignmentGeneration = 2L }
            assertDenied "Worker forged source lease hash" { worker with SourceLeaseSha256 = String.replicate 64 "b" }
            assertDenied "Worker forged source ref" { worker with SourceRef = "refs/heads/forged-source" }
            assertDenied "Worker forged source base" { worker with SourceBaseOid = f.InitialSourceHead }
            assertDenied "Worker forged repository" { worker with SourceRepositoryId = String.replicate 64 "e" }
            assertDenied "No such Reviewer claim" { reviewer with ClaimOperationId = Guid.NewGuid().ToString("D") }
            assertDenied "No such Reviewer review" { reviewer with ReviewId = Guid.NewGuid().ToString("N") }
            assertDenied "Reviewer stale version" { reviewer with ReviewVersion = 1L }
            assertDenied "Reviewer forged frozen source" { reviewer with ReviewedHeadOid = f.InitialSourceHead }
            assertDenied "Reviewer forged review ref" { reviewer with ReviewRef = "refs/heads/jarvis-review/fixture/forged/r1" }
            // The account head and original native occupancy remain unchanged
            // after the project itself advances, invalidating the signed coverage.
            let stale = NativeReservationFixture.coverage f f.AccountOne f.ProjectsOne
            let project = f.ProjectsOne[0]
            LedgerStore.transact project
                { OperationId = Guid.NewGuid()
                  Actor = "Disposable project TOCTOU adversary"
                  Kind = "FixtureSourceAdvance"
                  RequestJson = """{"advance":true}""" }
                (fun _ -> Ok [
                    { Path = "records/native-scheduler/stale-control.json"
                      ExpectedBlobOid = None; Content = """{"control":true}""" } ])
            |> NativeReservationFixture.success |> ignore
            let accountBefore = NativeFixture.head f.AccountOne
            match NativeReservationFixture.reserve f f.AccountOne f.ProjectsOne
                    stale (Guid.NewGuid()) worker with
            | Error(AccountPolicyNotAuthoritative _) -> ()
            | other -> failwithf "Stale project-head observation admitted: %A" other
            Assert.That(NativeFixture.head f.AccountOne, Is.EqualTo(accountBefore))
            Assert.That((NativeReservationFixture.snapshot f f.AccountOne f.ProjectsOne).Records.Length,
                        Is.EqualTo(0)))

    [<Test>]
    member _.LegacyHeldOrIncompleteCoverageAndUnreceiptedNativePathHoldClosed() =
        NativeFixture.withGit (fun f ->
            NativeReservationFixture.establish f
                (NativeReservationFixture.limits 2 1)
                [| NativeReservationFixture.limits 2 1
                   NativeReservationFixture.limits 2 1 |]
            let candidate = NativeReservationFixture.worker f 0
            let coverage = NativeReservationFixture.coverage f f.AccountOne f.ProjectsOne
            match NativeReservationFixture.reserve f f.AccountOne f.ProjectsOne
                    { coverage with ProjectHeads = [| coverage.ProjectHeads[0] |] } (Guid.NewGuid()) candidate with
            | Error(AccountPolicyNotAuthoritative _) -> ()
            | other -> failwithf "Incomplete session coverage admitted: %A" other
            match NativeReservationFixture.reserve f f.AccountOne f.ProjectsOne
                    { coverage with ObservedAtUtc = DateTimeOffset.UtcNow.AddHours(-1.0) }
                    (Guid.NewGuid()) candidate with
            | Error(AccountPolicyNotAuthoritative _) -> ()
            | other -> failwithf "Stale session coverage admitted: %A" other
            let p = f.ProjectsOne[0]
            let project =
                { ProjectId = p.ProjectId; LedgerId = p.LedgerId; Epoch = p.Epoch
                  ApprovedFetchUrl = p.ApprovedFetchUrl; ApprovedPushUrl = p.ApprovedPushUrl }
            AccountCapacityLedger.reserve f.AccountOne (Guid.NewGuid()) "Legacy fixture"
                (Guid.NewGuid()) project "worker" 1L
                |> NativeReservationFixture.success |> ignore
            match NativeReservationFixture.reserve f f.AccountOne f.ProjectsOne
                    (NativeReservationFixture.coverage f f.AccountOne f.ProjectsOne)
                    (Guid.NewGuid()) candidate with
            | Error(AccountPolicyNotAuthoritative _) -> ()
            | other -> failwithf "Legacy Held coexistence was not fenced: %A" other)

    [<Test>]
    member _.MissingConfiguredRosterCannotBeBypassedByCallerObservation() =
        NativeFixture.withGit (fun f ->
            let c = NativeReservationFixture.worker f 0
            let guessed =
                { AccountHead = NativeFixture.head f.AccountOne
                  ProjectHeads = [||]; ObservedAtUtc = DateTimeOffset.UtcNow }
            match NativeSchedulerAccountReservation.reserve f.AccountOne f.ProjectsOne
                    guessed (Guid.NewGuid()) "Fixture caller" c with
            | Error(AccountPolicyNotAuthoritative _) -> ()
            | other -> failwithf "Missing roster permitted reservation: %A" other)

    [<Test>]
    member _.PublishedReservationWithLostPushAcknowledgmentDoesNotRearm() =
        NativeFixture.withGit (fun f ->
            NativeReservationFixture.establish f
                (NativeReservationFixture.limits 1 1)
                [| NativeReservationFixture.limits 1 1
                   NativeReservationFixture.limits 1 1 |]
            let c = NativeReservationFixture.worker f 0
            let op = Guid.NewGuid()
            let coverage = NativeReservationFixture.coverage f f.AccountOne f.ProjectsOne
            let hooks =
                { BeforeLeasePush = fun _ _ -> ()
                  GitCommand = function
                    | "push" :: _ -> AccountGitTestFault.ExecuteThenLoseAcknowledgment
                    | _ -> AccountGitTestFault.PassThrough }
            let original =
                AccountCapacityLedger.withTestInterposition hooks (fun () ->
                    NativeReservationFixture.reserve f f.AccountOne f.ProjectsOne
                        coverage op c)
                |> NativeReservationFixture.success
            Assert.That(original.AlreadyApplied, Is.False)
            let replay = NativeReservationFixture.reserve f f.AccountTwo f.ProjectsTwo
                             coverage op c |> NativeReservationFixture.success
            Assert.That(replay.AlreadyApplied, Is.True)
            Assert.That(replay.Receipt.RequestSha256, Is.EqualTo(original.Receipt.RequestSha256))
            Assert.That((NativeReservationFixture.snapshot f f.AccountTwo f.ProjectsTwo).Records.Length,
                        Is.EqualTo(1))
            let fresh = NativeReservationFixture.coverage f f.AccountOne f.ProjectsOne
            let alternateDispatch = Guid.NewGuid().ToString("D")
            let alternateEffect = Guid.NewGuid().ToString("D")
            let sameAttempt : NativeDispatchCandidate =
                { c with DispatchId = alternateDispatch; EffectOperationId = alternateEffect }
            match NativeReservationFixture.reserve f f.AccountOne f.ProjectsOne fresh
                    (Guid.NewGuid()) sameAttempt with
            | Error(AccountPolicyNotAuthoritative _) -> ()
            | other -> failwithf "Lost-ACK restart booked a second copy of candidate: %A" other)

    [<Test>]
    member _.ForgedNativeStateWithoutMatchingCASReceiptCannotAuthorizeOccupancy() =
        NativeFixture.withGit (fun f ->
            NativeReservationFixture.establish f
                (NativeReservationFixture.limits 2 1)
                [| NativeReservationFixture.limits 2 1
                   NativeReservationFixture.limits 2 1 |]
            let candidate = NativeReservationFixture.worker f 0
            NativeReservationFixture.reserve f f.AccountOne f.ProjectsOne
                (NativeReservationFixture.coverage f f.AccountOne f.ProjectsOne)
                (Guid.NewGuid()) candidate
                |> NativeReservationFixture.success |> ignore
            NativeFixture.injectAccountFile f "native/scheduler-reservations-v1.json" "{}"
            |> ignore
            match NativeSchedulerAccountReservation.readVerified
                    f.AccountTwo f.ProjectsTwo
                    (NativeReservationFixture.coverage f f.AccountTwo f.ProjectsTwo) with
            | Error(AccountIntegrityError _) -> ()
            | other -> failwithf "Unreceipted Git native state rewrite was accepted: %A" other)

    [<Test>]
    member _.ReviewerVersionAliasesCannotBookTheSameVerifiedClaimAcrossTwoClones() =
        NativeFixture.withGit (fun f ->
            NativeReservationFixture.establish f
                (NativeReservationFixture.limits 2 2)
                [| NativeReservationFixture.limits 2 2
                   NativeReservationFixture.limits 2 2 |]
            let canonical = NativeReservationFixture.reviewer f 0
            Assert.That(canonical.ReviewVersion, Is.EqualTo(2L))
            NativeReservationFixture.reserve f f.AccountOne f.ProjectsOne
                (NativeReservationFixture.coverage f f.AccountOne f.ProjectsOne)
                (Guid.NewGuid()) canonical
                |> NativeReservationFixture.success |> ignore
            let before = NativeFixture.head f.AccountOne
            let original = (NativeReservationFixture.snapshot f f.AccountOne f.ProjectsOne).Records
            Assert.That(original.Length, Is.EqualTo(1))
            Assert.That(original[0].Candidate.ReviewVersion, Is.EqualTo(2L))
            // A 64-bit value truncated to int would pass the real Version=2
            // claim comparison yet create a distinct 64-bit canonical key.
            let forgedVersions =
                [| 4294967298L; 2147483648L; int64 Int32.MaxValue
                   Int64.MaxValue; -1L; 0L; Int64.MinValue |]
            for version in forgedVersions do
                let forged =
                    { canonical with ReviewVersion = version
                                     DispatchId = Guid.NewGuid().ToString("D")
                                     EffectOperationId = Guid.NewGuid().ToString("D") }
                match NativeReservationFixture.reserve f f.AccountTwo f.ProjectsTwo
                        (NativeReservationFixture.coverage f f.AccountTwo f.ProjectsTwo)
                        (Guid.NewGuid()) forged with
                | Error(AccountPolicyNotAuthoritative _) -> ()
                | other -> failwithf "Reviewer version alias %d admitted against immutable claim: %A" version other
                Assert.That(NativeFixture.head f.AccountOne, Is.EqualTo(before),
                            sprintf "Version %d changed account remote head" version)
                let current = (NativeReservationFixture.snapshot f f.AccountOne f.ProjectsOne).Records
                Assert.That(current.Length, Is.EqualTo(1))
                Assert.That(current[0].CanonicalKey, Is.EqualTo(original[0].CanonicalKey))
                Assert.That(current[0].Status, Is.EqualTo("Reserved")))
    [<Test>]
    member _.SameFrozenReviewerVersionCannotBeRebookedWithRewrittenReviewRef() =
        NativeFixture.withGit (fun f ->
            NativeReservationFixture.establish f
                (NativeReservationFixture.limits 2 2)
                [| NativeReservationFixture.limits 2 2
                   NativeReservationFixture.limits 2 2 |]
            let first = NativeReservationFixture.reviewer f 0
            NativeReservationFixture.reserve f f.AccountOne f.ProjectsOne
                (NativeReservationFixture.coverage f f.AccountOne f.ProjectsOne)
                (Guid.NewGuid()) first |> NativeReservationFixture.success |> ignore
            let rewritten =
                { first with DispatchId = Guid.NewGuid().ToString("D")
                             EffectOperationId = Guid.NewGuid().ToString("D")
                             ClaimOperationId = Guid.NewGuid().ToString("D")
                             ReviewRef = "refs/heads/jarvis-review/fixture/altered" }
            match NativeReservationFixture.reserve f f.AccountTwo f.ProjectsTwo
                    (NativeReservationFixture.coverage f f.AccountTwo f.ProjectsTwo)
                    (Guid.NewGuid()) rewritten with
            | Error(AccountPolicyNotAuthoritative _) -> ()
            | other -> failwithf "Rewritten immutable review source duplicated native slot: %A" other
            Assert.That((NativeReservationFixture.snapshot f f.AccountOne f.ProjectsOne).Records.Length,
                        Is.EqualTo(1)))
