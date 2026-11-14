namespace Client

open System
open System.Globalization
open System.IO

/// Optional kernel-backed Linux StartJob containment. An empty cgroup
/// observation is NOT a certificate that arbitrary children never escaped.
/// It cannot authorize an administrative correction or release a server
/// execution witness.
module LinuxJobScope =
    [<CLIMutable>]
    type Identity =
        { JobId: string
          Directory: string
          BootId: string }

    type Population =
        | Empty
        | Occupied
        | Unverified of string

    let private prefix = "jarvis-startjob-"
    let private root = "/sys/fs/cgroup"

    let enabled () =
        OperatingSystem.IsLinux()
        && String.Equals(
            Environment.GetEnvironmentVariable("JARVIS_STARTJOB_CGROUP_V2"),
            "1", StringComparison.Ordinal)

    let private jobName jobId =
        match Guid.TryParseExact(jobId, "N") with
        | true, guid when guid <> Guid.Empty && guid.ToString("N") = jobId ->
            prefix + jobId
        | _ -> invalidArg "jobId" "Expected a nonempty canonical GUID-N StartJob identifier."

    let private bootId () =
        let value = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim()
        match Guid.TryParse value with
        | true, parsed when parsed <> Guid.Empty -> parsed.ToString("D")
        | _ -> raise (IOException("Linux kernel boot identity is unavailable."))

    let private parentCgroup () =
        if not (OperatingSystem.IsLinux()) then
            invalidOp "Linux native StartJob containment is not supported on this platform."
        if not (File.Exists(Path.Combine(root, "cgroup.controllers"))) then
            invalidOp "The host does not expose the cgroup-v2 unified hierarchy."
        let entries = File.ReadAllLines("/proc/self/cgroup")
        let entry = entries |> Array.tryFind (fun x -> x.StartsWith("0::", StringComparison.Ordinal))
        match entry with
        | None -> invalidOp "The current unified cgroup membership is missing."
        | Some line ->
            let relative = line.Substring(3)
            if not (relative.StartsWith("/", StringComparison.Ordinal))
               || relative.Split('/') |> Array.exists (fun x -> x = "." || x = "..") then
                invalidOp "The current unified cgroup membership is malformed."
            let candidate = Path.GetFullPath(Path.Combine(root, relative.TrimStart('/')))
            if candidate <> root
               && not (candidate.StartsWith(root + "/", StringComparison.Ordinal)) then
                invalidOp "Cgroup scope escaped the kernel hierarchy."
            candidate

    let private validPath (identity: Identity) =
        try
            identity.Directory = Path.GetFullPath(identity.Directory)
            && identity.Directory.StartsWith(root + "/", StringComparison.Ordinal)
            && Path.GetFileName(identity.Directory) = jobName identity.JobId
            && not (DirectoryInfo(identity.Directory).Attributes.HasFlag(FileAttributes.ReparsePoint))
        with _ -> false

    /// Each scope is unique to a job. Opt-in failure is an admission failure,
    /// never a silent fallback to unscoped execution.
    let create jobId =
        if not (enabled()) then None
        else
            let directory = Path.Combine(parentCgroup(), jobName jobId)
            if Directory.Exists directory || File.Exists directory then
                invalidOp "A native StartJob scope already exists for the job ID."
            Directory.CreateDirectory directory |> ignore
            try
                let identity =
                    { JobId = jobId; Directory = directory; BootId = bootId() }
                if not (validPath identity)
                   || not (File.Exists(Path.Combine(directory, "cgroup.events")))
                   || not (File.Exists(Path.Combine(directory, "cgroup.procs")))
                   || not (File.Exists(Path.Combine(directory, "cgroup.kill"))) then
                    invalidOp "The new StartJob scope lacks required kernel cgroup-v2 kill or observation interfaces."
                Some identity
            with _ ->
                try Directory.Delete directory with _ -> ()
                reraise()

    /// The caller must hold the executable behind a startup handshake.
    /// Without that handshake, a child could fork before containment.
    let attachBeforeExec (scope: Identity) pid =
        if pid <= 0 || not (validPath scope) || bootId() <> scope.BootId then
            invalidOp "Cannot attach a process to an unverified StartJob scope."
        File.WriteAllText(Path.Combine(scope.Directory, "cgroup.procs"),
                          pid.ToString(CultureInfo.InvariantCulture))
        let members = File.ReadAllLines(Path.Combine(scope.Directory, "cgroup.procs"))
        if not (members |> Array.exists (fun value ->
            value.Trim() = pid.ToString(CultureInfo.InvariantCulture))) then
            invalidOp "OS did not confirm StartJob process membership."

    /// A cgroup.kill attempt has no terminal-certification semantics.
    /// An unsupported kernel, removed scope or I/O failure is indeterminate.
    let tryKill (scope: Identity) =
        try
            if not (validPath scope) || bootId() <> scope.BootId then
                Error "StartJob scope identity or kernel boot changed."
            else
                let path = Path.Combine(scope.Directory, "cgroup.kill")
                if not (File.Exists(path)) then
                    Error "The kernel does not expose cgroup.kill for this StartJob scope."
                else
                    File.WriteAllText(path, "1")
                    Ok ()
        with ex -> Error($"Native cgroup kill is unverified: {ex.GetType().Name}")

    /// Parse exactly one populated key. Unknown/malformed file contents must
    /// never be interpreted as an empty process tree.
    let inspectEvents (contents: string) =
        if String.IsNullOrWhiteSpace contents then Unverified "Missing cgroup.events."
        else
            let lines = contents.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            let parsed =
                lines
                |> Array.choose (fun line ->
                    let parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    if parts.Length = 2 && parts[0] = "populated" then Some parts[1]
                    else None)
            if parsed.Length <> 1 then Unverified "Missing or duplicated populated evidence."
            elif parsed[0] = "0" then Empty
            elif parsed[0] = "1" then Occupied
            else Unverified "Malformed populated evidence."

    let inspect (scope: Identity) =
        try
            if not (OperatingSystem.IsLinux()) || not (validPath scope)
               || bootId() <> scope.BootId then
                Unverified "Unverifiable native scope identity or kernel boot."
            elif not (File.Exists(Path.Combine(scope.Directory, "cgroup.controllers"))) then
                Unverified "Scope is no longer a kernel cgroup-v2 directory."
            else
                File.ReadAllText(Path.Combine(scope.Directory, "cgroup.events"))
                |> inspectEvents
        with ex -> Unverified($"Native scope observation unavailable: {ex.GetType().Name}")
