module BoundedProcessTests

open System
open System.Diagnostics
open System.IO
open System.Threading
open Client
open Client.ClientShell
open Client.IO
open Common
open NUnit.Framework

let private newProject () =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-process-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(Path.Combine(root, "Project1")) |> ignore
    root

type private Context(root: string) =
    interface ProjectIO with
        member _.Project = { Root = ProjectDirectory root; SpecialFiles = []; FolderFilters = [] }
    interface FileIO with
        member _.File = FileOperations.impl

let private command file seconds limit =
    { ProjectName = "Project1"
      Executable = "dotnet"
      Args = [ "fsi"; "--exec"; file ]
      Reason = None
      WorkingDirectory = None
      TimeoutSeconds = Some seconds
      MaxOutputBytes = Some limit }

let private isAlive pid =
    try
        use proc = Process.GetProcessById pid
        if proc.HasExited then false
        elif OperatingSystem.IsLinux() then
            let status = File.ReadAllText($"/proc/{pid}/stat")
            let close = status.LastIndexOf(')')
            let state = status[close + 2]
            state <> 'Z' && state <> 'X'
        else true
    with
    | :? ArgumentException
    | :? InvalidOperationException
    | :? FileNotFoundException -> false

let private stopChild pid =
    try
        use proc = Process.GetProcessById pid
        if not proc.HasExited then proc.Kill(true)
    with _ -> ()

[<Test>]
let pipe_holding_descendant_is_deadline_bounded_and_reaped () =
    let root = newProject ()
    let project = Path.Combine(root, "Project1")
    let childFile = Path.Combine(project, "child.fsx")
    let parentFile = Path.Combine(project, "parent.fsx")
    let marker = Path.Combine(project, "child-pid.txt")
    try
        let childScript =
            $"""open System
open System.IO
open System.Threading
File.WriteAllText(@"{marker}", string Environment.ProcessId)
printfn "descendant stdout"
eprintfn "descendant stderr"
Thread.Sleep 30000"""
        let parentScript =
            $"""open System
open System.Diagnostics
let startInfo = ProcessStartInfo("dotnet")
startInfo.ArgumentList.Add("fsi")
startInfo.ArgumentList.Add("--exec")
startInfo.ArgumentList.Add(@"{childFile}")
startInfo.UseShellExecute <- false
startInfo.RedirectStandardOutput <- false
startInfo.RedirectStandardError <- false
use child = Process.Start(startInfo)
printfn "parent exited"
"""
        File.WriteAllText(childFile, childScript)
        File.WriteAllText(parentFile, parentScript)
        let watch = Stopwatch.StartNew()
        let result = runCommand (command "parent.fsx" 6 4096) (Context root)
        watch.Stop()
        match result with
        | Error failure -> Assert.Fail($"Expected bounded timeout result, got {failure}")
        | Ok output ->
            Assert.That(output.TimedOut, Is.True,
                        "An immediate parent exit must not end the deadline while inherited pipes remain.")
            Assert.That(output.ExitCode, Is.EqualTo(-1))
            Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds 9.))
            Assert.That(output.StdOut, Does.Contain("parent exited"))
            Assert.That(File.Exists marker, Is.True, "Descendant must actually have started.")
            let pid = int (File.ReadAllText marker)
            try
                let deadline = DateTime.UtcNow.AddSeconds(2.)
                while isAlive pid && DateTime.UtcNow < deadline do Thread.Sleep 50
                Assert.That(isAlive pid, Is.False,
                            "The timeout must terminate descendants after the parent exited.")
            finally stopChild pid
    finally
        if File.Exists marker then
            match Int32.TryParse(File.ReadAllText marker) with
            | true, pid -> stopChild pid
            | _ -> ()
        if Directory.Exists(root) then Directory.Delete(root, true)

[<Test>]
let direct_hung_command_returns_typed_timeout () =
    let root = newProject ()
    try
        let project = Path.Combine(root, "Project1")
        File.WriteAllText(Path.Combine(project, "slow.fsx"),
                          "open System.Threading\nprintfn \"began\"\nThread.Sleep 15000\n")
        let watch = Stopwatch.StartNew()
        match runCommand (command "slow.fsx" 3 1024) (Context root) with
        | Ok output ->
            watch.Stop()
            Assert.That(output.TimedOut, Is.True)
            Assert.That(output.ExitCode, Is.EqualTo(-1))
            Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds 6.))
        | Error error -> Assert.Fail($"Expected timeout, got {error}")
    finally
        if Directory.Exists(root) then Directory.Delete(root, true)

