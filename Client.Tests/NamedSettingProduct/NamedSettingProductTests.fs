module NamedSettingProductTests

open System
open System.IO
open System.Text.Json
open Client
open Common
open NUnit.Framework

let private disposable (test: string -> unit) =
    let previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
    let root = Path.Combine(Path.GetTempPath(), "jarvis-w25-native-" + Guid.NewGuid().ToString("N"))
    let xdg = Path.Combine(root, "xdg")
    Directory.CreateDirectory(xdg) |> ignore
    try
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", xdg)
        Assert.That(ClientConfig.configPath().StartsWith(xdg, StringComparison.Ordinal), Is.True)
        test root
    finally
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous)
        try Directory.Delete(root, true) with _ -> ()

let private setup workspace arguments =
    let snapshot = ClientConfig.load() |> Result.defaultWith failwith
    let effective = ClientConfig.resolve snapshot.Stored arguments |> Result.defaultWith failwith
    ClientConfig.installRunningContext { effective with WorkspacePath = workspace } arguments
    new Runtime(workspace)

let private dispatch (rt: Runtime) setting value revision =
    Client.SignalR.Client.receiveCommand rt
        (UpdateNamedSettingCommand
            { Setting = setting; Value = value; ExpectedRevision = revision })
    |> fun result -> result.GetAwaiter().GetResult()

let private success rt setting value revision =
    match dispatch rt setting value revision with
    | Error issue -> failwithf "Named setting command failed: %A" issue
    | Ok serialized ->
        use json = JsonDocument.Parse(serialized)
        json.RootElement.Clone()

let private revision (json: JsonElement) = json.GetProperty("Revision").GetString()
let private read rt setting = success rt setting None None
let private saved () = ClientConfig.load() |> Result.defaultWith failwith

