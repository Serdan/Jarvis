module AutomationCapacityOperatorTests

open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open Client.LedgerStore
open Client.ConsoleTui
open Client.ProjectCapacityOperator

let good = function Ok x -> x | Error e -> failwithf "%A" e
let git directory args =
    use p = new Process()
    p.StartInfo.FileName <- "git"
    p.StartInfo.WorkingDirectory <- directory
    p.StartInfo.RedirectStandardOutput <- true
    p.StartInfo.RedirectStandardError <- true
    for a in args do p.StartInfo.ArgumentList.Add a
    Assert.That(p.Start(), Is.True)
    let output = p.StandardOutput.ReadToEndAsync()
    let error = p.StandardError.ReadToEndAsync()
    Assert.That(p.WaitForExit(30000), Is.True)
    if p.ExitCode <> 0 then failwith error.Result
    output.Result.Trim()

let withClones work =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-capacity-operator-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    try
        let bare, first, second = Path.Combine(root,"remote.git"), Path.Combine(root,"first"), Path.Combine(root,"second")
        git root ["init"; "--bare"; "--initial-branch=main"; bare] |> ignore
        git root ["clone"; bare; first] |> ignore
        File.WriteAllText(Path.Combine(first,"source.txt"), "fixture")
        git first ["add"; "source.txt"] |> ignore
        git first ["-c";"user.name=Fixture";"-c";"user.email=fixture@example.test";"commit";"-m";"source"] |> ignore
        git first ["push";"-u";"origin";"HEAD"] |> ignore
        git root ["clone";bare;second] |> ignore
        let project, ledger = Guid.NewGuid(), Guid.NewGuid()
        let binding repo =
            { RepositoryPath = repo; RemoteName = "origin"; ApprovedFetchUrl = bare; ApprovedPushUrl = bare
              ProjectId = project; LedgerId = ledger; Epoch = 1 }
        let a,b = binding first,binding second
        LedgerStore.initialize a true |> good |> ignore
        for role in ["Worker";"Reviewer"] do
            let path = "records/roles/" + role + ".json"
            let body = System.Text.Json.JsonSerializer.Serialize
                         {| schemaVersion=1;roleId=role;purpose="fixture";instructions="fixture"
                            version=1;updatedBy="fixture";updatedSequence=1L |}
            let intent = { OperationId=Guid.NewGuid(); Actor="fixture"; Kind="Fixture.Role"; RequestJson="{}" }
            LedgerStore.transact a intent (fun _ -> Ok [{ Path=path; ExpectedBlobOid=None; Content=body }]) |> good |> ignore
        work a b
    finally
        Directory.Delete(root,true)

let resolve binding project =
    if project <> "Fixture" then Error "project denied"
    else LedgerStore.fetch binding |> Result.map (fun snapshot -> binding,snapshot) |> Result.mapError (sprintf "%A")

let openAt binding =
    (create (fun () -> Some "operator:fixture") (resolve binding)).Load "Fixture" |> good

let draft total roles : CapacityPolicyDraft = { Total=total; Roles=Map.ofList roles }
let off () = draft None []

[<Test>]
let independentDimensionsAndExplicitRemoval () =
    withClones (fun a b ->
        let initial = openAt a
        Assert.That(initial.Revision, Is.EqualTo(0))
        Assert.That(initial.Draft=off (), Is.True)
        Assert.That(initial.ProjectId, Is.EqualTo(a.ProjectId))
        Assert.That(initial.LedgerId, Is.EqualTo(a.LedgerId))
        let changes = [ draft (Some 3) []; draft None ["Worker",2]; draft (Some 5) ["Worker",2;"Reviewer",1]
                        draft (Some 2) ["Reviewer",3]; off () ]
        changes |> List.iteri (fun i next ->
            let loaded = openAt a
            Assert.That(loaded.Revision, Is.EqualTo(i))
            loaded.Save (Guid.NewGuid()) next |> good
            let observed = openAt b
            Assert.That(observed.Revision, Is.EqualTo(i+1))
            Assert.That(observed.Draft=next, Is.True)))

