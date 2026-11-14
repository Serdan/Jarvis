module Client.BoundedProcess

open System
open System.ComponentModel
open System.Diagnostics
open System.IO
open System.IO.Pipes
open System.Reflection
open System.Runtime.InteropServices
open System.Text
open System.Threading
open System.Threading.Tasks
open Client

// A bounded command owns its descendants. Long-lived StartJob executions are
// intentionally separate and are not routed through this runner.
type StandardInputMode =
    | InheritInput
    | ClosedInput
    | TextInput of string

type Command =
    { Executable: string
      Args: string list
      WorkingDirectory: string
      Timeout: TimeSpan
      MaxOutputBytes: int option
      StandardInput: StandardInputMode
      ExtraEnvironment: (string * string) list
      ConfigureStart: (ProcessStartInfo -> unit) option }

type Outcome =
    { ExitCode: int
      TimedOut: bool
      StdOut: string
      StdErr: string
      StdOutTruncated: bool
      StdErrTruncated: bool }

module internal Native =
    // JOBOBJECT_EXTENDED_LIMIT_INFORMATION (Win32). Typed field layout
    // avoids guessing native offsets or architecture-dependent padding.
    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type JobBasicLimits =
        val mutable PerProcessUserTimeLimit: int64
        val mutable PerJobUserTimeLimit: int64
        val mutable LimitFlags: uint32
        val mutable MinimumWorkingSetSize: nativeint
        val mutable MaximumWorkingSetSize: nativeint
        val mutable ActiveProcessLimit: uint32
        val mutable Affinity: nativeint
        val mutable PriorityClass: uint32
        val mutable SchedulingClass: uint32

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type JobIoCounters =
        val mutable ReadOperationCount: uint64
        val mutable WriteOperationCount: uint64
        val mutable OtherOperationCount: uint64
        val mutable ReadTransferCount: uint64
        val mutable WriteTransferCount: uint64
        val mutable OtherTransferCount: uint64

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type JobExtendedLimits =
        val mutable BasicLimitInformation: JobBasicLimits
        val mutable IoInfo: JobIoCounters
        val mutable ProcessMemoryLimit: nativeint
        val mutable JobMemoryLimit: nativeint
        val mutable PeakProcessMemoryUsed: nativeint
        val mutable PeakJobMemoryUsed: nativeint

    [<Literal>]
    let private JobObjectExtendedLimitInformation = 9
    [<Literal>]
    let private JobObjectLimitKillOnClose = 0x2000u

    [<DllImport("libc", EntryPoint = "kill", SetLastError = true)>]
    extern int private killLinux(int pid, int signal)

    [<DllImport("libSystem.B.dylib", EntryPoint = "kill", SetLastError = true)>]
    extern int private killMac(int pid, int signal)

    [<DllImport("libSystem.B.dylib", EntryPoint = "setsid", SetLastError = true)>]
    extern int private setsidMac()

    [<DllImport("libc", EntryPoint = "setsid", SetLastError = true)>]
    extern int private setsidLinux()

    let newMacSession () = setsidMac()
    let newLinuxSession () = setsidLinux()

    let killUnixGroup pid =
        // libSystem is the guaranteed macOS system library; libc is Linux.
        if OperatingSystem.IsMacOS() then killMac(-pid, 9)
        else killLinux(-pid, 9)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern nativeint CreateJobObject(nativeint attributes, string name)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool AssignProcessToJobObject(nativeint job, nativeint proc)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool private SetInformationJobObject(
        nativeint job, int infoClass,
        JobExtendedLimits& info, uint32 length)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool TerminateJobObject(nativeint job, uint32 exitCode)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool CloseHandle(nativeint handle)

    let newWindowsJob () =
        let job = CreateJobObject(0n, null)
        if job = 0n then
            raise (Win32Exception(Marshal.GetLastWin32Error()))
        try
            let mutable basic = Unchecked.defaultof<JobBasicLimits>
            basic.LimitFlags <- JobObjectLimitKillOnClose
            let mutable limits = Unchecked.defaultof<JobExtendedLimits>
            limits.BasicLimitInformation <- basic
            let length = uint32 (Marshal.SizeOf<JobExtendedLimits>())
            if not (SetInformationJobObject(job, JobObjectExtendedLimitInformation, &limits, length)) then
                raise (Win32Exception(Marshal.GetLastWin32Error()))
            job
        with _ ->
            CloseHandle(job) |> ignore
            reraise()

let internal groupHostFlag = "--jarvis-internal-process-group"