[<TestFixture; NonParallelizable>]
type NamedSettingProductFixture() =
    [<Test>]
    member _.readSetUnsetCas() =
        disposable (fun root ->
            let workspace = Path.Combine(root, "projects")
            Directory.CreateDirectory(workspace) |> ignore
            let rt = setup workspace []
            let initial = read rt MachineName
            let first = revision initial
            Assert.That(initial.GetProperty("Operation").GetString(), Is.EqualTo("read"))
            let renamed = success rt MachineName (Some(NamedClientSettingValue.Text "disposable-client")) (Some first)
            Assert.That(saved().Stored.MachineName, Is.EqualTo(Some "disposable-client"))
            Assert.That(saved().Revision, Is.EqualTo(revision renamed))
            Assert.That(renamed.GetProperty("AppliedNow").GetBoolean(), Is.False)
            Assert.That(renamed.GetProperty("Activation").GetString(), Is.EqualTo("next-device-registration"))
            match dispatch rt MachineName (Some(NamedClientSettingValue.Text "stale")) (Some first) with
            | Error _ -> ()
            | Ok _ -> Assert.Fail("CAS stale write was not rejected")
            Assert.That(saved().Revision, Is.EqualTo(revision renamed))
            let reset = success rt MachineName None (Some(revision renamed))
            Assert.That(reset.GetProperty("Operation").GetString(), Is.EqualTo("unset"))
            Assert.That(saved().Stored.MachineName, Is.EqualTo(None)))

    [<Test>]
    member _.workspaceRootMovesOnlyOnFreshRuntime() =
        disposable (fun root ->
            let a = Path.Combine(root, "projects A")
            let b = Path.Combine(root, "projects B with spaces")
            Directory.CreateDirectory(a) |> ignore
            let project = Path.Combine(b, "project-in-B")
            Directory.CreateDirectory(project) |> ignore
            File.WriteAllText(Path.Combine(project, ".jarvis.json"), "{}")
            let rt = setup a []
            let initial = read rt WorkspacePath
            let moved = success rt WorkspacePath (Some(NamedClientSettingValue.Text b)) (Some(revision initial))
            Assert.That(saved().Stored.WorkspacePath, Is.EqualTo(Some b))
            Assert.That(moved.GetProperty("EffectiveRuntimeValue").GetString(), Is.EqualTo(a))
            Assert.That(moved.GetProperty("RestartRequired").GetBoolean(), Is.True)
            Assert.That(moved.GetProperty("AppliedNow").GetBoolean(), Is.False)
            let next = ClientConfig.resolve (saved().Stored) [] |> Result.defaultWith failwith
            Assert.That(next.WorkspacePath, Is.EqualTo(b))
            let fresh = new Runtime(next.WorkspacePath)
            match ProjectBrowser.listProjects fresh with
            | Error issue -> Assert.Fail(sprintf "Fresh client project discovery failed: %A" issue)
            | Ok names ->
                Assert.That(names |> Seq.exists (fun (ProjectName name) -> name = "project-in-B"), Is.True))

    [<Test>]
    member _.overrideDoesNotClaimLiveApply() =
        disposable (fun root ->
            let a = Path.Combine(root, "runtime-A")
            let b = Path.Combine(root, "configured-B")
            Directory.CreateDirectory(a) |> ignore
            Directory.CreateDirectory(b) |> ignore
            let rt = setup a ["--path"; a; "--machine-name"; "active-cli"]
            let machine = read rt MachineName
            let renamed = success rt MachineName (Some(NamedClientSettingValue.Text "saved-name")) (Some(revision machine))
            Assert.That(saved().Stored.MachineName, Is.EqualTo(Some "saved-name"))
            Assert.That(renamed.GetProperty("EffectiveRuntimeValue").GetString(), Is.EqualTo("active-cli"))
            Assert.That(renamed.GetProperty("StartupOverride").GetBoolean(), Is.True)
            Assert.That(renamed.GetProperty("AppliedNow").GetBoolean(), Is.False)
            let moved = success rt WorkspacePath (Some(NamedClientSettingValue.Text b)) (Some(revision renamed))
            Assert.That(moved.GetProperty("EffectiveRuntimeValue").GetString(), Is.EqualTo(a))
            Assert.That(moved.GetProperty("StartupOverride").GetBoolean(), Is.True)
            Assert.That(moved.GetProperty("AppliedNow").GetBoolean(), Is.False))

    [<Test>]
    member _.eightTypedFieldsAndFailClosedPrivilege() =
        disposable (fun root ->
            let workspace = Path.Combine(root, "projects")
            Directory.CreateDirectory(workspace) |> ignore
            let rt = setup workspace []
            let edits =
                [ MachineName, NamedClientSettingValue.Text "fixture-name"
                  WorkspacePath, Text workspace
                  TrustLevel, NamedClientSettingValue.Text "none"
                  AllowedEnvironmentVariables, NamedClientSettingValue.TextList []
                  MaxImportBytes, NamedClientSettingValue.Integer 16384L
                  BrowserPort, NamedClientSettingValue.Integer 18457L
                  NativeHealthEnabled, NamedClientSettingValue.Boolean true
                  NativeHealthTimeoutSeconds, NamedClientSettingValue.Integer 601L ]
            for setting, value in edits do
                let before = read rt setting
                let after = success rt setting (Some value) (Some(revision before))
                Assert.That(after.GetProperty("Revision").GetString(), Is.EqualTo(saved().Revision))
                Assert.That(after.GetProperty("AppliedNow").GetBoolean(), Is.False)
                Assert.That(revision (read rt setting), Is.EqualTo(revision after))
            let before = revision (read rt TrustLevel)
            for setting, value in
                [ TrustLevel, NamedClientSettingValue.Text "full"
                  AllowedEnvironmentVariables, NamedClientSettingValue.TextList ["SECRET_ENV"] ] do
                match dispatch rt setting (Some value) (Some before) with
                | Error _ -> ()
                | Ok _ -> Assert.Fail(sprintf "Remote privilege expansion accepted: %A" setting)
                Assert.That(saved().Revision, Is.EqualTo(before))
            match dispatch rt TrustLevel None (Some before) with
            | Error _ -> ()
            | Ok _ -> Assert.Fail("Unset from none to default partial escalated remote trust")
            Assert.That(saved().Revision, Is.EqualTo(before))
            let now = read rt NativeHealthEnabled
            let result = success rt NativeHealthEnabled None (Some(revision now))
            Assert.That(result.GetProperty("Operation").GetString(), Is.EqualTo("unset"))
            Assert.That(saved().Stored.NativeHealthEnabled, Is.EqualTo(None)))
