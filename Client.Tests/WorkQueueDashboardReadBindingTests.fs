module Client.Tests.WorkQueueDashboardReadBindingTests

open System
open System.Diagnostics
open System.IO
open Client.LedgerStore
open Client.WorkQueueGitLedger
open Client.WorkQueueRoleAuthority
open Client.WorkQueueToolFacade
open Client.WorkDashboardView
open Client.WorkQueueDashboardReadBinding
open Jarvis.WorkQueues
open NUnit.Framework

type private ProjectFixture =
    { Binding: LedgerBinding
      Remote: string }

let private success = function
    | Ok item -> item
    | Error error -> failwithf "Unexpected verified Git fixture failure: %A" error

let private git folder arguments =
    use command = new Process()
    command.StartInfo.FileName <- "git"
    command.StartInfo.WorkingDirectory <- folder
    command.StartInfo.RedirectStandardOutput <- true
    command.StartInfo.RedirectStandardError <- true
    for argument in arguments do command.StartInfo.ArgumentList.Add argument
    Assert.That(command.Start(), Is.True)
    let output = command.StandardOutput.ReadToEnd()
    let error = command.StandardError.ReadToEnd()
    command.WaitForExit()
    Assert.That(command.ExitCode, Is.EqualTo(0), error)
    output.Trim()