/// macOS uses a managed helper because the platform does not ship the
/// util-linux setsid executable. On Windows a named event is used only as
/// a startup barrier: the requested process cannot launch until its parent
/// has assigned this helper to a kill-on-close Job Object. The barrier is
/// independent of standard input, which must retain its configured meaning.
let runInternalGroupHost (args: string array) =
    let windows = OperatingSystem.IsWindows()
    let mac = OperatingSystem.IsMacOS()
    let linux = OperatingSystem.IsLinux()
    let gated = windows || linux
    if not (windows || mac || linux) || args.Length < (if gated then 2 else 1) then
        eprintfn "Invalid internal process-group invocation."
        126
    elif (mac && Native.newMacSession() < 0)
         || (linux && Native.newLinuxSession() < 0) then
        eprintfn "Could not establish process session."
        126
    else
        try
            let offset = if gated then 1 else 0
            let allowed =
                if windows then
                    use ready = EventWaitHandle.OpenExisting(args[0])
                    ready.WaitOne(TimeSpan.FromSeconds 15.)
                elif linux then
                    // The Linux helper cannot launch the requested executable
                    // until the parent verifies its kernel cgroup membership.
                    use ready = new NamedPipeClientStream(".", args[0], PipeDirection.In)
                    ready.Connect(15000)
                    ready.ReadByte() = 1
                else true

            if not allowed then
                eprintfn "Process-group assignment was not confirmed."
                126
            else
                use child = new Process()
                child.StartInfo.FileName <- args[offset]
                child.StartInfo.WorkingDirectory <- Environment.CurrentDirectory
                child.StartInfo.UseShellExecute <- false
                child.StartInfo.CreateNoWindow <- true
                // On Windows, CreateProcess does not reliably inherit the
                // helper's redirected standard handles when Process.Start is
                // called without redirection. An otherwise successful Git
                // command can therefore produce empty stdout/stderr, causing
                // the canonical ledger's remote URL check to fail spuriously.
                // Use explicit child pipes and forward bytes, not text lines,
                // to preserve Git's raw output and encoding.
                child.StartInfo.RedirectStandardInput <- windows
                child.StartInfo.RedirectStandardOutput <- windows
                child.StartInfo.RedirectStandardError <- windows
                args |> Seq.skip (offset + 1) |> Seq.iter child.StartInfo.ArgumentList.Add
                if not (child.Start()) then 127
                else
                    if windows then
                        let stdout = child.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput())
                        let stderr = child.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError())
                        // Forward stdin without awaiting it after the child
                        // exits: a console source can remain open indefinitely,
                        // even for a command that never reads input. EOF from
                        // a ClosedInput/TextInput parent is forwarded to the
                        // child. The helper owns the process lifetime.
                        let _stdin =
                            task {
                                try
                                    do! Console.OpenStandardInput().CopyToAsync(child.StandardInput.BaseStream)
                                    do! child.StandardInput.BaseStream.FlushAsync()
                                with
                                | :? IOException
                                | :? ObjectDisposedException -> ()
                                try child.StandardInput.Close() with _ -> ()
                            }
                        child.WaitForExit()
                        // The exit code is not sufficient until both pipes
                        // reach EOF. Descendants retaining them are bounded
                        // by the outer job-object deadline.
                        Task.WhenAll(stdout, stderr).GetAwaiter().GetResult()
                    else
                        // macOS already inherits its session helper's streams.
                        // Keep its existing behavior unchanged.
                        child.WaitForExit()
                    child.ExitCode
        with ex ->
            eprintfn "Could not launch process: %s" ex.Message
            127

type private Capture(limit: int option) =
    let gate = obj()
    let buffer = new MemoryStream()
    let mutable truncated = false

    member _.Append(bytes: byte array, count: int) =
        lock gate (fun () ->
            let allowed =
                match limit with
                | None -> count
                | Some maxBytes ->
                    min count (max 0 (maxBytes - int buffer.Length))
            if allowed > 0 then
                buffer.Write(bytes, 0, allowed)
            if allowed < count then truncated <- true)

    member _.Snapshot(encoding: Encoding) =
        lock gate (fun () ->
            let data = buffer.ToArray()
            // Process.StandardOutput/StandardError historically decoded using
            // their configured (or console-default) StreamReader encoding.
            // Preserve that behavior while independently bounding retained bytes.
            let text = encoding.GetString data
            let text =
                match limit with
                | Some maxBytes when Encoding.UTF8.GetByteCount text > maxBytes ->
                    // The captured byte prefix may finish in the middle of a
                    // multibyte rune. Never let replacement chars exceed the
                    // external maxOutputBytes contract.
                    let value = StringBuilder()
                    let mutable remaining = maxBytes
                    let mutable accepting = true
                    for rune in text.EnumerateRunes() do
                        if accepting && remaining >= rune.Utf8SequenceLength then
                            value.Append(rune.ToString()) |> ignore
                            remaining <- remaining - rune.Utf8SequenceLength
                        else
                            accepting <- false
                    value.ToString()
                | _ -> text
            text, truncated)

