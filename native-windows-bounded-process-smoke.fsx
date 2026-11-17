#I "Client/bin/Release/net11.0"
#r "Common.dll"
#r "JarvisClient.dll"
open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open Client

let root = Directory.CreateTempSubdirectory("jarvis-worker23-native-").FullName
let failures = ResizeArray<string>()
let assertThat condition detail = if not condition then failwith detail
let cmd executable args input (seconds: float) cap : BoundedProcess.Command =
    { Executable = executable
      Args = args
      WorkingDirectory = root
      Timeout = TimeSpan.FromSeconds seconds
      MaxOutputBytes = Some cap
      StandardInput = input
      ExtraEnvironment = []
      ConfigureStart = None }
let unwrap = function
    | Ok result -> result
    | Error error -> failwithf "Unexpected process error: %A" error
let alive pid =
    try
        use process = Process.GetProcessById(pid)
        not process.HasExited
    with
    | :? ArgumentException
    | :? InvalidOperationException -> false
let stop pid =
    try
        use process = Process.GetProcessById(pid)
        if not process.HasExited then process.Kill(true)
    with _ -> ()
let waitUntil seconds predicate =
    let watch = Stopwatch.StartNew()
    while not (predicate()) && watch.Elapsed.TotalSeconds < seconds do Thread.Sleep 50
    predicate()
let readPid path = Int32.Parse(File.ReadAllText path)
let runCase (name: string) action =
    if fsi.CommandLineArgs.Length < 2 || name.Contains(fsi.CommandLineArgs[1]) then
        let watch = Stopwatch.StartNew()
        try
            action()
            printfn "PASS %s elapsed_ms=%d" name watch.ElapsedMilliseconds
        with e ->
            printfn "FAIL %s elapsed_ms=%d error=%s" name watch.ElapsedMilliseconds e.Message
            failures.Add(name + ": " + e.Message)
