module Client.Tests.NativeSchedulerRosterPolicyTests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Client.AccountCapacityLedger
open Client.LedgerStore
open Client.NativeSchedulerRosterPolicy
open NUnit.Framework

type private NativeFixture =
    { Root: string
      AccountOne: AccountBinding
      AccountTwo: AccountBinding
      ProjectsOne: LedgerBinding array
      ProjectsTwo: LedgerBinding array
      Signer: ECDsa
      PublicKey: byte array
      Subject: string
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
                       yield project |]
            let secondClientProjects =
                projects |> Array.mapi (fun i p ->
                    let checkout = Path.Combine(root, $"project-second-{i}")
                    git root [ "clone"; p.ApprovedFetchUrl; checkout ] |> ignore
                    { p with RepositoryPath = checkout })
            use signer = ECDsa.Create(ECCurve.NamedCurves.nistP256)
            let f =
                { Root = root; AccountOne = account one; AccountTwo = account two
                  ProjectsOne = projects; ProjectsTwo = secondClientProjects
                  Signer = signer; PublicKey = signer.ExportSubjectPublicKeyInfo()
                  Subject = "auth0|signed-disposable-native-fixture"
                  AccountRemote = remote; AccountCheckoutOne = one
                  AccountCheckoutTwo = two; InitialSourceHead = sourceHead }
            action f
            Assert.That(git one [ "rev-parse"; "HEAD" ], Is.EqualTo(sourceHead))
            Assert.That(git two [ "rev-parse"; "HEAD" ], Is.EqualTo(sourceHead))
            Assert.That(git one [ "status"; "--porcelain" ], Is.Empty)
            Assert.That(git two [ "status"; "--porcelain" ], Is.Empty)
        finally
            Directory.Delete(root, true)

    let approval (f: NativeFixture) (version: int64) (previous: int64)
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
              AuthenticatedSubject = f.Subject; Version = version
              MembershipComplete = true; AccountLimits = accountLimits
              Projects = projects }
        let unsigned =
            { SchemaVersion = 1; OperationId = operationId.ToString("D")
              Actor = "Authenticated fixture operator"; PreviousVersion = previous
              ExpectedAccountHead = head; Policy = policy
              SignerPublicKeySha256 = NativeSchedulerRosterPolicy.keySha256 f.PublicKey
              SignatureBase64 = "" }
        let signature =
            f.Signer.SignData(NativeSchedulerRosterPolicy.approvalMessage unsigned,
                              HashAlgorithmName.SHA256)
        { unsigned with SignatureBase64 = Convert.ToBase64String signature }

    let resign (f: NativeFixture) (approval: NativeSignedRosterApproval) =
        let signature =
            f.Signer.SignData(NativeSchedulerRosterPolicy.approvalMessage approval,
                              HashAlgorithmName.SHA256)
        { approval with SignatureBase64 = Convert.ToBase64String signature }

    let writer f account projects approval =
        AccountCapacityLedger.setVerifiedDisposableNativeRosterPolicy
            f.Root account f.PublicKey f.Subject projects approval

    let reader f account projects =
        AccountCapacityLedger.readVerifiedDisposableNativeRosterPolicy
            f.Root account f.PublicKey f.Subject projects

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
    member _.SignedCompletePolicyUsesSameAccountHeadButNeverAuthorizesLaunch() =
        NativeFixture.withGit (fun f ->
            let before = AccountCapacityLedger.fetch f.AccountOne |> NativeFixture.success
            let record =
                NativeFixture.approval f 1L 0L before.Head (Guid.NewGuid())
                    (NativeFixture.limits 3 2)
                    [| NativeFixture.limits 0 1; NativeFixture.limits 2 0 |]
            match AccountCapacityLedger.setNativeRosterPolicy f.AccountOne record with
            | Error(AccountPolicyNotAuthoritative _) -> ()
            | other -> failwithf "Production host must be disabled: %A" other
            Assert.That((AccountCapacityLedger.readNativeRosterPolicy f.AccountOne).Status,
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
            Assert.That((AccountCapacityLedger.readNativeRosterPolicy f.AccountTwo).Status,
                        Is.EqualTo("NotAuthoritative"))
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
    member _.ExactReplayChangedPayloadAndLostAcknowledgmentRemainReceiptBound() =
        NativeFixture.withGit (fun f ->
            let initial = NativeFixture.head f.AccountOne
            let original =
                NativeFixture.approval f 1L 0L initial (Guid.NewGuid())
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
                |> NativeFixture.resign f
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
                NativeFixture.approval f 1L 0L (NativeFixture.head f.AccountOne)
                    (Guid.NewGuid()) (NativeFixture.limits 2 2)
                    [| NativeFixture.limits 1 1; NativeFixture.limits 1 1 |]
            NativeFixture.writer f f.AccountOne f.ProjectsOne first
            |> NativeFixture.success |> ignore
            let head = NativeFixture.head f.AccountOne
            let a = NativeFixture.approval f 2L 1L head (Guid.NewGuid())
                        (NativeFixture.limits 4 1)
                        [| NativeFixture.limits 1 1; NativeFixture.limits 2 0 |]
            let b = NativeFixture.approval f 2L 1L head (Guid.NewGuid())
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
                NativeFixture.approval f 4L 2L (NativeFixture.head f.AccountTwo)
                    (Guid.NewGuid()) (NativeFixture.limits 1 1)
                    [| NativeFixture.limits 1 1; NativeFixture.limits 0 0 |]
                |> NativeFixture.resign f
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
                NativeFixture.approval f 1L 0L (NativeFixture.head f.AccountOne)
                    (Guid.NewGuid()) (NativeFixture.limits 0 0)
                    [| NativeFixture.limits 0 0; NativeFixture.limits 0 0 |]
            let invalidCases =
                [ { original with SignatureBase64 = "not-a-signature" }
                  { original with Policy = { original.Policy with
                                               MembershipComplete = false } }
                      |> NativeFixture.resign f
                  { original with Policy = { original.Policy with Projects = [||] } }
                      |> NativeFixture.resign f
                  { original with Policy = { original.Policy with
                                               Projects = [| original.Policy.Projects[0];
                                                             { original.Policy.Projects[1] with
                                                                 Alias = original.Policy.Projects[0].Alias } |] } }
                      |> NativeFixture.resign f
                  { original with Policy = { original.Policy with
                                               Projects = [| original.Policy.Projects[0];
                                                             { original.Policy.Projects[1] with
                                                                 ProjectId = original.Policy.Projects[0].ProjectId } |] } }
                      |> NativeFixture.resign f
                  { original with Policy = { original.Policy with AccountEpoch = 9 } }
                      |> NativeFixture.resign f
                  { original with Policy = { original.Policy with
                                               AuthenticatedSubject = "auth0|foreign-user" } }
                      |> NativeFixture.resign f
                  { original with Policy = { original.Policy with
                                               Projects = [| { original.Policy.Projects[0] with
                                                                 RemoteAuthoritySha256 = String.replicate 64 "0" };
                                                             original.Policy.Projects[1] |] } }
                      |> NativeFixture.resign f
                  { original with Policy = { original.Policy with
                                               Projects = [| { original.Policy.Projects[0] with
                                                                 Epoch = 2 };
                                                             original.Policy.Projects[1] |] } }
                      |> NativeFixture.resign f
                  { original with Policy = { original.Policy with
                                               AccountLimits = NativeFixture.limits (-1) 0 } }
                      |> NativeFixture.resign f ]
            for invalid in invalidCases do
                match NativeFixture.writer f f.AccountOne f.ProjectsOne invalid with
                | Error _ -> ()
                | Ok _ -> failwith "Unapproved or inconsistent policy was committed."
            Assert.That(NativeFixture.head f.AccountOne, Is.EqualTo(original.ExpectedAccountHead))
            // Signer is real, but an incomplete host-enumerated binding set is
            // not a complete roster even when the policy says complete.
            NativeFixture.writer f f.AccountOne f.ProjectsOne original
            |> NativeFixture.success |> ignore
            let subset = NativeFixture.reader f f.AccountTwo [| f.ProjectsTwo[0] |]
            Assert.That(subset.Status, Is.EqualTo("NotAuthoritative"))
            let foreign = { f.ProjectsTwo[0] with Epoch = 7 }
            let untrusted = NativeFixture.reader f f.AccountTwo [| foreign; f.ProjectsTwo[1] |]
            Assert.That(untrusted.Status, Is.EqualTo("NotAuthoritative"))
            let remoteMismatch = { f.AccountTwo with ApprovedPushUrl = f.ProjectsTwo[1].ApprovedPushUrl }
            Assert.That((AccountCapacityLedger.readNativeRosterPolicy remoteMismatch).Status,
                        Is.EqualTo("NotAuthoritative")))

    [<Test>]
    member _.PolicyProofCannotBeReusedAfterOutOfBandGitRewrite() =
        NativeFixture.withGit (fun f ->
            let original =
                NativeFixture.approval f 1L 0L (NativeFixture.head f.AccountOne)
                    (Guid.NewGuid()) (NativeFixture.limits 1 1)
                    [| NativeFixture.limits 1 0; NativeFixture.limits 0 1 |]
            NativeFixture.writer f f.AccountOne f.ProjectsOne original
            |> NativeFixture.success |> ignore
            // A direct non-ledger Git commit with another signed but unaudited
            // policy value must not borrow the previous account operation receipt.
            let forged = { original with Policy = { original.Policy with Version = 2L } }
                         |> NativeFixture.resign f
            NativeFixture.injectAccountFile f NativeSchedulerRosterPolicy.PolicyPath
                (JsonSerializer.Serialize(forged)) |> ignore
            let snapshot = NativeFixture.reader f f.AccountTwo f.ProjectsTwo
            Assert.That(snapshot.Status, Is.EqualTo("NotAuthoritative"))
            Assert.That(snapshot.LaunchAuthorized, Is.False))

    [<Test>]
    member _.FrozenWq07StateFencesNativePolicyEvenWithValidSignatureAndAccountHead() =
        NativeFixture.withGit (fun f ->
            let original =
                NativeFixture.approval f 1L 0L (NativeFixture.head f.AccountOne)
                    (Guid.NewGuid()) (NativeFixture.limits 1 1)
                    [| NativeFixture.limits 1 0; NativeFixture.limits 0 1 |]
            NativeFixture.writer f f.AccountOne f.ProjectsOne original
            |> NativeFixture.success |> ignore
            // Simulate the Frozen WQ07 state in a disposable bare remote;
            // the policy has not been changed, and still carries a valid signer.
            let oldState = (AccountCapacityLedger.fetch f.AccountOne |> NativeFixture.success).State
            NativeFixture.injectAccountFile f "state.json"
                (JsonSerializer.Serialize({ oldState with MigrationPhase = "Frozen" })) |> ignore
            let snapshot = NativeFixture.reader f f.AccountTwo f.ProjectsTwo
            Assert.That(snapshot.Status, Is.EqualTo("NotAuthoritative"))
            Assert.That(snapshot.AccountCapacityComplete, Is.False)
            let next =
                NativeFixture.approval f 2L 1L (NativeFixture.head f.AccountOne)
                    (Guid.NewGuid()) (NativeFixture.limits 2 1)
                    [| NativeFixture.limits 1 0; NativeFixture.limits 1 1 |]
            match NativeFixture.writer f f.AccountOne f.ProjectsOne next with
            | Error(AccountMigrationWritesFenced "Frozen") -> ()
            | other -> failwithf "Frozen WQ07 migration permitted a native policy write: %A" other)


    [<Test>]
    member _.SignedApprovalCannotBeTransplantedToDifferentGitPredecessor() =
        NativeFixture.withGit (fun f ->
            let initial = NativeFixture.head f.AccountOne
            let original =
                NativeFixture.approval f 1L 0L initial (Guid.NewGuid())
                    (NativeFixture.limits 1 1)
                    [| NativeFixture.limits 1 0; NativeFixture.limits 0 1 |]
            let written = NativeFixture.writer f f.AccountOne f.ProjectsOne original
                          |> NativeFixture.success
            let committed = written.CurrentHead
            let localGit = NativeFixture.git f.AccountCheckoutOne
            let accountBranchParent = localGit [ "rev-parse"; $"{committed}^" ]
            Assert.That(accountBranchParent, Is.EqualTo(initial))
            let beforeTree = localGit [ "rev-parse"; $"{initial}^{{tree}}" ]
            let signedTree = localGit [ "rev-parse"; $"{committed}^{{tree}}" ]
            // The signature belongs to initial. A raw Git actor grafts the
            // byte-identical signed policy and receipt onto another predecessor,
            // preserving the forged receipt's commit trailers. A NEW account
            // client has no pre-existing local trusted head to detect the remote
            // rewind; checking the signed expected parent is essential.
            let alteredParent =
                localGit [ "-c"; "user.name=Fixture"; "-c"; "user.email=fixture@example.test"
                           "commit-tree"; beforeTree; "-p"; initial
                           "-m"; "Disposable unrelated account descendant" ]
            let fakeMessage =
                $"Account SetNativeSchedulerRosterPolicy\n\nOperation-Id: {original.OperationId}\nRequest-SHA256: {written.Receipt.RequestSha256}"
            let graft =
                localGit [ "-c"; "user.name=Fixture"; "-c"; "user.email=fixture@example.test"
                           "commit-tree"; signedTree; "-p"; alteredParent
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
                AccountCapacityLedger.readVerifiedDisposableNativeRosterPolicy
                    f.Root fresh f.PublicKey f.Subject f.ProjectsTwo
            Assert.That(observed.Status, Is.EqualTo("NotAuthoritative"))
            Assert.That(observed.HoldReasons[0], Does.Contain("transplanted"))
            Assert.That(observed.LaunchAuthorized, Is.False))
