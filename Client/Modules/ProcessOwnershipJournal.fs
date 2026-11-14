namespace Client

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text
open System.Text.Json

// Append-only diagnostic evidence for explicit StartJob process launches.
// None of these records certifies process-tree termination.
module ProcessOwnershipJournal =
    [<CLIMutable>]
    type Intent =
        { SchemaVersion: int
          JobId: string
          ProjectId: string
          DeviceId: string
          ClientIncarnationId: string
          CreatedUtc: string }

    [<CLIMutable>]
    type Launch =
        { SchemaVersion: int
          JobId: string
          IntentSha256: string
          ProcessId: int
          ProcessStartUtc: string
          LinuxBootId: string
          LinuxStartTicks: string
          RecordedUtc: string }

    // Kernel identity is a separate immutable pre-exec stage. The native
    // cgroup must already exist before the requested executable may start.
    [<CLIMutable>]
    type NativeScope =
        { SchemaVersion: int
          JobId: string
          IntentSha256: string
          LinuxCgroupPath: string
          LinuxBootId: string
          RecordedUtc: string }

    [<CLIMutable>]
    type Evidence =
        { Intent: Intent
          Launch: Launch option
          NativeScope: NativeScope option }

    let private incarnationId = Guid.NewGuid().ToString("D")
    let private utf8 = UTF8Encoding(false, true)

    [<DllImport("libc", EntryPoint = "open", SetLastError = true)>]
    extern int private openDirectory(string path, int flags)
    [<DllImport("libc", EntryPoint = "fsync", SetLastError = true)>]
    extern int private syncDirectoryFd(int fd)
    [<DllImport("libc", EntryPoint = "close", SetLastError = true)>]
    extern int private closeDirectoryFd(int fd)

    let private syncDirectory path =
        if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
            let fd = openDirectory(path, 0)
            if fd < 0 then
                raise (IOException($"Cannot open process journal directory for durable sync (errno {Marshal.GetLastPInvokeError()})."))
            try
                if syncDirectoryFd(fd) <> 0 then
                    raise (IOException($"Cannot fsync process journal directory (errno {Marshal.GetLastPInvokeError()})."))
            finally
                closeDirectoryFd(fd) |> ignore

    let private regularDirectory path =
        if DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint) then
            raise (IOException("Process journal directory must not be a symbolic link."))

    let private ensureDirectory root =
        let parent = Path.GetDirectoryName(Path.GetFullPath root)
        if String.IsNullOrWhiteSpace parent then
            invalidOp "Process journal requires a valid parent directory."
        Directory.CreateDirectory(parent) |> ignore
        regularDirectory parent
        let created = not (Directory.Exists root)
        Directory.CreateDirectory(root) |> ignore
        regularDirectory root
        if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
            File.SetUnixFileMode(root,
                UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
        if created then syncDirectory parent

    let defaultDirectory () =
        Path.Combine(Path.GetDirectoryName(ClientConfig.configPath()), "process-ownership-v1")

    let private canonicalJobId jobId =
        match Guid.TryParseExact(jobId, "N") with
        | true, value when value <> Guid.Empty && value.ToString("N") = jobId -> jobId
        | _ -> invalidArg "jobId" "Process journal requires canonical GUID-N job ID."

    let private filePath directory jobId stage =
        Path.Combine(directory, canonicalJobId jobId + "." + stage + ".json")

    let private digest (payload: string) =
        SHA256.HashData(utf8.GetBytes payload) |> Convert.ToHexString

    // Immutable stage files: a torn/partial creation is retained as unknown
    // evidence, never overwritten or treated as successful completion.
    let private writeNew directory path (payload: string) =
        ensureDirectory directory
        if File.Exists(path) || Directory.Exists(path) then
            raise (IOException("Process journal stage already exists; refusing to overwrite."))
        use output = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)
        if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
            File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
        let bytes = utf8.GetBytes payload
        output.Write(bytes, 0, bytes.Length)
        output.Flush(true)
        syncDirectory directory

    let private readRaw path =
        if not (File.Exists path) then
            if Directory.Exists path then
                raise (IOException("Process journal stage is a directory."))
            None
        elif File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) then
            raise (IOException("Process journal stage must not be a symbolic link."))
        elif FileInfo(path).Length > 4096L then
            raise (IOException("Process journal stage exceeds its maximum size."))
        else
            use input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
            use reader = new StreamReader(input, utf8, false)
            let value = reader.ReadToEnd()
            if String.IsNullOrWhiteSpace value then
                raise (IOException("Process journal stage is empty or torn."))
            Some value

    let private validateIntent expectedJobId (intent: Intent) =
        if isNull (box intent)
           || intent.SchemaVersion <> 1 || intent.JobId <> expectedJobId
           || String.IsNullOrWhiteSpace intent.ProjectId
           || String.IsNullOrWhiteSpace intent.DeviceId
           || String.IsNullOrWhiteSpace intent.ClientIncarnationId
           || String.IsNullOrWhiteSpace intent.CreatedUtc then
            raise (IOException("Process journal intent is incomplete or corrupt."))
        intent

    let beginLaunchAt directory jobId projectId deviceId =
        let jobId = canonicalJobId jobId
        if String.IsNullOrWhiteSpace projectId || String.IsNullOrWhiteSpace deviceId then
            invalidArg "projectId" "Process journal requires project and device identities."
        let intent =
            { SchemaVersion = 1; JobId = jobId; ProjectId = projectId
              DeviceId = deviceId; ClientIncarnationId = incarnationId
              CreatedUtc = DateTimeOffset.UtcNow.ToString("O") }
        writeNew directory (filePath directory jobId "intent") (JsonSerializer.Serialize intent)
        intent

    let recordNativeScopeAt directory (intent: Intent) (scope: LinuxJobScope.Identity) =
        let jobId = canonicalJobId intent.JobId
        let persisted =
            readRaw (filePath directory jobId "intent")
            |> Option.defaultWith (fun () ->
                raise (IOException("Process intent missing before native scope registration.")))
        let loaded = JsonSerializer.Deserialize<Intent>(persisted) |> validateIntent jobId
        if loaded <> intent || scope.JobId <> jobId
           || String.IsNullOrWhiteSpace scope.Directory
           || String.IsNullOrWhiteSpace scope.BootId then
            raise (IOException("Native cgroup identity does not match immutable launch intent."))
        match LinuxJobScope.inspect scope with
        | LinuxJobScope.Unverified _ ->
            raise (IOException("Native scope must exist on this kernel before journaling."))
        | _ -> ()
        let record =
            { SchemaVersion = 1; JobId = jobId; IntentSha256 = digest persisted
              LinuxCgroupPath = scope.Directory; LinuxBootId = scope.BootId
              RecordedUtc = DateTimeOffset.UtcNow.ToString("O") }
        writeNew directory (filePath directory jobId "native-scope") (JsonSerializer.Serialize record)
        record

    let private linuxStartTicks pid =
        if not (OperatingSystem.IsLinux()) then "" else
            try
                let contents = File.ReadAllText($"/proc/{pid}/stat")
                let endName = contents.LastIndexOf(')')
                if endName < 0 || endName + 2 >= contents.Length then "" else
                    // The /proc stat suffix starts at field 3. Birth tick is 22.
                    let parts = contents.Substring(endName + 2).Split(
                        ' ', StringSplitOptions.RemoveEmptyEntries)
                    if parts.Length <= 19 then "" else
                        match UInt64.TryParse(parts[19]) with
                        | true, value when value > 0UL -> value.ToString()
                        | _ -> ""
            with _ -> ""

    let private linuxBootId () =
        if not (OperatingSystem.IsLinux()) then "" else
            try
                let value = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim()
                match Guid.TryParse value with
                | true, parsed when parsed <> Guid.Empty -> parsed.ToString("D")
                | _ -> ""
            with _ -> ""

    let private processStartUtc (proc: Process) =
        try proc.StartTime.ToUniversalTime().ToString("O")
        with _ -> ""

    let recordLaunchedAt directory (intent: Intent) (proc: Process) =
        let jobId = canonicalJobId intent.JobId
        let persisted =
            readRaw (filePath directory jobId "intent")
            |> Option.defaultWith (fun () ->
                raise (IOException("Process journal intent missing before launch registration.")))
        let loaded = JsonSerializer.Deserialize<Intent>(persisted) |> validateIntent jobId
        if loaded <> intent then
            raise (IOException("Process journal intent changed before launch registration."))
        let pid = proc.Id
        if pid <= 0 then invalidOp "Native process has no OS PID."
        let launch =
            { SchemaVersion = 1; JobId = jobId; IntentSha256 = digest persisted
              ProcessId = pid; ProcessStartUtc = processStartUtc proc
              LinuxBootId = linuxBootId(); LinuxStartTicks = linuxStartTicks pid
              RecordedUtc = DateTimeOffset.UtcNow.ToString("O") }
        writeNew directory (filePath directory jobId "launched") (JsonSerializer.Serialize launch)
        launch

    // Read-only forensics after a restart. A missing stage remains uncertain.
    // Even complete PID/birth metadata is NOT descendant termination proof.
    let inspectAt directory jobId =
        let jobId = canonicalJobId jobId
        if not (Directory.Exists directory) then
            Error "Process journal directory is missing."
        else
            try
                regularDirectory directory
                match readRaw(filePath directory jobId "intent") with
                | None -> Error "Process journal intent missing."
                | Some raw ->
                    let intent = JsonSerializer.Deserialize<Intent>(raw) |> validateIntent jobId
                    let nativeScope =
                        match readRaw(filePath directory jobId "native-scope") with
                        | None -> Ok None
                        | Some stage ->
                            let scope = JsonSerializer.Deserialize<NativeScope>(stage)
                            if isNull (box scope)
                               || scope.SchemaVersion <> 1 || scope.JobId <> jobId
                               || scope.IntentSha256 <> digest raw
                               || String.IsNullOrWhiteSpace scope.LinuxCgroupPath
                               || String.IsNullOrWhiteSpace scope.LinuxBootId
                               || String.IsNullOrWhiteSpace scope.RecordedUtc then
                                Error "Native StartJob scope evidence is incomplete or mismatched."
                            else Ok(Some scope)
                    match nativeScope with
                    | Error failure -> Error failure
                    | Ok nativeScope ->
                        match readRaw(filePath directory jobId "launched") with
                        | None -> Ok { Intent = intent; Launch = None; NativeScope = nativeScope }
                        | Some second ->
                            let launch = JsonSerializer.Deserialize<Launch>(second)
                            if isNull (box launch)
                               || launch.SchemaVersion <> 1 || launch.JobId <> jobId
                               || launch.IntentSha256 <> digest raw || launch.ProcessId <= 0
                               || String.IsNullOrWhiteSpace launch.RecordedUtc then
                                Error "Process journal launch is incomplete, corrupt or mismatched."
                            else
                                Ok { Intent = intent; Launch = Some launch; NativeScope = nativeScope }
            with ex -> Error $"Process journal unavailable or corrupt: {ex.GetType().Name}"