let private pump (stream: Stream) (capture: Capture) (token: CancellationToken) =
    task {
        let bytes = Array.zeroCreate<byte> 8192
        let mutable reading = true
        while reading do
            let! count = stream.ReadAsync(bytes.AsMemory(), token)
            if count = 0 then reading <- false
            else capture.Append(bytes, count)
    }

let private safeDispose (value: IDisposable) =
    try value.Dispose() with _ -> ()

let private tryTerminateGroup pid job =
    if OperatingSystem.IsWindows() then
        if job <> 0n then Native.TerminateJobObject(job, 1u) |> ignore
    elif OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
        // Unlike Process.Kill(entireProcessTree), this still works after the
        // immediate child exits while descendants retain inherited pipes.
        Native.killUnixGroup pid |> ignore

// Framework-dependent apphosts already know which managed assembly to run.
// Only the general dotnet host needs a DLL path prepended to its arguments.
let internal helperHostInvocation (hostPath: string) (assemblyPath: string) (args: string list) =
    let isDotnetHost =
        String.Equals(Path.GetFileNameWithoutExtension(hostPath), "dotnet",
                      StringComparison.OrdinalIgnoreCase)
    if isDotnetHost && not (String.IsNullOrWhiteSpace assemblyPath) then
        hostPath, assemblyPath :: args
    else hostPath, args

let internal groupProcessInvocation (executable: string) (args: string list) (readyName: string option) =
    if OperatingSystem.IsLinux() && readyName.IsSome then
        let host = Environment.ProcessPath
        if String.IsNullOrWhiteSpace host then
            raise (InvalidOperationException "Cannot locate the current Jarvis executable.")
        let dll = Assembly.GetExecutingAssembly().Location
        helperHostInvocation host dll
            ([ groupHostFlag; Option.get readyName; executable ] @ args)
    elif OperatingSystem.IsLinux() then
        let setsid =
            if File.Exists("/usr/bin/setsid") then "/usr/bin/setsid"
            elif File.Exists("/bin/setsid") then "/bin/setsid"
            else raise (FileNotFoundException "Linux bounded commands require the system setsid utility.")
        setsid, [ "--"; executable ] @ args
    elif OperatingSystem.IsMacOS() || OperatingSystem.IsWindows() then
        let host = Environment.ProcessPath
        if String.IsNullOrWhiteSpace host then
            raise (InvalidOperationException "Cannot locate the current Jarvis executable.")
        let dll = Assembly.GetExecutingAssembly().Location
        let args =
            [ yield groupHostFlag
              if OperatingSystem.IsWindows() then
                  yield readyName |> Option.defaultWith (fun () ->
                      invalidOp "Windows process-group startup requires a readiness event.")
              yield executable
              yield! args ]
        helperHostInvocation host dll args
    else
        executable, args

let private prepareCommand (command: Command) (windowsReadyName: string option) =
    groupProcessInvocation command.Executable command.Args windowsReadyName