[<Test>]
let staleCompetingAndCanonicalRoleValidation () =
    withClones (fun a b ->
        let first,second = openAt a,openAt b
        let x = Task.Run(fun () -> first.Save (Guid.NewGuid()) (draft (Some 2) ["Worker",1]))
        let y = Task.Run(fun () -> second.Save (Guid.NewGuid()) (draft None ["Reviewer",2]))
        Task.WaitAll [|x :> Task;y :> Task|]
        let results = [x.Result;y.Result]
        Assert.That(results |> List.filter Result.isOk |> List.length, Is.EqualTo(1))
        Assert.That(results |> List.exists (function Error(CapacityStale _) -> true | _ -> false), Is.True)
        let next = openAt a
        Assert.That(next.Revision, Is.EqualTo(1))
        for role in ["Unknown";"worker";"../Worker"] do
            Assert.That(next.Save (Guid.NewGuid()) (draft None [role,1]) |> Result.isError, Is.True)
        Assert.That((openAt b).Revision, Is.EqualTo(1)))

[<Test>]
let authenticationBindingAndDeniedResolutionNeverWrite () =
    withClones (fun a b ->
        let mutable owner = None
        let mutable selected = a
        let mutable allowed = true
        let mutable resolutions = 0
        let access =
            create (fun () -> owner) (fun project ->
                resolutions <- resolutions + 1
                if allowed then resolve selected project else Error "denied")
        Assert.That(access.Load "Fixture" |> Result.isError, Is.True)
        Assert.That(resolutions, Is.Zero)
        owner <- Some "operator:fixture"
        Assert.That(access.Load "NotFixture" |> Result.isError, Is.True)
        let loaded = access.Load "Fixture" |> good
        let op = Guid.NewGuid()
        let next = draft (Some 1) []
        owner <- None
        Assert.That(loaded.Save op next |> Result.isError, Is.True)
        owner <- Some "operator:other"
        Assert.That(loaded.Save op next |> Result.isError, Is.True)
        owner <- Some "operator:fixture"
        allowed <- false
        Assert.That(loaded.Save op next |> Result.isError, Is.True)
        allowed <- true
        selected <- b // same project/ledger/epoch but a different approved binding
        Assert.That(loaded.Save op next |> Result.isError, Is.True)
        selected <- a
        Assert.That((openAt a).Revision, Is.Zero)
        loaded.Save op next |> good
        Assert.That((openAt b).Revision, Is.EqualTo(1)))

[<Test>]
let textParserRejectsAmbiguityAndRoundTrips () =
    for configuration in [off ();draft (Some 2) [];draft None ["Worker",3];draft (Some 7) ["Worker",3;"Reviewer",2]] do
        Assert.That(CapacityPolicyDraft.parse (CapacityPolicyDraft.format configuration) = Ok configuration, Is.True)
    for text in ["";"role Worker=1";"total=0";"total=-1";"total=2147483648";"total=off\ntotal=off"
                 "total=off\nrole Worker=0";"total=off\nrole Worker=2\nrole Worker=3"
                 "total=off\nrole ../Worker=2";"total=off\nfoo=2";"total=off=2"] do
        Assert.That(CapacityPolicyDraft.parse text |> Result.isError, Is.True, text)

let key k = ConsoleKeyInfo('\000',k,false,false,false)
let typeChar c = ConsoleKeyInfo(c,ConsoleKey.NoName,false,false,false)
let ctrlU () = ConsoleKeyInfo('\000',ConsoleKey.U,false,false,true)
let awaitEditor (tui: ConsoleTui) =
    Assert.That(SpinWait.SpinUntil((fun () -> let _,busy,_,_,_,_ = tui.InspectProjectEditorForFixture() in not busy), TimeSpan.FromSeconds(30.)), Is.True)