[<Test>]
let bounded_reader_drains_large_stdout_and_stderr () =
    let root = newProject ()
    try
        let project = Path.Combine(root, "Project1")
        File.WriteAllText(Path.Combine(project, "large.fsx"),
                          "for _ in 1 .. 15000 do\n    printfn \"æææææææææææ\"\n    eprintfn \"errrrr\"\n")
        match runCommand (command "large.fsx" 20 31) (Context root) with
        | Ok output ->
            Assert.That(output.TimedOut, Is.False)
            Assert.That(output.ExitCode, Is.EqualTo(0))
            Assert.That(output.Truncated, Is.True)
            Assert.That(Text.Encoding.UTF8.GetByteCount(output.StdOut), Is.LessThanOrEqualTo(31))
            Assert.That(Text.Encoding.UTF8.GetByteCount(output.StdErr), Is.LessThanOrEqualTo(31))
        | Error error -> Assert.Fail($"Expected success, got {error}")
    finally
        if Directory.Exists(root) then Directory.Delete(root, true)

[<Test>]
let explicit_cancellation_terminates_bounded_process () =
    let root = newProject ()
    try
        let project = Path.Combine(root, "Project1")
        File.WriteAllText(Path.Combine(project, "cancel.fsx"),
                          "open System.Threading\nprintfn \"running\"\nThread.Sleep 30000\n")
        use cancellation = new CancellationTokenSource()
        let command: BoundedProcess.Command =
            { Executable = "dotnet"
              Args = [ "fsi"; "--exec"; "cancel.fsx" ]
              WorkingDirectory = project
              Timeout = TimeSpan.FromSeconds 20.
              MaxOutputBytes = Some 1024
              StandardInput = BoundedProcess.InheritInput
              ExtraEnvironment = []
              ConfigureStart = None }
        let watch = Stopwatch.StartNew()
        let activity =
            System.Threading.Tasks.Task.Run(fun () ->
                BoundedProcess.runWithCancellation cancellation.Token command)
        Thread.Sleep 500
        cancellation.Cancel()
        let result = activity.GetAwaiter().GetResult()
        watch.Stop()
        match result with
        | Error(Client.ContextError message) ->
            Assert.That(message, Does.Contain("canceled"))
            Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds 4.))
        | other -> Assert.Fail($"Expected explicit cancellation result, got {other}")
    finally
        if Directory.Exists(root) then Directory.Delete(root, true)

[<Test>]
let timeout_interrupts_blocked_standard_input_write () =
    let root = newProject ()
    try
        let project = Path.Combine(root, "Project1")
        File.WriteAllText(Path.Combine(project, "ignore-input.fsx"),
                          "open System.Threading\nThread.Sleep 30000\n")
        let command: BoundedProcess.Command =
            { Executable = "dotnet"
              Args = [ "fsi"; "--exec"; "ignore-input.fsx" ]
              WorkingDirectory = project
              Timeout = TimeSpan.FromSeconds 3.
              MaxOutputBytes = Some 1024
              StandardInput = BoundedProcess.TextInput(String.replicate 200000 "x")
              ExtraEnvironment = []
              ConfigureStart = None }
        let watch = Stopwatch.StartNew()
        match BoundedProcess.run command with
        | Ok output ->
            watch.Stop()
            Assert.That(output.TimedOut, Is.True)
            Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds 6.))
        | Error error -> Assert.Fail($"Expected stdin backpressure to time out, got {error}")
    finally
        if Directory.Exists(root) then Directory.Delete(root, true)

[<Test>]
let successful_command_cleans_up_detached_descendants () =
    let root = newProject ()
    let project = Path.Combine(root, "Project1")
    let markerPath = Path.Combine(project, "detached-pid.txt")
    let childPath = Path.Combine(project, "detached-child.fsx")
    let parentPath = Path.Combine(project, "detached-parent.fsx")
    try
        let childScript = $"""open System
open System.IO
open System.Threading
File.WriteAllText(@"{markerPath}", string Environment.ProcessId)
Thread.Sleep 30000"""
        let parentScript = $"""open System
open System.Diagnostics
open System.IO
open System.Threading
let info = ProcessStartInfo("dotnet")
info.ArgumentList.Add("fsi")
info.ArgumentList.Add("--exec")
info.ArgumentList.Add(@"{childPath}")
info.UseShellExecute <- false
// Capture the child's output separately so the outer command sees EOF
// when this parent exits, even though the descendant is still alive.
info.RedirectStandardOutput <- true
info.RedirectStandardError <- true
use child = Process.Start(info)
let deadline = DateTime.UtcNow.AddSeconds(15.)
while not (File.Exists(@"{markerPath}")) && DateTime.UtcNow < deadline do
    Thread.Sleep 20
if not (File.Exists(@"{markerPath}")) then failwith "Child did not start."
printfn "parent completed"
"""
        File.WriteAllText(childPath, childScript)
        File.WriteAllText(parentPath, parentScript)
        match runCommand (command "detached-parent.fsx" 20 4096) (Context root) with
        | Error error -> Assert.Fail($"Expected successful cleanup, got {error}")
        | Ok output ->
            Assert.That(output.TimedOut, Is.False)
            Assert.That(output.ExitCode, Is.EqualTo(0))
            Assert.That(File.Exists markerPath, Is.True)
            let pid = int (File.ReadAllText markerPath)
            try
                let deadline = DateTime.UtcNow.AddSeconds(2.)
                while isAlive pid && DateTime.UtcNow < deadline do Thread.Sleep 50
                Assert.That(isAlive pid, Is.False,
                            "A bounded command must not leave descendants alive after success.")
            finally stopChild pid
    finally
        if File.Exists markerPath then
            match Int32.TryParse(File.ReadAllText markerPath) with
            | true, pid -> stopChild pid
            | _ -> ()
        if Directory.Exists root then Directory.Delete(root, true)