let runWithCancellation (cancellation: CancellationToken) (command: Command) : Client.Result<Outcome> =
    if command.Timeout <= TimeSpan.Zero then
        Error(ValidationError "Process timeout must be positive.")
    elif command.MaxOutputBytes |> Option.exists (fun cap -> cap <= 0) then
        Error(ValidationError "Process output limit must be positive.")
    elif cancellation.IsCancellationRequested then
        Error(ContextError "Process execution canceled.")
    else
        let deadline = Stopwatch.StartNew()
        let windows = OperatingSystem.IsWindows()
        let mutable job = 0n
        let mutable readiness: EventWaitHandle option = None
        let readyName =
            if windows then Some($"Local\\Jarvis.BoundedProcess.{Guid.NewGuid():N}")
            else None
        use proc = new Process()
        use canceled = CancellationTokenSource.CreateLinkedTokenSource(cancellation)
        let stdout = Capture(command.MaxOutputBytes)
        let stderr = Capture(command.MaxOutputBytes)
        let mutable started = false

        try
            try
                if windows then
                    job <- Native.newWindowsJob()
                    let name = Option.get readyName
                    readiness <- Some(new EventWaitHandle(false, EventResetMode.ManualReset, name))

                let executable, args = prepareCommand command readyName
                proc.StartInfo.FileName <- executable
                proc.StartInfo.WorkingDirectory <- command.WorkingDirectory
                proc.StartInfo.UseShellExecute <- false
                proc.StartInfo.RedirectStandardOutput <- true
                proc.StartInfo.RedirectStandardError <- true
                proc.StartInfo.RedirectStandardInput <-
                    (match command.StandardInput with InheritInput -> false | _ -> true)
                // Git's stdin and generic TextInput payloads are UTF-8.
                // Do not depend on a Windows console's legacy code page.
                match command.StandardInput with
                | TextInput _ -> proc.StartInfo.StandardInputEncoding <- new UTF8Encoding(false)
                | _ -> ()
                proc.StartInfo.CreateNoWindow <- true
                command.ConfigureStart |> Option.iter (fun configure -> configure proc.StartInfo)
                for key, value in command.ExtraEnvironment do
                    proc.StartInfo.Environment[key] <- value
                for arg in args do proc.StartInfo.ArgumentList.Add arg

                if not (proc.Start()) then
                    Error(ContextError "Failed to start process.")
                else
                    started <- true
                    if windows then
                        if not (Native.AssignProcessToJobObject(job, proc.Handle)) then
                            raise (Win32Exception(Marshal.GetLastWin32Error()))
                        // The helper waits for this event before launching
                        // the target, independent of the actual stdin stream.
                        // Descendants therefore inherit the assigned job.
                        readiness |> Option.iter (fun ready -> ready.Set() |> ignore)

                    let stdoutEncoding = proc.StandardOutput.CurrentEncoding
                    let stderrEncoding = proc.StandardError.CurrentEncoding
                    let stdoutTask = pump proc.StandardOutput.BaseStream stdout canceled.Token
                    let stderrTask = pump proc.StandardError.BaseStream stderr canceled.Token
                    let inputTask =
                        task {
                            match command.StandardInput with
                            | TextInput value ->
                                do! proc.StandardInput.WriteAsync(value.AsMemory(), canceled.Token)
                                do! proc.StandardInput.FlushAsync(canceled.Token)
                                proc.StandardInput.Close()
                            | ClosedInput -> proc.StandardInput.Close()
                            | InheritInput -> ()
                        }
                    let exitTask = proc.WaitForExitAsync(canceled.Token)
                    let all = Task.WhenAll([| stdoutTask :> Task; stderrTask :> Task; inputTask :> Task; exitTask |])
                    let remaining = command.Timeout - deadline.Elapsed
                    let mutable timedOut = remaining <= TimeSpan.Zero
                    let mutable canceledByCaller = cancellation.IsCancellationRequested
                    if not timedOut && not canceledByCaller then
                        try
                            all.WaitAsync(remaining, cancellation).GetAwaiter().GetResult()
                        with
                        | :? TimeoutException -> timedOut <- true
                        | :? OperationCanceledException when cancellation.IsCancellationRequested ->
                            canceledByCaller <- true

                    if timedOut || canceledByCaller then
                        canceled.Cancel()
                        tryTerminateGroup proc.Id job
                        try
                            if not proc.HasExited then proc.Kill(entireProcessTree = true)
                        with _ -> ()
                        if proc.StartInfo.RedirectStandardInput then safeDispose proc.StandardInput
                        safeDispose proc.StandardOutput
                        safeDispose proc.StandardError
                        try
                            all.WaitAsync(TimeSpan.FromMilliseconds 600.).GetAwaiter().GetResult()
                        with _ -> ()

                    let output, outputTruncated = stdout.Snapshot(stdoutEncoding)
                    let errors, errorTruncated = stderr.Snapshot(stderrEncoding)
                    if canceledByCaller then
                        Error(ContextError "Process execution canceled.")
                    else
                        Ok { ExitCode = if timedOut then -1 else proc.ExitCode
                             TimedOut = timedOut
                             StdOut = output
                             StdErr = errors
                             StdOutTruncated = outputTruncated
                             StdErrTruncated = errorTruncated }
            with ex ->
                Error(ExceptionError ex)
        finally
            // Always tear down the owned process group (not just its leader).
            // A successful bounded command cannot spawn orphaned descendants;
            // StartJob is the explicit mechanism for persistent processes.
            if started then
                try tryTerminateGroup proc.Id job with _ -> ()
                try
                    if not proc.HasExited then proc.Kill(entireProcessTree = true)
                with _ -> ()
                try proc.WaitForExit(250) |> ignore with _ -> ()
            readiness |> Option.iter safeDispose
            if job <> 0n then Native.CloseHandle(job) |> ignore


// The public synchronous command interface has no cancellation token.
// Internal callers can use runWithCancellation when their operation supports it.
let run command = runWithCancellation CancellationToken.None command