[<Test; NonParallelizable>]
let actualModalPreservesExactUnknownReceiptIntent () =
    withClones (fun a b ->
        let access = create (fun () -> Some "operator:fixture") (resolve a)
        let attempts = ResizeArray<Guid * CapacityPolicyDraft>()
        let tui = ConsoleTui()
        // Simulate the UI losing the first successful receipt after the real
        // disposable ledger write; retry must reconcile the original UUID.
        tui.SetCapacityPolicyAccess
            { Load = fun project ->
                access.Load project |> Result.map (fun snapshot ->
                    let save operation next =
                        attempts.Add(operation,next)
                        let result = snapshot.Save operation next
                        if attempts.Count=1 && Result.isOk result then Error(CapacityUncertain "receipt lost")
                        else result
                    { snapshot with Save = save }) }
        tui.OpenCapacityPolicyForFixture("Fixture")
        awaitEditor tui
        let _,_,_,body,_,_ = tui.InspectProjectEditorForFixture()
        Assert.That(body, Is.EqualTo("total=off"))
        tui.DispatchProjectEditorKeyForFixture(ctrlU ())
        for c in "total=2" do tui.DispatchProjectEditorKeyForFixture(typeChar c)
        tui.DispatchProjectEditorKeyForFixture(key ConsoleKey.Enter)
        for c in "role Worker=1" do tui.DispatchProjectEditorKeyForFixture(typeChar c)
        tui.DispatchProjectEditorKeyForFixture(key ConsoleKey.F2)
        awaitEditor tui
        let _,_,pending,original,_,_ = tui.InspectProjectEditorForFixture()
        Assert.That(pending.IsSome, Is.True)
        Assert.That((openAt b).Revision, Is.EqualTo(1))
        tui.DispatchProjectEditorKeyForFixture(ctrlU ())
        tui.DispatchProjectEditorKeyForFixture(typeChar 'x')
        tui.DispatchProjectEditorKeyForFixture(key ConsoleKey.Escape)
        tui.DispatchProjectEditorKeyForFixture(key ConsoleKey.F5)
        let mode,_,stillPending,unchanged,_,_ = tui.InspectProjectEditorForFixture()
        Assert.That(mode, Is.EqualTo("ProjectEditorMode"))
        Assert.That(stillPending=pending && unchanged=original, Is.True)
        tui.DispatchProjectEditorKeyForFixture(key ConsoleKey.F2)
        awaitEditor tui
        let _,_,finished,_,_,reload = tui.InspectProjectEditorForFixture()
        Assert.That(finished.IsNone && reload, Is.True)
        Assert.That(attempts.Count, Is.EqualTo(2))
        Assert.That(attempts[0]=attempts[1], Is.True)
        Assert.That((openAt b).Revision, Is.EqualTo(1))
        tui.DispatchProjectEditorKeyForFixture(key ConsoleKey.F5)
        awaitEditor tui
        let _,_,_,_,_,reload = tui.InspectProjectEditorForFixture()
        Assert.That(reload, Is.False))

[<Test; NonParallelizable>]
let staleModalRequiresRefreshBeforeSaving () =
    withClones (fun a b ->
        let tui = ConsoleTui()
        tui.SetCapacityPolicyAccess(create (fun () -> Some "operator:fixture") (resolve a))
        tui.OpenCapacityPolicyForFixture("Fixture")
        awaitEditor tui
        (openAt b).Save (Guid.NewGuid()) (draft (Some 3) ["Reviewer",1]) |> good
        tui.DispatchProjectEditorKeyForFixture(ctrlU ())
        for c in "total=2" do tui.DispatchProjectEditorKeyForFixture(typeChar c)
        tui.DispatchProjectEditorKeyForFixture(key ConsoleKey.F2)
        awaitEditor tui
        let _,_,pending,body,notice,reload = tui.InspectProjectEditorForFixture()
        Assert.That(pending.IsNone && reload, Is.True, notice)
        Assert.That(body, Is.EqualTo("total=2"))
        tui.DispatchProjectEditorKeyForFixture(key ConsoleKey.F2)
        Assert.That((openAt b).Revision, Is.EqualTo(1))
        tui.DispatchProjectEditorKeyForFixture(key ConsoleKey.F5)
        awaitEditor tui
        let _,_,_,body,_,reload = tui.InspectProjectEditorForFixture()
        Assert.That(reload, Is.False)
        Assert.That(body, Is.EqualTo("total=3\nrole Reviewer=1")))

[<Test; NonParallelizable>]
let sharedEditorPreservesLaunchTemplateBodySemantics () =
    let projectId,ledgerId = Guid.NewGuid(),Guid.NewGuid()
    let mutable body = "<old template>"
    let mutable version = 0
    let writes = ResizeArray<Guid * string>()
    let tui = ConsoleTui()
    tui.SetLaunchTemplateAccess
        { Load = fun project ->
            Ok { LaunchProjectName=project; ProjectId=projectId; LedgerId=ledgerId
                 Epoch=1; Version=version; Template=body }
          Save = fun snapshot operation next ->
            writes.Add(operation,next)
            if snapshot.Version <> version then Error(TemplateStale "stale")
            else
                version <- version+1
                body <- next
                Ok () }
    tui.OpenLaunchTemplateForFixture("Fixture")
    awaitEditor tui
    let _,_,_,loaded,_,_ = tui.InspectProjectEditorForFixture()
    Assert.That(loaded, Is.EqualTo("<old template>"))
    tui.DispatchProjectEditorKeyForFixture(ctrlU ())
    for c in "not a capacity draft" do tui.DispatchProjectEditorKeyForFixture(typeChar c)
    tui.DispatchProjectEditorKeyForFixture(key ConsoleKey.F2)
    awaitEditor tui
    Assert.That(writes.Count, Is.EqualTo(1))
    Assert.That(body, Is.EqualTo("not a capacity draft"))
    tui.DispatchProjectEditorKeyForFixture(key ConsoleKey.F5)
    awaitEditor tui
    let _,_,_,reloaded,_,needsReload = tui.InspectProjectEditorForFixture()
    Assert.That(needsReload, Is.False)
    Assert.That(reloaded, Is.EqualTo(body))