[<Test>]
let managed_dotnet_host_and_apphost_choose_the_correct_helper_arguments () =
    let payload = [ "--jarvis-internal-process-group"; "test-event"; "git"; "status" ]
    let dotnetHost, dotnetArgs =
        BoundedProcess.helperHostInvocation "/opt/dotnet" "/apps/JarvisClient.dll" payload
    Assert.That(dotnetHost, Is.EqualTo("/opt/dotnet"))
    Assert.That((dotnetArgs = "/apps/JarvisClient.dll" :: payload), Is.True)

    let apphost, apphostArgs =
        BoundedProcess.helperHostInvocation "/apps/JarvisClient.exe" "/apps/JarvisClient.dll" payload
    Assert.That(apphost, Is.EqualTo("/apps/JarvisClient.exe"))
    Assert.That((apphostArgs = payload), Is.True)

    let singleFile, singleFileArgs =
        BoundedProcess.helperHostInvocation "/apps/JarvisClient" "" payload
    Assert.That(singleFile, Is.EqualTo("/apps/JarvisClient"))
    Assert.That((singleFileArgs = payload), Is.True)

[<Test>]
let closed_input_provides_eof_to_the_child () =
    let root = newProject ()
    try
        let project = Path.Combine(root, "Project1")
        File.WriteAllText(Path.Combine(project, "read-stdin.fsx"),
                          "let input = System.Console.In.ReadToEnd()\nprintfn \"input-length:%d\" input.Length\n")
        let command: BoundedProcess.Command =
            { Executable = "dotnet"
              Args = [ "fsi"; "--exec"; "read-stdin.fsx" ]
              WorkingDirectory = project
              Timeout = TimeSpan.FromSeconds 15.
              MaxOutputBytes = Some 4096
              StandardInput = BoundedProcess.ClosedInput
              ExtraEnvironment = []
              ConfigureStart = None }
        match BoundedProcess.run command with
        | Ok result ->
            Assert.That(result.TimedOut, Is.False)
            Assert.That(result.ExitCode, Is.EqualTo(0))
            Assert.That(result.StdOut, Does.Contain("input-length:0"))
        | Error error -> Assert.Fail($"Expected stdin EOF, got {error}")
    finally
        if Directory.Exists(root) then Directory.Delete(root, true)

[<Test>]
let supplied_text_reaches_child_stdin_unmodified () =
    let root = newProject ()
    try
        let project = Path.Combine(root, "Project1")
        File.WriteAllText(Path.Combine(project, "echo-stdin.fsx"),
                          "let input = System.Console.In.ReadToEnd()\nprintfn \"received:%s\" input\n")
        let command: BoundedProcess.Command =
            { Executable = "dotnet"
              Args = [ "fsi"; "--exec"; "echo-stdin.fsx" ]
              WorkingDirectory = project
              Timeout = TimeSpan.FromSeconds 15.
              MaxOutputBytes = Some 4096
              StandardInput = BoundedProcess.TextInput "hello ø world"
              ExtraEnvironment = []
              ConfigureStart = None }
        match BoundedProcess.run command with
        | Ok result ->
            Assert.That(result.TimedOut, Is.False)
            Assert.That(result.ExitCode, Is.EqualTo(0))
            Assert.That(result.StdOut, Does.Contain("received:hello ø world"))
        | Error error -> Assert.Fail($"Expected supplied UTF-8 stdin, got {error}")
    finally
        if Directory.Exists(root) then Directory.Delete(root, true)

[<Test>]
let nonzero_exit_is_distinct_from_timeout () =
    let root = newProject ()
    try
        let project = Path.Combine(root, "Project1")
        File.WriteAllText(Path.Combine(project, "fail.fsx"), "eprintfn \"deliberate boom\"\nexit 23\n")
        match runCommand (command "fail.fsx" 15 4096) (Context root) with
        | Ok output ->
            Assert.That(output.TimedOut, Is.False)
            Assert.That(output.ExitCode, Is.Not.EqualTo(0))
            Assert.That(output.StdErr, Does.Contain("boom"))
        | Error error -> Assert.Fail($"Expected nonzero exit, got {error}")
    finally
        if Directory.Exists(root) then Directory.Delete(root, true)
