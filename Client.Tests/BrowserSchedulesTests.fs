module BrowserSchedulesTests

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open Client
open NUnit.Framework

let private temp () =
    let dir = Path.Combine(Path.GetTempPath(), "jarvis-schedules-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    dir, Path.Combine(dir, "schedules.json")

let private add (service: BrowserSchedules.Service) id seconds =
    service.Schedule(id, seconds, "https://example.test/chat", "Scripts", "start.js",
        "agent-next", """{"token":"private-args"}""", false)

let private status (service: BrowserSchedules.Service) id =
    use doc = JsonDocument.Parse(service.List())
    doc.RootElement.EnumerateArray()
    |> Seq.find (fun x -> x.GetProperty("id").GetString() = id)
    |> fun x -> x.GetProperty("status").GetString()

let private awaitStatus test = task {
    let mutable found = false
    for _ in 1..120 do
        if test() then found <- true
        elif not found then do! Task.Delay(25)
    Assert.That(found, Is.True, "Expected schedule state was not reached")
}

[<Test>]
let durableOneShot () = task {
    let dir,path = temp()
    let mutable clock = DateTimeOffset.UtcNow
    let mutable called = 0
    let action (a:BrowserSchedules.ScheduledAction) = task {
        called <- called + 1
        Assert.That(a.ProjectName, Is.EqualTo("Scripts"))
        return Ok "new-tab-registration"
    }
    try
        do
            use first = new BrowserSchedules.Service(path, action, now=(fun () -> clock), pollMs=25)
            Assert.That((add first "orchestrator" 600).IsOk, Is.True)
            Assert.That(status first "orchestrator", Is.EqualTo("pending"))
            if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
                let mode = File.GetUnixFileMode(path)
                Assert.That(mode.HasFlag(UnixFileMode.GroupRead), Is.False)
                Assert.That(mode.HasFlag(UnixFileMode.OtherRead), Is.False)
        clock <- clock.AddSeconds(601.)
        use restored = new BrowserSchedules.Service(path, action, now=(fun () -> clock), pollMs=25)
        restored.Start()
        do! awaitStatus (fun () -> status restored "orchestrator" = "completed")
        do! Task.Delay(100)
        Assert.That(called, Is.EqualTo(1))
        Assert.That(restored.List(), Does.Contain("new-tab-registration"))
        Assert.That(restored.List(), Does.Not.Contain("private-args"))
    finally
        Directory.Delete(dir, true)
}

[<Test>]
let replaceAndCancel () = task {
    let dir,path = temp()
    let mutable clock = DateTimeOffset.UtcNow
    let mutable called = 0
    let action (_:BrowserSchedules.ScheduledAction) = task { called<-called+1; return Ok "id" }
    try
        use store = new BrowserSchedules.Service(path, action, now=(fun () -> clock), pollMs=25)
        Assert.That((add store "orchestrator" 600).IsOk, Is.True)
        Assert.That((add store "orchestrator" 1200).IsOk, Is.True)
        use listed = JsonDocument.Parse(store.List())
        Assert.That(listed.RootElement.GetArrayLength(), Is.EqualTo(1))
        Assert.That(store.Cancel("orchestrator").IsOk, Is.True)
        store.Start()
        clock <- clock.AddDays(1.)
        do! Task.Delay(100)
        Assert.That(called, Is.Zero)
    finally
        Directory.Delete(dir, true)
}

[<Test>]
let restartDoesNotReplayClaimedAction () = task {
    let dir,path = temp()
    let mutable called = 0
    let action (_:BrowserSchedules.ScheduledAction) = task { called<-called+1; return Ok "id" }
    try
        do
            use first = new BrowserSchedules.Service(path, action)
            Assert.That((add first "orchestrator" 600).IsOk, Is.True)
        let node = JsonNode.Parse(File.ReadAllText(path))
        node["actions"][0]["Status"] <- JsonValue.Create("running")
        File.WriteAllText(path, node.ToJsonString())
        use restored = new BrowserSchedules.Service(path, action, pollMs=25)
        Assert.That(status restored "orchestrator", Is.EqualTo("outcomeUnknown"))
        restored.Start()
        do! Task.Delay(100)
        Assert.That(called, Is.Zero)
    finally
        Directory.Delete(dir, true)
}

[<Test>]
let inputValidation () =
    let dir,path = temp()
    let action (_:BrowserSchedules.ScheduledAction) = task { return Ok "id" }
    try
        use store = new BrowserSchedules.Service(path, action)
        let invalid delay url args =
            store.Schedule("orchestrator", delay, url, "Scripts", "start.js", "", args, false).IsError
        Assert.That(invalid 0 "https://example.test/" "{}", Is.True)
        Assert.That(invalid 600 "file:///etc/passwd" "{}", Is.True)
        Assert.That(invalid 600 "https://user:pass@example.test/" "{}", Is.True)
        Assert.That(invalid 600 "https://example.test/" "bad-json", Is.True)
        Assert.That(store.Schedule("../escape",600,"https://example.test/","Scripts","start.js","","{}",false).IsError, Is.True)
    finally
        Directory.Delete(dir, true)