try
    runCase "windows_named_event_job_and_both_streams" (fun () ->
        let result = unwrap (BoundedProcess.run (cmd "cmd.exe" ["/d"; "/c"; "echo W23_OUT & echo W23_ERR 1>&2"] BoundedProcess.ClosedInput 15. 4096))
        assertThat (not result.TimedOut && result.ExitCode = 0) (sprintf "Outcome %A" result)
        assertThat (result.StdOut.Contains "W23_OUT" && result.StdErr.Contains "W23_ERR") (sprintf "Pipes %A" result))

    runCase "closed_input_eof" (fun () ->
        let path = Path.Combine(root, "closed.fsx")
        File.WriteAllText(path, "let data = System.Console.In.ReadToEnd()\nprintfn \"LEN=%d\" data.Length\n")
        let result = unwrap (BoundedProcess.run (cmd "dotnet" ["fsi"; "--exec"; path] BoundedProcess.ClosedInput 15. 4096))
        assertThat (not result.TimedOut && result.ExitCode = 0 && result.StdOut.Contains "LEN=0") (sprintf "Outcome %A" result))

    runCase "text_input_exact_utf8" (fun () ->
        let path = Path.Combine(root, "text.fsx")
        File.WriteAllText(path, "let data = System.Console.In.ReadToEnd()\nprintfn \"GOT:%s\" data\n")
        let result = unwrap (BoundedProcess.run (cmd "dotnet" ["fsi"; "--exec"; path] (BoundedProcess.TextInput "hello ø world") 15. 4096))
        assertThat (not result.TimedOut && result.ExitCode = 0 && result.StdOut.Contains "GOT:hello ø world") (sprintf "Outcome %A" result))

    runCase "text_input_raw_utf8_bytes" (fun () ->
        let path = Path.Combine(root, "text-bytes.fsx")
        File.WriteAllText(path, "use stream = new System.IO.MemoryStream()\nSystem.Console.OpenStandardInput().CopyTo(stream)\nprintfn \"INPUT_BASE64:%s\" (System.Convert.ToBase64String(stream.ToArray()))\n")
        let payload = "hello ø world"
        let expected = Convert.ToBase64String(Text.Encoding.UTF8.GetBytes(payload))
        let result = unwrap (BoundedProcess.run (cmd "dotnet" ["fsi"; "--exec"; path] (BoundedProcess.TextInput payload) 15. 4096))
        assertThat (not result.TimedOut && result.ExitCode = 0 && result.StdOut.Contains("INPUT_BASE64:" + expected)) (sprintf "Raw stdin byte mismatch; expected=%s outcome=%A" expected result))

    runCase "inherit_input_nonreading_child" (fun () ->
        let result = unwrap (BoundedProcess.run (cmd "cmd.exe" ["/d"; "/c"; "echo W23_INHERIT"] BoundedProcess.InheritInput 15. 4096))
        assertThat (not result.TimedOut && result.ExitCode = 0 && result.StdOut.Contains "W23_INHERIT") (sprintf "Outcome %A" result))

    runCase "nonzero_exit_not_timeout" (fun () ->
        let result = unwrap (BoundedProcess.run (cmd "cmd.exe" ["/d"; "/c"; "exit /b 23"] BoundedProcess.ClosedInput 15. 4096))
        assertThat (not result.TimedOut && result.ExitCode = 23) (sprintf "Outcome %A" result))

    runCase "direct_deadline_bounded" (fun () ->
        let path = Path.Combine(root, "sleep.fsx")
        File.WriteAllText(path, "System.Threading.Thread.Sleep 30000\n")
        let watch = Stopwatch.StartNew()
        let result = unwrap (BoundedProcess.run (cmd "dotnet" ["fsi"; "--exec"; path] BoundedProcess.ClosedInput 5. 4096))
        assertThat (result.TimedOut && result.ExitCode = -1 && watch.Elapsed.TotalSeconds < 9.) (sprintf "Outcome %A duration=%A" result watch.Elapsed))

    runCase "output_capped_without_deadlock" (fun () ->
        let path = Path.Combine(root, "large.fsx")
        File.WriteAllText(path, "for i in 1 .. 10000 do\n    printfn \"øøøøøøøøøø\"\n    eprintfn \"error-stream\"\n")
        let result = unwrap (BoundedProcess.run (cmd "dotnet" ["fsi"; "--exec"; path] BoundedProcess.ClosedInput 20. 31))
        assertThat (not result.TimedOut && result.ExitCode = 0 && result.StdOutTruncated && result.StdErrTruncated) (sprintf "Outcome %A" result)
        assertThat (Text.Encoding.UTF8.GetByteCount result.StdOut <= 31 && Text.Encoding.UTF8.GetByteCount result.StdErr <= 31) (sprintf "Output exceeded cap: %A" result))

    runCase "cancel_after_target_started" (fun () ->
        let marker = Path.Combine(root, "cancel.pid")
        let path = Path.Combine(root, "cancel.fsx")
        File.WriteAllText(path, sprintf "System.IO.File.WriteAllText(@\"%s\", string System.Environment.ProcessId)\nSystem.Threading.Thread.Sleep 30000\n" marker)
        let source = new CancellationTokenSource()
        let activity = Task.Run(fun () ->
            BoundedProcess.runWithCancellation source.Token (cmd "dotnet" ["fsi"; "--exec"; path] BoundedProcess.ClosedInput 25. 4096))
        try
            assertThat (waitUntil 10. (fun () -> File.Exists marker)) "Cancellation target never started"
            let pid = readPid marker
            source.Cancel()
            match activity.GetAwaiter().GetResult() with
            | Error(Client.ContextError message) ->
                assertThat (message.Contains "canceled") message
            | other -> failwithf "Expected cancellation error, got %A" other
            assertThat (waitUntil 3. (fun () -> not (alive pid))) (sprintf "Cancelled child survived: %d" pid)
        finally
            source.Cancel()
            if File.Exists marker then stop (readPid marker)
            source.Dispose())

    runCase "text_input_backpressure_times_out" (fun () ->
        let path = Path.Combine(root, "ignore-input.fsx")
        File.WriteAllText(path, "System.Threading.Thread.Sleep 30000\n")
        let watch = Stopwatch.StartNew()
        let result = unwrap (BoundedProcess.run (cmd "dotnet" ["fsi"; "--exec"; path] (BoundedProcess.TextInput (String.replicate 300000 "x")) 5. 4096))
        assertThat (result.TimedOut && watch.Elapsed.TotalSeconds < 9.) (sprintf "Outcome %A duration=%A" result watch.Elapsed))

    runCase "descendant_held_both_pipes_then_timeout" (fun () ->
        let marker = Path.Combine(root, "pipe-child.pid")
        let childPath = Path.Combine(root, "pipe-child.fsx")
        let parentPath = Path.Combine(root, "pipe-parent.fsx")
        File.WriteAllText(childPath, sprintf "open System\nSystem.IO.File.WriteAllText(@\"%s\", string Environment.ProcessId)\nprintfn \"PIPE_CHILD_STDOUT\"\neprintfn \"PIPE_CHILD_STDERR\"\nSystem.Threading.Thread.Sleep 30000\n" marker)
        let parentLines =
            [ "open System"; "open System.Diagnostics"; "open System.IO"; "open System.Threading"
              "let info = ProcessStartInfo(\"dotnet\")"
              "info.ArgumentList.Add(\"fsi\")"
              "info.ArgumentList.Add(\"--exec\")"
              sprintf "info.ArgumentList.Add(@\"%s\")" childPath
              "info.UseShellExecute <- false"
              "info.RedirectStandardOutput <- false"
              "info.RedirectStandardError <- false"
              "use child = Process.Start(info)"
              sprintf "let marker = @\"%s\"" marker
              "let deadline = DateTime.UtcNow.AddSeconds 10."
              "while not (File.Exists marker) && DateTime.UtcNow < deadline do Thread.Sleep 20"
              "if not (File.Exists marker) then failwith \"Descendant never started\""
              "printfn \"PIPE_PARENT_EXIT\"" ]
        File.WriteAllText(parentPath, String.concat "\n" parentLines + "\n")
        try
            let result = unwrap (BoundedProcess.run (cmd "dotnet" ["fsi"; "--exec"; parentPath] BoundedProcess.ClosedInput 12. 4096))
            assertThat (File.Exists marker) (sprintf "Descendant did not start; outcome=%A" result)
            let pid = readPid marker
            assertThat (waitUntil 3. (fun () -> not (alive pid))) (sprintf "Descendant survives: %d; outcome=%A" pid result)
            // A Windows grandchild without stdout inheritance is legitimate, but then success
            // must still terminate the full Job Object; when inherited pipes exist, expect timeout.
            if result.TimedOut then
                assertThat (result.ExitCode = -1) (sprintf "Expected -1 after deadline: %A" result)
            else
                assertThat (result.ExitCode = 0) (sprintf "Successful parent with child cleanup: %A" result)
            printfn "PIPE_CASE timedOut=%b parent_marker=%b stdout_child=%b stderr_child=%b descendant_pid=%d deceased=true"
                result.TimedOut (result.StdOut.Contains "PIPE_PARENT_EXIT")
                (result.StdOut.Contains "PIPE_CHILD_STDOUT") (result.StdErr.Contains "PIPE_CHILD_STDERR") pid
        finally
            if File.Exists marker then stop (readPid marker))

    runCase "success_reaps_detached_descendant" (fun () ->
        let marker = Path.Combine(root, "success-child.pid")
        let parentMarker = Path.Combine(root, "success-parent.pid")
        let childPath = Path.Combine(root, "success-child.fsx")
        let parentPath = Path.Combine(root, "success-parent.fsx")
        File.WriteAllText(childPath, sprintf "System.IO.File.WriteAllText(@\"%s\", string System.Environment.ProcessId)\nSystem.Threading.Thread.Sleep 30000\n" marker)
        let parentLines =
            [ "open System"; "open System.Diagnostics"; "open System.IO"; "open System.Threading"
              "let info = ProcessStartInfo(\"dotnet\")"
              "info.ArgumentList.Add(\"fsi\")"
              "info.ArgumentList.Add(\"--exec\")"
              sprintf "info.ArgumentList.Add(@\"%s\")" childPath
              "info.UseShellExecute <- false"
              "info.RedirectStandardOutput <- true"
              "info.RedirectStandardError <- true"
              "use child = Process.Start(info)"
              sprintf "let marker = @\"%s\"" marker
              "let deadline = DateTime.UtcNow.AddSeconds 10."
              "while not (File.Exists marker) && DateTime.UtcNow < deadline do Thread.Sleep 20"
              "if not (File.Exists marker) then failwith \"Descendant never started\""
              sprintf "File.WriteAllText(@\"%s\", string Environment.ProcessId)" parentMarker
              "printfn \"SUCCESS_PARENT_EXIT\"" ]
        File.WriteAllText(parentPath, String.concat "\n" parentLines + "\n")
        try
            let running = Task.Run(fun () -> BoundedProcess.run (cmd "dotnet" ["fsi"; "--exec"; parentPath] BoundedProcess.ClosedInput 20. 4096))
            assertThat (waitUntil 10. (fun () -> File.Exists marker && File.Exists parentMarker)) "Success descendant/parent never started"
            let childPid = readPid marker
            let parentPid = readPid parentMarker
            Thread.Sleep(2500)
            printfn "SUCCESS_LIVE_TREE parent_pid=%d parent_alive=%b descendant_pid=%d descendant_alive=%b bounded_finished=%b"
                parentPid (alive parentPid) childPid (alive childPid) running.IsCompleted
            let result = unwrap (running.GetAwaiter().GetResult())
            assertThat (not result.TimedOut && result.ExitCode = 0) (sprintf "Outcome %A" result)
            assertThat (File.Exists marker) "Success-descendant did not start"
            let pid = readPid marker
            assertThat (waitUntil 3. (fun () -> not (alive pid))) (sprintf "Success-descendant survived: %d" pid)
            printfn "SUCCESS_CASE descendant_pid=%d deceased=true" pid
        finally
            if File.Exists marker then stop (readPid marker))

    runCase "owner_early_crash_closes_job_and_reaps_child" (fun () ->
        let marker = Path.Combine(root, "owner-crash-child.pid")
        let childPath = Path.Combine(root, "owner-crash-child.fsx")
        let ownerPath = Path.Combine(root, "owner-crash-owner.fsx")
        File.WriteAllText(childPath, sprintf "System.IO.File.WriteAllText(@\"%s\", string System.Environment.ProcessId)\nSystem.Threading.Thread.Sleep 30000\n" marker)
        let ownerLines =
            [ sprintf "#I @\"%s\"" (Path.Combine(Environment.CurrentDirectory, "Client", "bin", "Release", "net11.0")); "#r \"Common.dll\""; "#r \"JarvisClient.dll\""
              "open System"; "open Client"
              "let command : BoundedProcess.Command ="
              "    { Executable = \"dotnet\""
              sprintf "      Args = [\"fsi\"; \"--exec\"; @\"%s\"]" childPath
              sprintf "      WorkingDirectory = @\"%s\"" root
              "      Timeout = TimeSpan.FromSeconds 25."
              "      MaxOutputBytes = Some 4096"
              "      StandardInput = BoundedProcess.ClosedInput"
              "      ExtraEnvironment = []"
              "      ConfigureStart = None }"
              "BoundedProcess.run command |> printfn \"Owner result: %A\"" ]
        File.WriteAllText(ownerPath, String.concat "\n" ownerLines + "\n")
        use owner = new Process()
        owner.StartInfo.FileName <- "dotnet"
        owner.StartInfo.WorkingDirectory <- Environment.CurrentDirectory
        owner.StartInfo.UseShellExecute <- false
        owner.StartInfo.RedirectStandardOutput <- true
        owner.StartInfo.RedirectStandardError <- true
        for arg in ["fsi"; "--exec"; ownerPath] do owner.StartInfo.ArgumentList.Add arg
        assertThat (owner.Start()) "Owner test process did not start"
        try
            assertThat (waitUntil 12. (fun () -> File.Exists marker || owner.HasExited)) "Owner child startup timed out"
            assertThat (File.Exists marker) "Owner exited before target created process marker"
            let pid = readPid marker
            // Kill only the direct owner; Kill(true) would destroy the child independent
            // of the production JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE mechanism.
            owner.Kill(false)
            owner.WaitForExit(5000) |> ignore
            assertThat (waitUntil 4. (fun () -> not (alive pid))) (sprintf "Target survived owner crash, PID=%d" pid)
            printfn "OWNER_CRASH_CASE target_pid=%d deceased=true" pid
        finally
            if not owner.HasExited then owner.Kill(false)
            if File.Exists marker then stop (readPid marker))

    if failures.Count = 0 then
        printfn "W23_SMOKE_SUITE_PASS checks=12"
    else
        printfn "W23_SMOKE_SUITE_FAIL failures=%d" failures.Count
        for failure in failures do printfn "DETAIL %s" failure
        failwithf "Native Windows smoke failures: %d" failures.Count
finally
    try Directory.Delete(root, true) with e -> printfn "SCRATCH_CLEANUP_WARNING %s" e.Message