// Exercise the compiled Program registration and real approved-binding resolver,
// through the same selected Agents row/key path used by the terminal. Only the
// already-authenticated identity is a fixture input; no real account is used.
[<Test; NonParallelizable>]
let productionRegistrationUsesSelectedProjectAndApprovedBinding () =
    withClones (fun a b ->
        let root = Path.GetDirectoryName(a.RepositoryPath)
        let prior = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(root,"config"))
        try
            let row project : Client.AgentView.AgentRow =
                { Key = project + "::Worker"; Agent = "Worker"; Project = project
                  ProjectLabel = project; ProjectAliases = [project]; State = "Ready"
                  StateEnteredAt = None; StateReason = None; Focus = None; RoleIds = ["Worker"]
                  Activity = None; ReviewContext = None; PendingNotifications = 0
                  LatestNotification = None; ActivityKeys = [] }
            let snapshot project : Client.AgentView.Snapshot =
                { Agents = [row project]; Projects = []; Issues = []; RefreshedAt = DateTimeOffset.UtcNow }
            let project = Path.GetFileName(a.RepositoryPath)
            let tui = ConsoleTui()
            let rt = Client.Runtime(root, tui, Client.FullTrust)
            let mutable actor = Some "operator:fixture"
            Program.registerCapacityPolicyAccess tui rt None (fun () -> actor)
            tui.DispatchCapacityPolicyKeyForFixture(snapshot project, key ConsoleKey.K)
            let mode,_,_,_,_,_ = tui.InspectProjectEditorForFixture()
            Assert.That(mode, Is.EqualTo("AgentsMode"))
            let head = (LedgerStore.fetch a |> good).Commit
            Client.OrchestrationApi.LedgerBindingConfig.bindExisting project rt a.ProjectId a.LedgerId a.Epoch
                a.RemoteName a.ApprovedFetchUrl a.ApprovedPushUrl head true |> good |> ignore
            Program.registerCapacityPolicyAccess tui rt actor (fun () -> actor)
            tui.DispatchCapacityPolicyKeyForFixture(snapshot "unbound", key ConsoleKey.K)
            awaitEditor tui
            let _,_,_,_,_,denied = tui.InspectProjectEditorForFixture()
            Assert.That(denied, Is.True)
            Assert.That((openAt b).Revision, Is.Zero)
            tui.DispatchCapacityPolicyKeyForFixture(snapshot project, key ConsoleKey.K)
            awaitEditor tui
            let _,_,_,initial,_,needsReload = tui.InspectProjectEditorForFixture()
            Assert.That(needsReload, Is.False)
            Assert.That(initial, Is.EqualTo("total=off"))
            tui.DispatchProjectEditorKeyForFixture(ctrlU ())
            for c in "total=4" do tui.DispatchProjectEditorKeyForFixture(typeChar c)
            tui.DispatchProjectEditorKeyForFixture(key ConsoleKey.Enter)
            for c in "role Worker=2" do tui.DispatchProjectEditorKeyForFixture(typeChar c)
            actor <- Some "operator:different"
            tui.DispatchProjectEditorKeyForFixture(key ConsoleKey.F2)
            awaitEditor tui
            let _,_,pending,_,_,_ = tui.InspectProjectEditorForFixture()
            Assert.That(pending.IsSome, Is.True)
            Assert.That((openAt b).Revision, Is.Zero)
            actor <- Some "operator:fixture"
            tui.DispatchProjectEditorKeyForFixture(key ConsoleKey.F2)
            awaitEditor tui
            let _,_,finished,_,_,refresh = tui.InspectProjectEditorForFixture()
            Assert.That(finished.IsNone && refresh, Is.True)
            let saved = openAt b
            Assert.That(saved.Revision, Is.EqualTo(1))
            Assert.That(saved.Draft = draft (Some 4) ["Worker",2], Is.True)
            tui.DispatchProjectEditorKeyForFixture(key ConsoleKey.F5)
            awaitEditor tui
            let _,_,_,body,_,reload = tui.InspectProjectEditorForFixture()
            Assert.That(reload, Is.False)
            Assert.That(body, Is.EqualTo("total=4\nrole Worker=2"))
        finally
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME",prior))
