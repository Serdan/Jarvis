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