let private fixture root tag rolePresent =
    let remote = Path.Combine(root, tag + ".git")
    let clone = Path.Combine(root, tag)
    git root [ "init"; "--bare"; "--initial-branch=main"; remote ] |> ignore
    git root [ "clone"; remote; clone ] |> ignore
    File.WriteAllText(Path.Combine(clone, "source.txt"), "Approved " + tag)
    git clone [ "add"; "source.txt" ] |> ignore
    git clone [ "-c"; "user.name=Fixture"; "-c"; "user.email=fixture@example.test"
                "commit"; "-m"; "fixture" ] |> ignore
    git clone [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
    let binding =
        { RepositoryPath = clone
          RemoteName = "origin"
          ApprovedFetchUrl = remote
          ApprovedPushUrl = remote
          ProjectId = Guid.NewGuid()
          LedgerId = Guid.NewGuid()
          Epoch = 1 }
    LedgerStore.initialize binding true |> success |> ignore
    WorkQueueGitLedger.initialize binding "Fixture" (Guid.NewGuid())
        (Map.ofList [ "Lead", Set.singleton "planning" ]) |> success |> ignore
    if rolePresent then
        WorkQueueRoleAuthority.initializeCatalog binding "Fixture" (Guid.NewGuid())
        |> success |> ignore
        WorkQueueRoleAuthority.configure binding "Fixture" (Guid.NewGuid()) 0 0
            { RoleId = "Lead"; Version = 1; Instructions = "Fixture pinned role"
              Enabled = true; QueueSubscriptions = Set.singleton "planning" }
        |> success |> ignore
    { Binding = binding; Remote = remote }

let private withProjectsWithRoles alphaRole betaRole action =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-work-read-ui-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(root) |> ignore
    try action (fixture root "alpha" alphaRole) (fixture root "beta" betaRole)
    finally
        try Directory.Delete(root, true) with _ -> ()

let private withProjects action =
    withProjectsWithRoles true true action

let private identity name (p: ProjectFixture) : ApprovedProjectIdentity =
    { ProjectId = p.Binding.ProjectId; DisplayName = name }

let private authority (a: ProjectFixture) (b: ProjectFixture) =
    { AccountId = "one-account"
      MembershipComplete = true
      Projects = [ identity "Zulu" b; identity "Alpha" a ]
      Bindings = [ b.Binding; a.Binding ] }

let private stage projectId id dependencies =
    { Id = id; ProjectId = projectId; SubjectId = "task-" + id
      Kind = WorkKind.Planning; QueueId = "planning"; StageGeneration = 1
      State = WorkState.Available; Domain = DomainState.Undecided
      Gate = GateState.Clear; ClaimGeneration = 0
      Dependencies = Set.ofList dependencies; WaivedDependencies = Map.empty
      AvailableAt = 100L; AvailableSequence = 1L
      SourceLineage = None; Push = None; IntegrationAuthorized = false }

let private publish (p: ProjectFixture) version id deps =
    WorkQueueGitLedger.executeInertFixtureAt p.Binding "Fixture" (Guid.NewGuid())
        version (1000L + int64 version)
        (PublishStage (stage (p.Binding.ProjectId.ToString("D")) id deps))
    |> success |> ignore

[<Test>]
let twoApprovedProjectRemotesLoadExactHeadsAndCanonicalWorkWithoutDispatch () =
    withProjects (fun a b ->
        publish a 0 "ready" []
        publish a 1 "blocked" ["ready"]
        publish b 0 "other" []
        let approved = authority a b
        let result = createAccess (fun () -> Ok approved)
        let sources = result.Load() |> success
        Assert.That(sources |> List.map _.ProjectName = ["Alpha"; "Zulu"], Is.True)
        for p, source in [ a, sources[0]; b, sources[1] ] do
            let expected = (LedgerStore.fetch p.Binding |> success).Commit
            match source.Snapshot with
            | Error reason -> Assert.Fail("Approved Git must be readable: " + reason)
            | Ok value ->
                Assert.That(value.ProjectId, Is.EqualTo(p.Binding.ProjectId.ToString("D")))
                Assert.That(value.VerifiedGitHead, Is.EqualTo(expected))
                Assert.That(value.Coverage, Is.EqualTo("Verified"))
                Assert.That(value.ReadOnly, Is.True)
                Assert.That(value.Work |> List.forall (fun row ->
                    row.SubjectTitle.IsNone && not row.ClaimEnabled), Is.True)
        let alpha = (sources[0].Snapshot |> success)
        Assert.That(alpha.ProjectVersion, Is.EqualTo(2))
        Assert.That(alpha.Work |> List.map _.Category = ["Available"; "Blocked"], Is.True)
        let dep = (alpha.Work |> List.find (fun row -> row.WorkId = "blocked")).Dependencies.Head
        Assert.That(dep.WorkId, Is.EqualTo("ready"))
        Assert.That(dep.StageGeneration, Is.EqualTo(Some 1))
        Assert.That(dep.ClaimGeneration, Is.EqualTo(Some 0))
        Assert.That(dep.Satisfied, Is.False)
        Assert.That(dep.Reason, Does.Contain("graph v"))
        let rendered = allRows sources
        Assert.That(rendered |> List.map _.Project = ["Alpha"; "Alpha"; "Zulu"], Is.True)
        Assert.That((rendered |> List.find (fun r -> r.WorkId = "ready")).Status,
                    Is.EqualTo("NotReady (queue available)"))
        Assert.That((rendered |> List.find (fun r -> r.WorkId = "blocked")).Status,
                    Is.EqualTo("Blocked"))
        Assert.That(rendered |> List.forall (fun r ->
            r.Details |> List.exists (fun line -> line.Contains("not owner"))), Is.True))

[<Test>]
let healthyQueueWithoutPinnedWq04RoleProofIsLocalUnknown () =
    withProjectsWithRoles true false (fun a b ->
        publish a 0 "approved" []
        publish b 0 "role-unproved" []
        // WQ02/WQ09 can prove the queue while WQ04 is independently unavailable.
        let unproved = WorkQueueToolFacade.readWithBinding b.Binding "List" "All" "" |> success
        Assert.That(unproved.Coverage, Is.EqualTo("Verified"))
        Assert.That(unproved.RoleAuthority, Does.StartWith("UNKNOWN"))
        let sources = load (authority a b) |> success
        Assert.That(sources.Length, Is.EqualTo(2))
        Assert.That(sources[0].Snapshot.IsOk, Is.True)
        let alphaHead = (LedgerStore.fetch a.Binding |> success).Commit
        Assert.That((sources[0].Snapshot |> success).VerifiedGitHead, Is.EqualTo(alphaHead))
        match sources[1].Snapshot with
        | Ok _ -> Assert.Fail("Verified queue coverage alone cannot prove WQ04 role authority.")
        | Error reason ->
            Assert.That(reason, Does.Contain("UNKNOWN"))
            Assert.That(reason, Does.Contain("WQ04 role authority"))
        let rows = allRows sources
        Assert.That(rows |> List.exists (fun row ->
            row.Project = "Alpha" && row.WorkId = "approved"), Is.True)
        let unknownRows = rows |> List.filter (fun row -> row.Project = "Zulu")
        Assert.That(unknownRows.Length, Is.EqualTo(1))
        Assert.That(unknownRows.Head.WorkId, Is.EqualTo("(unbound)"))
        Assert.That(unknownRows.Head.Status, Does.Contain("NotReady"))
        Assert.That(unknownRows.Head.Details |> List.exists (fun detail ->
            detail.Contains("UNKNOWN")), Is.True))

[<Test>]
let offlineProjectRetainsPerProjectErrorAndOtherApprovedProjectRemainsVisible () =
    withProjects (fun a b ->
        publish a 0 "available" []
        publish b 0 "also-available" []
        let approved = authority a b
        Directory.Move(b.Remote, b.Remote + ".offline")
        let sources = load approved |> success
        Assert.That(sources.Length, Is.EqualTo(2))
        let aSource = sources |> List.find (fun source -> source.ProjectName = "Alpha")
        let bSource = sources |> List.find (fun source -> source.ProjectName = "Zulu")
        Assert.That(aSource.Snapshot.IsOk, Is.True)
        match bSource.Snapshot with
        | Ok _ -> Assert.Fail("Missing approved Git remote must not use last-good data.")
        | Error reason -> Assert.That(reason, Does.Contain("UNKNOWN"))
        let rendered = allRows sources
        Assert.That(rendered |> List.exists (fun r ->
            r.Project = "Zulu" && r.Status.Contains("NotReady")), Is.True)
        Assert.That(rendered |> List.exists (fun r ->
            r.Project = "Alpha" && r.WorkId = "available"), Is.True))

[<Test>]
let omittedApprovedBindingIsExplicitUnknownWithoutErasingOtherProjects () =
    withProjects (fun a b ->
        publish a 0 "good" []
        let approved = { authority a b with Bindings = [ a.Binding ] }
        let sources = load approved |> success
        Assert.That(sources.Length, Is.EqualTo(2))
        Assert.That(sources[0].Snapshot.IsOk, Is.True)
        match sources[1].Snapshot with
        | Ok _ -> Assert.Fail("Omitted approved project binding must be unavailable.")
        | Error reason -> Assert.That(reason, Does.Contain("no verified Git binding"))
        let rendered = allRows sources
        Assert.That(rendered |> List.exists (fun x -> x.WorkId = "good"), Is.True)
        Assert.That(rendered |> List.exists (fun x ->
            x.Project = "Zulu" && x.Status.Contains("UNKNOWN")), Is.True))

[<Test>]
let incompleteAccountMembershipKeepsApprovedFactsButSurfacesGlobalUnknown () =
    withProjects (fun a b ->
        publish a 0 "observed" []
        let partial = { authority a b with MembershipComplete = false }
        let sources = load partial |> success
        Assert.That(sources.Length, Is.EqualTo(3))
        let errors = sources |> List.filter (fun p -> p.Snapshot.IsError)
        Assert.That(errors.Length, Is.EqualTo(1))
        Assert.That(errors.Head.ProjectName, Does.Contain("Account membership UNKNOWN"))
        Assert.That(allRows sources |> List.exists (fun r ->
            r.Project.Contains("Account membership UNKNOWN")
            && r.Status.Contains("NotReady")), Is.True))

[<Test>]
let duplicateOrContradictoryProjectAuthoritiesAreRejectedBeforeReading () =
    withProjects (fun a b ->
        let good = authority a b
        let invalid =
            [ { good with Projects = [ identity "Alpha" a; identity "alpha" b ] }
              { good with Projects = [ identity "Alpha" a; identity "Also Alpha" a ] }
              { good with Bindings = [ a.Binding; a.Binding ] }
              { good with Bindings = [ a.Binding; { b.Binding with LedgerId = a.Binding.LedgerId } ] }
              { good with Bindings = [ a.Binding; { b.Binding with ProjectId = Guid.NewGuid() } ] }
              { good with Bindings = [ a.Binding; { b.Binding with RepositoryPath = a.Binding.RepositoryPath } ] }
              { good with Bindings = [ a.Binding; { b.Binding with ApprovedFetchUrl = a.Binding.ApprovedFetchUrl; ApprovedPushUrl = a.Binding.ApprovedPushUrl } ] } ]
        for bad in invalid do
            match load bad with
            | Ok _ -> Assert.Fail("Duplicate or inconsistent trust registry accepted.")
            | Error reason -> Assert.That(reason, Does.Contain("UNKNOWN")))

[<Test>]
let missingHostAuthorityPortCannotBindAnyProductionWork () =
    let missing = createAccess (fun () -> Error "operator project registry absent")
    match missing.Load() with
    | Ok _ -> Assert.Fail("No operator approval must not produce a Work source.")
    | Error reason -> Assert.That(reason, Does.Contain("UNKNOWN"))
    let thrown = createAccess (fun () -> failwith "unavailable")
    match thrown.Load() with
    | Ok _ -> Assert.Fail("Failed registry must not become an empty success.")
    | Error reason -> Assert.That(reason, Does.Contain("unavailable"))
