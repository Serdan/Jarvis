module Client.Tests.WorkQueueSchedulerHeadBindingTests

open System
open System.Diagnostics
open System.IO
open System.Threading.Tasks
open NUnit.Framework
open Jarvis.WorkQueues
open Client.LedgerStore
open Client.AccountCapacityLedger
open Client.WorkQueueGitLedger
open Client.WorkQueueRoleAuthority
open Client.WorkQueueReactiveWake
open Client.WorkQueueSchedulerHostLoop
open Client.WorkQueueSchedulerHeadBinding

type private HeadGitRepo =
    { Remote: string
      Writer: string
      Reader: string
      SourceHead: string }

type private HeadGitFixture =
    { AccountWriter: AccountBinding
      AccountReader: AccountBinding
      ProjectWriters: LedgerBinding list
      ProjectReaders: LedgerBinding list
      WriterScope: SchedulerHeadScope
      ReaderScope: SchedulerHeadScope
      Repos: HeadGitRepo list }

[<TestFixture>]
type WorkQueueSchedulerHeadBindingTests() =
    let git directory args =
        let psi = ProcessStartInfo("git")
        psi.WorkingDirectory <- directory
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        for arg in args do psi.ArgumentList.Add arg
        use proc = Process.Start psi
        let stdout = proc.StandardOutput.ReadToEnd()
        let stderr = proc.StandardError.ReadToEnd()
        if not (proc.WaitForExit(20000)) then
            proc.Kill true
            failwithf "Disposable git command timed out: %A" args
        Assert.That(proc.ExitCode, Is.Zero, stderr)
        stdout.Trim()

    let succeed = function
        | Ok value -> value
        | Error err -> failwithf "Disposable Git setup unexpectedly failed: %A" err

    let fixture action =
        let root = Path.Combine(Path.GetTempPath(), "wq-head-observer-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        let makeRepo name =
            let remote = Path.Combine(root, name + ".git")
            let writer = Path.Combine(root, name + "-writer")
            let reader = Path.Combine(root, name + "-reader")
            git root ["init"; "--bare"; "--initial-branch=main"; remote] |> ignore
            git root ["clone"; remote; writer] |> ignore
            git writer ["config"; "user.name"; "Head Observer Fixture"] |> ignore
            git writer ["config"; "user.email"; "head-observer@example.invalid"] |> ignore
            File.WriteAllText(Path.Combine(writer, "source.txt"), name)
            git writer ["add"; "source.txt"] |> ignore
            git writer ["commit"; "-m"; "fixture source"] |> ignore
            git writer ["push"; "-u"; "origin"; "HEAD"] |> ignore
            git root ["clone"; remote; reader] |> ignore
            { Remote = remote; Writer = writer; Reader = reader
              SourceHead = git writer ["rev-parse"; "HEAD"] }
        try
            // Three independently approved canonical refs: one account, two projects.
            let a = makeRepo "account"
            let p1 = makeRepo "project-one"
            let p2 = makeRepo "project-two"
            let accountId, accountLedgerId = Guid.NewGuid(), Guid.NewGuid()
            let projectIds = [Guid.NewGuid(); Guid.NewGuid()]
            let projectLedgerIds = [Guid.NewGuid(); Guid.NewGuid()]
            let account path: AccountBinding =
                { RepositoryPath = path; RemoteName = "origin"
                  ApprovedFetchUrl = a.Remote; ApprovedPushUrl = a.Remote
                  AccountId = accountId; LedgerId = accountLedgerId; Epoch = 1 }
            let project (repo: HeadGitRepo) id ledger path: LedgerBinding =
                { RepositoryPath = path; RemoteName = "origin"
                  ApprovedFetchUrl = repo.Remote; ApprovedPushUrl = repo.Remote
                  ProjectId = id; LedgerId = ledger; Epoch = 1 }
            let writers =
                [project p1 projectIds.[0] projectLedgerIds.[0] p1.Writer
                 project p2 projectIds.[1] projectLedgerIds.[1] p2.Writer]
            let readers =
                [project p1 projectIds.[0] projectLedgerIds.[0] p1.Reader
                 project p2 projectIds.[1] projectLedgerIds.[1] p2.Reader]
            AccountCapacityLedger.initialize (account a.Writer) 2 |> succeed |> ignore
            for p in writers do
                LedgerStore.initialize p true |> succeed |> ignore
                WorkQueueGitLedger.initialize p "fixture" (Guid.NewGuid())
                    (Map.ofList ["Worker", set ["implementation"]])
                |> succeed |> ignore
                WorkQueueRoleAuthority.initializeCatalog p "fixture" (Guid.NewGuid())
                |> succeed |> ignore
                let role: RoleConfiguration =
                    { RoleId = "Worker"; Version = 1; Enabled = true
                      QueueSubscriptions = set ["implementation"]; Instructions = "Fixture worker" }
                WorkQueueRoleAuthority.configure p "fixture" (Guid.NewGuid()) 0 0 role
                |> succeed |> ignore
            let membership =
                { AccountId = accountId
                  ProjectIds = Set.ofList projectIds
                  RoleSubscriptions =
                    (projectIds
                     |> List.map (fun id -> id, Map.ofList ["Worker", set ["implementation"]])
                     |> Map.ofList)
                  // Independent test fixture approval is pinned to exact WQ04
                  // catalog identities after completing their legitimate setup.
                  ApprovedRoleCatalogs =
                    (writers
                     |> List.map (fun writer ->
                         let snapshot = LedgerStore.fetch writer |> succeed
                         let pinned = WorkQueueRoleAuthority.loadAt writer snapshot |> succeed
                         writer.ProjectId,
                         { BlobOid = pinned.Proof.CatalogBlobOid
                           Revision = pinned.Proof.CatalogRevision })
                     |> Map.ofList)
                  AuthorityDescription = "Explicit disposable test-only operator approval" }
            let scope ab bs =
                { Account = ab
                  Projects = bs |> List.mapi (fun i b -> { Alias = "Project-" + string (i + 1); Binding = b })
                  Membership = Some membership }
            let data =
                { AccountWriter = account a.Writer
                  AccountReader = account a.Reader
                  ProjectWriters = writers
                  ProjectReaders = readers
                  WriterScope = scope (account a.Writer) writers
                  ReaderScope = scope (account a.Reader) readers
                  Repos = [a; p1; p2] }
            action data
            for repo in data.Repos do
                Assert.That(git repo.Writer ["rev-parse"; "HEAD"], Is.EqualTo(repo.SourceHead))
                Assert.That(git repo.Reader ["rev-parse"; "HEAD"], Is.EqualTo(repo.SourceHead))
                Assert.That(git repo.Writer ["status"; "--porcelain"], Is.Empty)
                Assert.That(git repo.Reader ["status"; "--porcelain"], Is.Empty)
        finally
            try Directory.Delete(root, true) with _ -> ()

    let hints = function
        | SchedulerHeadScan.Hints list -> list
        | other -> failwithf "Expected verified head wake hints, got %A" other

    let assertUnknown = function
        | SchedulerHeadScan.NeedsAttention(_, reason) ->
            Assert.That(reason, Is.Not.Empty)
        | other -> Assert.Fail(sprintf "Expected fail-closed head read, got %A" other)

    [<Test>]
    member _.TwoProjectsTwoClientsReadExactCanonicalRefsAndOnlySignalChangedHeads() =
        fixture (fun data ->
            let one = Observer(data.WriterScope, enabled = true)
            let two = Observer(data.ReaderScope, enabled = true)
            let observations = [one.ReadAll() |> succeed; two.ReadAll() |> succeed]
            for observation in observations do
                Assert.That(observation.AccountRef, Is.EqualTo(AccountCapacityLedger.AccountRef))
                Assert.That(observation.AccountHead.Length, Is.EqualTo(40))
                Assert.That(observation.AccountCapacity, Is.EqualTo(2))
                Assert.That(observation.Projects.Count, Is.EqualTo(2))
                for entry in observation.Projects.Values do
                    Assert.That(entry.Ref, Is.EqualTo(ProjectRef))
                    Assert.That(entry.Head.Length, Is.EqualTo(40))
                    Assert.That(entry.RoleCatalogRevision, Is.EqualTo(1))
                    Assert.That(entry.RoleVersions.["Worker"], Is.EqualTo(1))
            for observer in [one;two] do
                Assert.That(hints (observer.Scan false) |> List.length, Is.EqualTo(2))
                Assert.That(observer.Scan false, Is.EqualTo(SchedulerHeadScan.Dormant))

            // A canonical project commit is a *possible* change, not proof of
            // the event class. Conservatively emit one ManualReconcile hint.
            let changedId = data.ProjectWriters.Head.ProjectId
            LedgerStore.putRecord data.ProjectWriters.Head
                { OperationId = Guid.NewGuid()
                  Actor = "head-observer-fixture"
                  Path = "records/head-observer-hint.json"
                  ExpectedBlobOid = None
                  Content = """{"event":"integration"}""" }
            |> succeed |> ignore
            for observer in [one;two] do
                let changed = hints (observer.Scan false)
                Assert.That((changed |> List.map _.ProjectId) = [changedId], Is.True)
                Assert.That(changed.Head.Trigger, Is.EqualTo(WorkWakeTrigger.ManualReconcile))
                Assert.That(observer.Scan false, Is.EqualTo(SchedulerHeadScan.Dormant))

            // Account-global capacity changes affect all approved projects.
            let account = AccountCapacityLedger.fetch data.AccountWriter |> succeed
            AccountCapacityLedger.setCapacity data.AccountWriter
                (Guid.NewGuid()) "fixture" account.State.ConfigVersion 3
            |> succeed |> ignore
            for observer in [one;two] do
                Assert.That(hints (observer.Scan false) |> List.length, Is.EqualTo(2))
                Assert.That(observer.Scan false, Is.EqualTo(SchedulerHeadScan.Dormant))
                // A full bounded rescan recovers an entirely lost hint.
                Assert.That(hints (observer.Scan true) |> List.length, Is.EqualTo(2))
                Assert.That(observer.Scan false, Is.EqualTo(SchedulerHeadScan.Dormant)))

    [<Test>]
    member _.NoApprovalOrContradictoryMembershipNeverStartsGitOrHostCallbacks() =
        fixture (fun data ->
            let mutable calls = 0
            let poisoned =
                { realReadPorts with
                    ReadAccount = fun _ -> calls <- calls + 1; Error "unexpected Git fetch" }
            let off = Observer(data.ReaderScope, ports = poisoned)
            Assert.That(off.Scan true, Is.EqualTo(SchedulerHeadScan.Disabled))
            Assert.That(off.ReadAll() |> Result.isError, Is.True)
            let missing = Observer({ data.ReaderScope with Membership = None },
                                   enabled = true, ports = poisoned)
            assertUnknown (missing.Scan false)
            let badMember =
                data.ReaderScope.Membership.Value
                |> fun m -> { m with AccountId = Guid.NewGuid() }
            let mismatched =
                Observer({ data.ReaderScope with Membership = Some badMember },
                         enabled = true, ports = poisoned)
            assertUnknown (mismatched.Scan true)
            let missingCatalog =
                { data.ReaderScope.Membership.Value with ApprovedRoleCatalogs = Map.empty }
            assertUnknown (Observer({ data.ReaderScope with Membership = Some missingCatalog },
                                    enabled = true, ports = poisoned).Scan false)
            let duplicateAlias =
                { data.ReaderScope with
                    Projects =
                        [ data.ReaderScope.Projects.[0]
                          { data.ReaderScope.Projects.[1] with
                              Alias = data.ReaderScope.Projects.[0].Alias } ] }
            assertUnknown (Observer(duplicateAlias, enabled = true, ports = poisoned).Scan false)
            let duplicateId =
                { data.ReaderScope with
                    Projects =
                        [ data.ReaderScope.Projects.[0]
                          { data.ReaderScope.Projects.[1] with
                              Binding = data.ReaderScope.Projects.[0].Binding } ] }
            assertUnknown (Observer(duplicateId, enabled = true, ports = poisoned).Scan true)
            Assert.That(calls, Is.Zero, "Invalid approval must fail before remote Git reads."))

    [<Test>]
    member _.ChangedSnapshotUnreachableRemoteAndCrossProjectTokensNeedAttention() =
        fixture (fun data ->
            let unavailable =
                { realReadPorts with ReadProject = fun _ -> Error "unavailable remote" }
            assertUnknown (Observer(data.ReaderScope, enabled = true, ports = unavailable).Scan false)
            let mutable reads = 0
            let unstable =
                { realReadPorts with
                    ReadAccount = fun binding ->
                        reads <- reads + 1
                        realReadPorts.ReadAccount binding
                        |> Result.map (fun snapshot ->
                            if reads = 1 then snapshot
                            else { snapshot with Head = String.replicate 40 "f" }) }
            assertUnknown (Observer(data.ReaderScope, enabled = true, ports = unstable).Scan true)

            let token =
                { LaunchId = string (Guid.NewGuid())
                  ProjectId = string (Guid.NewGuid())
                  ProjectLedgerId = string (Guid.NewGuid())
                  ProjectLedgerEpoch = 1
                  ProjectAuthoritySha256 = String.replicate 64 "a"
                  RoleId = "Worker"; RoleConfigVersion = 1L
                  AdmissionOperationId = string (Guid.NewGuid())
                  Status = "Held"
                  ReleaseProof = Unchecked.defaultof<AccountTerminalProof> }
            let conflicting =
                { realReadPorts with
                    ReadAccount = fun binding ->
                        realReadPorts.ReadAccount binding
                        |> Result.map (fun snap ->
                            { snap with State = { snap.State with Tokens = [| token |] } }) }
            assertUnknown (Observer(data.ReaderScope, enabled = true, ports = conflicting).Scan false))

    [<Test>]
    member _.RoleSubscriptionChangeRequiresRenewedApprovalInsteadOfSelectingAnotherRole() =
        fixture (fun data ->
            let observer = Observer(data.ReaderScope, enabled = true)
            Assert.That(hints (observer.Scan false) |> List.length, Is.EqualTo(2))
            let project = data.ProjectWriters.Head
            let next: RoleConfiguration =
                { RoleId = "Worker"; Version = 2; Enabled = true
                  QueueSubscriptions = set ["review"]; Instructions = "Changed worker" }
            WorkQueueRoleAuthority.configure project "fixture" (Guid.NewGuid()) 1 1 next
            |> succeed |> ignore
            assertUnknown (observer.Scan false))

    [<TestCase("version")>]
    [<TestCase("disabled")>]
    [<TestCase("instructions")>]
    member _.SameSubscriptionRoleCatalogChangesBlockHostsUntilIndependentlyReapproved(changeKind: string) =
        fixture (fun data ->
            let observer = Observer(data.ReaderScope, enabled = true)
            Assert.That(hints (observer.Scan false) |> List.length, Is.EqualTo(2))
            let project = data.ProjectWriters.Head
            let next: RoleConfiguration =
                { RoleId = "Worker"; Version = 2
                  Enabled = changeKind <> "disabled"
                  QueueSubscriptions = set ["implementation"]
                  Instructions =
                    if changeKind = "instructions" then "Changed worker instructions"
                    else "Fixture worker" }
            WorkQueueRoleAuthority.configure project "fixture" (Guid.NewGuid()) 1 1 next
            |> succeed |> ignore

            // A fresh Git head with identical subscriptions is NOT renewed
            // operator approval of the changed version/enablement/instructions.
            match observer.ReadAll() with
            | Error reason -> Assert.That(reason, Does.Contain("RoleCatalogApprovalChanged"))
            | Ok _ -> Assert.Fail("A changed WQ04 role catalog must invalidate old approval.")
            Assert.That(observer.ReadVerifiedHeads project.ProjectId |> Result.isError, Is.True)
            assertUnknown (observer.Scan false)
            let mutable hostReads, wakes = 0, 0
            let hostPorts: SchedulerHostPorts =
                { ReadVerifiedHeads = fun () ->
                    hostReads <- hostReads + 1
                    Error "Host must not run with stale role approval."
                  Wake = fun _ ->
                    wakes <- wakes + 1
                    failwith "Coordinator must not wake with stale role approval."
                  Report = ignore
                  Delay = fun _ _ -> Task.CompletedTask }
            use first = new WorkQueueSchedulerHostLoop.Host(hostPorts, enabled = true)
            use second = new WorkQueueSchedulerHostLoop.Host(hostPorts, enabled = true)
            let hosts =
                [ data.ProjectReaders.[0].ProjectId, first
                  data.ProjectReaders.[1].ProjectId, second ] |> Map.ofList
            match runOneManualStep observer hosts false with
            | SchedulerManualStep.NeedsAttention("VerifiedHeadReadUnknown", reason) ->
                Assert.That(reason, Does.Contain("RoleCatalogApprovalChanged"))
            | other ->
                Assert.Fail(sprintf "Stale role approval must block all host callbacks: %A" other)
            Assert.That(hostReads, Is.Zero)
            Assert.That(wakes, Is.Zero)

            // Only a separately supplied, explicitly renewed catalog identity
            // allows this revised role configuration through the read boundary.
            let pinned = WorkQueueRoleAuthority.loadAt project (LedgerStore.fetch project |> succeed)
                         |> succeed
            let membership = data.ReaderScope.Membership.Value
            let renewed =
                { membership with
                    ApprovedRoleCatalogs =
                        membership.ApprovedRoleCatalogs.Add(project.ProjectId,
                            { BlobOid = pinned.Proof.CatalogBlobOid
                              Revision = pinned.Proof.CatalogRevision }) }
            let reapproved =
                Observer({ data.ReaderScope with Membership = Some renewed }, enabled = true)
            Assert.That(reapproved.ReadAll() |> Result.isOk, Is.True))

    [<Test>]
    member _.NullProjectScopeNeverThrowsOrInvokesManualHost() =
        fixture (fun data ->
            let incomplete =
                { data.ReaderScope with Projects = Unchecked.defaultof<_> }
            let observer = Observer(incomplete, enabled = true)
            match runOneManualStep observer Map.empty false with
            | SchedulerManualStep.NeedsAttention("VerifiedHeadReadUnknown", reason) ->
                Assert.That(reason, Does.Contain("ProjectMembershipUnknown"))
            | other ->
                Assert.Fail(sprintf "Missing project scope must fail closed: %A" other))

    [<Test>]
    member _.ManualStepUsesOnlyApprovedHostsAndNeverCallsPlannerOrProviderWithoutProvenance() =
        fixture (fun data ->
            let observer = Observer(data.ReaderScope, enabled = true)
            let mutable plans, providers = 0, 0
            let makeHost (project: LedgerBinding) =
                let ports: WorkWakePorts =
                    { ReadVerified = WorkQueueReactiveWake.boundRead data.AccountReader project "Worker"
                      Plan = fun _ -> plans <- plans + 1; Ok None
                      Execute = fun _ -> providers <- providers + 1; WorkExecutorResult.Applied }
                let coordinator = WorkQueueReactiveWake.Coordinator(ports, enabled = true)
                let hostPorts =
                    WorkQueueSchedulerHostLoop.productionPorts coordinator
                        (fun () -> observer.ReadVerifiedHeads project.ProjectId) ignore
                new WorkQueueSchedulerHostLoop.Host(hostPorts, enabled = true)
            use first = makeHost data.ProjectReaders.[0]
            use second = makeHost data.ProjectReaders.[1]
            let hosts =
                [data.ProjectReaders.[0].ProjectId, first
                 data.ProjectReaders.[1].ProjectId, second] |> Map.ofList
            match runOneManualStep observer (hosts |> Map.remove data.ProjectReaders.[1].ProjectId) true with
            | SchedulerManualStep.NeedsAttention("HostMembershipMismatch", _) -> ()
            | other -> Assert.Fail(sprintf "Incomplete approved host graph must block: %A" other)
            match runOneManualStep observer hosts false with
            | SchedulerManualStep.Reconciled results ->
                Assert.That(results.Length, Is.EqualTo(2))
                for (_, result) in results do
                    match result with
                    | SchedulerHostOutcome.Reconciled(
                        WorkWakeResult.NeedsAttention("AccountNotActivated", _)) -> ()
                    | other -> Assert.Fail(sprintf "Expected fail-closed legacy migration: %A" other)
            | other -> Assert.Fail(sprintf "Expected one safe manual sweep: %A" other)
            Assert.That(runOneManualStep observer hosts false,
                        Is.EqualTo(SchedulerManualStep.Dormant))
            match runOneManualStep observer hosts true with
            | SchedulerManualStep.Reconciled results ->
                Assert.That(results.Length, Is.EqualTo(2))
            | other -> Assert.Fail(sprintf "Full rescan must converge lost hints: %A" other)
            Assert.That(plans, Is.Zero)
            Assert.That(providers, Is.Zero)
            Assert.That(first.IsRunning, Is.False)
            Assert.That(second.IsRunning, Is.False))
