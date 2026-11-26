module Client.BoundedProcess

open System
open System.ComponentModel
open System.Diagnostics
open System.IO
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

module private Native =
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

    let newMacSession () = setsidMac()

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

// This mode uses the existing early internal-host entry point, not a second
// client or a scheduler. The supervisor alone owns the child and its exit
// evidence. The UI/SignalR host may exec() or restart without owning any pipes.
module DurableJobs =
    open Common
    open System.Text.Json
    open System.Security.Cryptography

    let mode = "--durable-engineering-job-v1"
    let maxOutputBytes = 1024 * 1024
    let maxRequestBytes = 256 * 1024
    let private maxReceiptBytes = 8 * 1024 * 1024
    let private maxEvents = 8192
    let private directoryMode =
        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
    let private fileMode = UnixFileMode.UserRead ||| UnixFileMode.UserWrite

    type Identity = { Pid: int; Birth: string }
    type Request =
        { Schema: int
          JobId: string
          Project: string
          Actor: string option
          Executable: string
          Args: string list
          Reason: string option
          WorkingDirectory: string
          StartedAt: DateTimeOffset
          MaxOutputBytes: int }

    type Receipt =
        { Schema: int
          JobId: string
          Supervisor: Identity
          Child: Identity option
          Started: bool
          Status: JobStatus
          CompletedAt: DateTimeOffset option
          Events: JobOutputEvent list
          NextSequence: int64
          Truncated: bool
          CancelError: string option }

    // An absent/invalid/dead supervisor receipt is uncertainty, never an exit
    // code. A recovered live observation says the *supervisor* is alive.
    type Observation =
        | Live of Receipt
        | Terminal of Receipt
        | Unknown of Receipt option

    let validId (id: string) =
        match Guid.TryParseExact(id, "N") with
        | true, value -> value.ToString("N") = id
        | _ -> false

    let private rejectLink path =
        if not (isNull (FileInfo(path).LinkTarget))
           || not (isNull (DirectoryInfo(path).LinkTarget)) then
            raise (UnauthorizedAccessException "Linked job-registry paths are forbidden.")
        if (File.Exists path || Directory.Exists path)
           && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) then
            raise (UnauthorizedAccessException "Reparse-point job-registry paths are forbidden.")

    let private ensureDirectory path =
        rejectLink path
        if OperatingSystem.IsWindows() then Directory.CreateDirectory(path) |> ignore
        else
            Directory.CreateDirectory(path, directoryMode) |> ignore
            File.SetUnixFileMode(path, directoryMode)
        rejectLink path

    let root workspace =
        let data = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        if String.IsNullOrWhiteSpace data then invalidOp "No client-local application data directory."
        let jarvis = Path.Combine(data, "Jarvis")
        let jobs = Path.Combine(jarvis, "engineering-jobs-v1")
        let normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath workspace)
        let key = if OperatingSystem.IsWindows() then normalized.ToUpperInvariant() else normalized
        let scope = SHA256.HashData(Encoding.UTF8.GetBytes key) |> Convert.ToHexString
        rejectLink jarvis
        Directory.CreateDirectory(jarvis) |> ignore
        rejectLink jarvis
        ensureDirectory jobs
        let path = Path.Combine(jobs, scope)
        ensureDirectory path
        path

    let jobDirectory root id =
        if not (validId id) then invalidArg "id" "Invalid engineering job identifier."
        let directory = Path.Combine(root, id)
        rejectLink directory
        directory

    let private openPrivate path mode =
        rejectLink path
        let options = FileStreamOptions(Mode = mode, Access = FileAccess.Write, Share = FileShare.None)
        if not (OperatingSystem.IsWindows()) then options.UnixCreateMode <- fileMode
        new FileStream(path, options)

    let private writeAtomic limit path value =
        let bytes = JsonSerializer.SerializeToUtf8Bytes value
        if bytes.Length > limit then invalidOp "Engineering job receipt exceeds its storage bound."
        rejectLink path
        let stage = path + "." + Guid.NewGuid().ToString("N") + ".tmp"
        try
            do
                use output = openPrivate stage FileMode.CreateNew
                output.Write(bytes)
                output.Flush(true)
            rejectLink path
            File.Move(stage, path, true)
        finally
            if File.Exists stage then File.Delete stage

    let private readBounded<'T> limit path =
        rejectLink path
        use input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete)
        if input.Length > int64 limit || input.Length = 0L then invalidOp "Invalid engineering job record size."
        let bytes = Array.zeroCreate<byte> (int input.Length)
        input.ReadExactly(bytes)
        JsonSerializer.Deserialize<'T>(bytes)

    let private requestPath directory = Path.Combine(directory, "request.json")
    let private receiptPath directory = Path.Combine(directory, "receipt.json")
    let private claimPath directory = Path.Combine(directory, "claimed")
    let private cancelPath directory = Path.Combine(directory, "cancel")

    let create root (request: Request) =
        let directory = jobDirectory root request.JobId
        if Directory.Exists directory then invalidOp "Engineering job id is already registered."
        ensureDirectory directory
        try
            writeAtomic maxRequestBytes (requestPath directory) request
            directory
        with _ ->
            try Directory.Delete(directory, false) with _ -> ()
            reraise()

    let readRequest directory =
        let request = readBounded<Request> maxRequestBytes (requestPath directory)
        if request.Schema <> 1 || not (validId request.JobId)
           || Path.GetFileName(directory) <> request.JobId
           || String.IsNullOrWhiteSpace request.Project
           || String.IsNullOrWhiteSpace request.Executable
           || request.MaxOutputBytes <= 0 || request.MaxOutputBytes > maxOutputBytes then
            invalidOp "Invalid engineering job request."
        request

    let private readReceipt directory (request: Request) =
        try
            let receipt = readBounded<Receipt> maxReceiptBytes (receiptPath directory)
            if receipt.Schema <> 1 || receipt.JobId <> request.JobId then None
            else Some receipt
        with _ -> None

    let private identity (proc: Process) =
        try
            if proc.HasExited then None
            elif OperatingSystem.IsLinux() then
                // PID + kernel birth tick + boot ID. Wall-clock changes do not
                // change the token, and a recycled PID never authorizes action.
                let stat = File.ReadAllText($"/proc/{proc.Id}/stat")
                let fields = stat.Substring(stat.LastIndexOf(')') + 2).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                if fields[0] = "Z" then None
                else
                    let boot = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim()
                    Some { Pid = proc.Id; Birth = boot + ":" + fields[19] }
            else
                Some { Pid = proc.Id; Birth = string (proc.StartTime.ToUniversalTime().Ticks) }
        with _ -> None

    let private alive expected =
        try
            use proc = Process.GetProcessById expected.Pid
            identity proc = Some expected
        with _ -> false

    // A supervisor may be alive even when OS identity probing fails. Its
    // lifetime lock still reserves capacity, but never authorizes signaling.
    let hasClaimOwner directory =
        let path = claimPath directory
        try
            rejectLink path
            use lease = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
            false
        with
        | :? FileNotFoundException -> false
        | :? IOException -> true
        | :? UnauthorizedAccessException -> true

    let observe directory (request: Request) =
        let terminal receipt =
            match receipt.Status with
            | Completed _ | Canceled | FailedToStart _ when receipt.CompletedAt.IsSome -> true
            | _ -> false
        match readReceipt directory request with
        | Some receipt when terminal receipt -> Terminal receipt
        | Some receipt when receipt.Status = Running && alive receipt.Supervisor -> Live receipt
        | prior ->
            // Final publication may race with the supervisor exiting. Read the
            // receipt again before reporting uncertainty; never infer success.
            match readReceipt directory request with
            | Some receipt when terminal receipt -> Terminal receipt
            | Some receipt when receipt.Status = Running && alive receipt.Supervisor -> Live receipt
            | Some receipt -> Unknown(Some receipt)
            | None -> Unknown prior

    let requestCancel directory =
        let path = cancelPath directory
        rejectLink path
        try
            use marker = openPrivate path FileMode.CreateNew
            marker.Flush(true)
        with :? IOException when File.Exists path -> ()

    let entries root =
        Directory.EnumerateDirectories(root)
        |> Seq.choose (fun path ->
            try
                rejectLink path
                if validId (Path.GetFileName path) then Some(path, readRequest path) else None
            with _ -> None)
        |> Seq.toArray

    // Stable admission lock (never unlink). A supervisor never takes this
    // lock, so polling or a slow client cannot prevent terminal publication.
    let withAdmission root f =
        let path = Path.Combine(root, "admission.lock")
        rejectLink path
        let deadline = Stopwatch.StartNew()
        let mutable stream: FileStream = null
        while isNull stream do
            try stream <- new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
            with :? IOException when deadline.Elapsed < TimeSpan.FromSeconds 10. -> Thread.Sleep 25
        use lockHandle = stream
        if not (OperatingSystem.IsWindows()) then File.SetUnixFileMode(path, fileMode)
        f ()

    // The permanent claim also has a lifetime lock. Retention cannot delete a
    // live writer even if identity probing is unavailable. Fence late startup
    // by taking or creating the claim before removing the immutable request.
    let tryPrune directory =
        try
            rejectLink directory
            let claim = claimPath directory
            rejectLink claim
            do
                use lease =
                    if File.Exists claim then
                        new FileStream(claim, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                    else openPrivate claim FileMode.CreateNew
                let request = requestPath directory
                rejectLink request
                File.Delete request
                for path in Directory.EnumerateFiles(directory) do
                    if path <> claim then
                        rejectLink path
                        File.Delete path
            // Even if a second helper reaches CreateNew after this deletion,
            // it must re-read the now absent request before doing anything.
            File.Delete claim
            Directory.Delete(directory, false)
            true
        with _ -> false

    let runSupervisor directory =
        try
            rejectLink directory
            // Permanent one-shot claim is made before any external effect.
            // A restarted client observes this job; it never re-runs it.
            use claim = openPrivate (claimPath directory) FileMode.CreateNew
            claim.Flush(true)
            let request = readRequest directory
            use self = Process.GetCurrentProcess()
            let supervisor = identity self |> Option.defaultWith (fun () -> invalidOp "Cannot fence job supervisor identity.")
            let gate = obj()
            let events = ResizeArray<JobOutputEvent>()
            let mutable bytes = 0
            let mutable next = 0L
            let mutable truncated = false
            let mutable childIdentity = None
            let mutable started = false
            let mutable cancelError = None
            let mutable canceled = false
            let mutable publishedSequence = -1L
            let mutable publishedCancelError = None
            let publish status completed =
                lock gate (fun () ->
                    let receipt =
                        { Schema = 1; JobId = request.JobId; Supervisor = supervisor
                          Child = childIdentity; Started = started; Status = status
                          CompletedAt = completed; Events = List.ofSeq events
                          NextSequence = next; Truncated = truncated; CancelError = cancelError }
                    writeAtomic maxReceiptBytes (receiptPath directory) receipt
                    publishedSequence <- next
                    publishedCancelError <- cancelError)
            let append stream (data: string) =
                if not (isNull data) then
                    lock gate (fun () ->
                        next <- next + 1L
                        let value = data + Environment.NewLine
                        let mutable remaining = max 0 (request.MaxOutputBytes - bytes)
                        let fragment = StringBuilder()
                        let mutable accepting = events.Count < maxEvents
                        for rune in value.EnumerateRunes() do
                            if accepting && rune.Utf8SequenceLength <= remaining then
                                fragment.Append(rune.ToString()) |> ignore
                                remaining <- remaining - rune.Utf8SequenceLength
                                bytes <- bytes + rune.Utf8SequenceLength
                            else
                                accepting <- false
                                truncated <- true
                        if fragment.Length > 0 then
                            events.Add { Sequence = next; Stream = stream; Text = fragment.ToString() })
            use child = new Process()
            child.StartInfo.FileName <- request.Executable
            child.StartInfo.WorkingDirectory <- request.WorkingDirectory
            child.StartInfo.RedirectStandardOutput <- true
            child.StartInfo.RedirectStandardError <- true
            child.StartInfo.UseShellExecute <- false
            child.StartInfo.CreateNoWindow <- true
            // The initiating client filtered the supervisor's inherited
            // environment exactly once. Do not reconfigure it on restart.
            request.Args |> List.iter child.StartInfo.ArgumentList.Add
            child.OutputDataReceived.Add(fun e -> append JobOutputStream.StdOut e.Data)
            child.ErrorDataReceived.Add(fun e -> append JobOutputStream.StdErr e.Data)
            publish Running None
            try
                if not (child.Start()) then invalidOp "Failed to start engineering command."
                started <- true
                childIdentity <- identity child
                child.BeginOutputReadLine()
                child.BeginErrorReadLine()
                publish Running None
                let mutable attemptedCancel = false
                while not (child.WaitForExit(100)) do
                    if not attemptedCancel && File.Exists(cancelPath directory) then
                        attemptedCancel <- true
                        try
                            if not child.HasExited then
                                child.Kill(entireProcessTree = true)
                                canceled <- true
                        with ex -> cancelError <- Some ex.Message
                    if lock gate (fun () -> next <> publishedSequence || cancelError <> publishedCancelError) then
                        publish Running None
                // WaitForExit() drains async output before exact terminal receipt.
                child.WaitForExit()
                publish (if canceled then Canceled else Completed child.ExitCode) (Some DateTimeOffset.UtcNow)
                0
            with ex ->
                // If Start succeeded, the absence of an authoritative receipt
                // is intentionally unknown. A supervisor crash must not
                // masquerade as a failed start or infer the child's exit code.
                if not started then
                    publish (FailedToStart ex.Message) (Some DateTimeOffset.UtcNow)
                1
        with _ -> 1

/// macOS uses a managed helper because the platform does not ship the
/// util-linux setsid executable. On Windows a named event is used only as
/// a startup barrier: the requested process cannot launch until its parent
/// has assigned this helper to a kill-on-close Job Object. The barrier is
/// independent of standard input, which must retain its configured meaning.
let private runProcessGroupHost (args: string array) =
    let windows = OperatingSystem.IsWindows()
    let mac = OperatingSystem.IsMacOS()
    if not (windows || mac) || args.Length < (if windows then 2 else 1) then
        eprintfn "Invalid internal process-group invocation."
        126
    elif mac && Native.newMacSession() < 0 then
        eprintfn "Could not establish process session."
        126
    else
        try
            let offset = if windows then 1 else 0
            let allowed =
                if windows then
                    use ready = EventWaitHandle.OpenExisting(args[0])
                    ready.WaitOne(TimeSpan.FromSeconds 15.)
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

let runInternalGroupHost (args: string array) =
    if args.Length = 2 && args[0] = DurableJobs.mode then DurableJobs.runSupervisor args[1]
    else runProcessGroupHost args

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

let private prepareCommand (command: Command) (windowsReadyName: string option) =
    if OperatingSystem.IsLinux() then
        let setsid =
            if File.Exists("/usr/bin/setsid") then "/usr/bin/setsid"
            elif File.Exists("/bin/setsid") then "/bin/setsid"
            else raise (FileNotFoundException "Linux bounded commands require the system setsid utility.")
        setsid, [ "--"; command.Executable ] @ command.Args
    elif OperatingSystem.IsMacOS() || OperatingSystem.IsWindows() then
        let host = Environment.ProcessPath
        if String.IsNullOrWhiteSpace host then
            raise (InvalidOperationException "Cannot locate the current Jarvis executable.")
        let dll = Assembly.GetExecutingAssembly().Location
        let args =
            [ yield groupHostFlag
              if OperatingSystem.IsWindows() then
                  yield windowsReadyName |> Option.defaultWith (fun () ->
                      invalidOp "Windows process-group startup requires a readiness event.")
              yield command.Executable
              yield! command.Args ]
        helperHostInvocation host dll args
    else
        command.Executable, command.Args

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
