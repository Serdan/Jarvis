module Client.SelfUpdate

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text.Json
open Common

type internal PreparedUpdate =
    { LivePath: string
      StagedPath: string
      BackupPath: string
      Arguments: string array }

[<DllImport("libc", SetLastError = true)>]
extern int private execv(string path, nativeint arguments)

let private gate = obj()
let mutable private pending: PreparedUpdate option = None
let private maxDownloadBytes = 256L * 1024L * 1024L

let runtimeIdentifier () =
    let arch = RuntimeInformation.ProcessArchitecture
    if RuntimeInformation.IsOSPlatform(OSPlatform.Linux) then
        match arch with
        | Architecture.X64 -> Ok "linux-x64"
        | _ -> Error(Client.ValidationError $"No published self-update artifact is available for Linux {arch}.")
    elif RuntimeInformation.IsOSPlatform(OSPlatform.OSX) then
        match arch with
        | Architecture.Arm64 -> Ok "osx-arm64"
        | _ -> Error(Client.ValidationError $"No published self-update artifact is available for macOS {arch}.")
    else
        Error(Client.ValidationError "Self-update currently supports Linux and macOS published clients.")

let parseChecksum (fileName: string) (manifest: string) =
    manifest.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries)
    |> Array.tryPick (fun line ->
        let parts = line.Trim().Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
        if parts.Length >= 2 && String.Equals(parts[parts.Length - 1], fileName, StringComparison.Ordinal) then
            let hash = parts[0].Trim().ToLowerInvariant()
            if hash.Length = 64 && hash |> Seq.forall Uri.IsHexDigit then Some hash else None
        else None)
    |> function
       | Some hash -> Ok hash
       | None -> Error(Client.ValidationError $"No valid SHA-256 entry for {fileName} was found in the update manifest.")

// The immutable release pointer and stable checksum channel must agree on
// the exact binary. A dirty build or mixed-platform release is not admissible.
let parseReleaseManifest (rid: string) (fileName: string) (manifest: string) =
    try
        use json = JsonDocument.Parse(manifest)
        let root = json.RootElement
        let version = root.GetProperty("applicationVersion").GetString()
        let commit = root.GetProperty("gitCommit").GetString()
        let releaseId = root.GetProperty("releaseId").GetString()
        let dirty = root.GetProperty("dirty").GetBoolean()
        let schema = root.GetProperty("schemaVersion").GetInt32()
        if schema <> 1 || dirty || String.IsNullOrWhiteSpace version
           || isNull commit || commit.Length <> 40 || not (Seq.forall Uri.IsHexDigit commit)
           || releaseId <> version + "-" + commit.Substring(0, 12) then
            Error(Client.ValidationError "The current release manifest has an invalid or unapproved build identity.")
        else
            let expectedPath = $"releases/{releaseId}/{fileName}"
            let matching =
                root.GetProperty("artifacts").EnumerateArray()
                |> Seq.tryFind (fun artifact ->
                    artifact.GetProperty("rid").GetString() = rid
                    && artifact.GetProperty("fileName").GetString() = fileName)
            match matching with
            | None -> Error(Client.ValidationError $"The current release does not contain {rid}/{fileName}.")
            | Some artifact ->
                let path = artifact.GetProperty("releasePath").GetString()
                let hash = artifact.GetProperty("sha256").GetString()
                let size = artifact.GetProperty("bytes").GetInt64()
                if path <> expectedPath || isNull hash || hash.Length <> 64
                   || not (Seq.forall Uri.IsHexDigit hash) || size <= 0L then
                    Error(Client.ValidationError "Release artifact identity, path, or SHA-256 is invalid.")
                else Ok(hash.ToLowerInvariant())
    with
    | :? JsonException
    | :? KeyNotFoundException
    | :? InvalidOperationException
    | :? NullReferenceException ->
        Error(Client.ValidationError "The current release manifest is malformed.")

let verifyReleaseDownloads (rid: string) (fileName: string) (checksums: string) (latest: string) =
    match parseChecksum fileName checksums, parseReleaseManifest rid fileName latest with
    | Error error, _
    | _, Error error -> Error error
    | Ok checksum, Ok latestHash when checksum <> latestHash ->
        Error(Client.ValidationError "Release manifest and SHA256SUMS disagree; refusing update.")
    | Ok expectedHash, Ok _ -> Ok expectedHash

let private publishedFileName (rid: string) =
    if rid.StartsWith("win-", StringComparison.Ordinal) then $"JarvisClient-{rid}.exe"
    else $"JarvisClient-{rid}"

let private downloadRoot () =
    let server = Uri(BuildInfo.ServerUrl, UriKind.Absolute)
    Uri(server.GetLeftPart(UriPartial.Authority) + "/downloads/")

let private validateLiveExecutable () =
    match Environment.ProcessPath with
    | null -> Error(Client.ValidationError "Could not determine the running Jarvis executable path.")
    | path when not (File.Exists path) -> Error(Client.ValidationError $"Running executable does not exist: {path}")
    | path ->
        let name = Path.GetFileName path
        if name.StartsWith("JarvisClient", StringComparison.OrdinalIgnoreCase) then Ok path
        else Error(Client.ValidationError $"Self-update requires a published JarvisClient executable; current process is {name}.")

let private sha256File path =
    use stream = File.OpenRead path
    SHA256.HashData(stream)
    |> Convert.ToHexString
    |> fun value -> value.ToLowerInvariant()

let prepare () =
    task {
        match runtimeIdentifier (), validateLiveExecutable () with
        | Error error, _
        | _, Error error -> return Error error
        | Ok rid, Ok livePath ->
            let fileName = publishedFileName rid
            let root = downloadRoot ()
            let manifestUri = Uri(root, "SHA256SUMS")
            let downloadUri = Uri(root, fileName)
            let directory = Path.GetDirectoryName livePath
            let stagedPath = Path.Combine(directory, $".{Path.GetFileName(livePath)}.{Guid.NewGuid():N}.next")
            let backupPath = livePath + ".prev"

            try
                use http = new HttpClient()
                http.Timeout <- TimeSpan.FromMinutes 5.0
                let! manifest = http.GetStringAsync manifestUri
                let! latest = http.GetStringAsync(Uri(root, "latest.json"))
                match verifyReleaseDownloads rid fileName manifest latest with
                | Error error -> return Error error
                | Ok expectedHash ->
                    use! response = http.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead)
                    if not response.IsSuccessStatusCode then
                        return Error(Client.ValidationError $"Update download failed with HTTP status {int response.StatusCode}.")
                    elif response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value > maxDownloadBytes then
                        return Error(Client.ValidationError $"Published client exceeds the {maxDownloadBytes} byte self-update limit.")
                    else
                        use! source = response.Content.ReadAsStreamAsync()
                        use destination = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.SequentialScan)
                        let buffer = Array.zeroCreate<byte> 81920
                        let mutable total = 0L
                        let mutable finished = false
                        while not finished do
                            let! count = source.ReadAsync(buffer.AsMemory(0, buffer.Length))
                            if count = 0 then finished <- true
                            else
                                total <- total + int64 count
                                if total > maxDownloadBytes then
                                    raise (InvalidDataException $"Published client exceeds the {maxDownloadBytes} byte self-update limit.")
                                do! destination.WriteAsync(buffer.AsMemory(0, count))
                        do! destination.FlushAsync()
                        destination.Flush(true)
                        destination.Dispose()

                        let actualHash = sha256File stagedPath
                        if not (String.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase)) then
                            File.Delete stagedPath
                            return Error(Client.ValidationError $"Update checksum mismatch. Expected {expectedHash}, got {actualHash}.")
                        else
                            if not (OperatingSystem.IsWindows()) then
                                File.SetUnixFileMode(
                                    stagedPath,
                                    UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute |||
                                    UnixFileMode.GroupRead ||| UnixFileMode.GroupExecute |||
                                    UnixFileMode.OtherRead ||| UnixFileMode.OtherExecute)

                            let prepared =
                                { LivePath = livePath
                                  StagedPath = stagedPath
                                  BackupPath = backupPath
                                  Arguments = Environment.GetCommandLineArgs() |> Array.skip 1 }
                            lock gate (fun () ->
                                pending |> Option.iter (fun old -> if File.Exists old.StagedPath then File.Delete old.StagedPath)
                                pending <- Some prepared)

                            return
                                Ok
                                    { RuntimeIdentifier = rid
                                      Sha256 = actualHash
                                      DownloadUrl = downloadUri.AbsoluteUri
                                      RestartScheduled = true }
            with ex ->
                if File.Exists stagedPath then
                    try File.Delete stagedPath with _ -> ()
                return Error(Client.ExceptionError ex)
    }

let private execUnix path (arguments: string array) =
    let argumentPointers =
        Array.append [| path |] arguments
        |> Array.map Marshal.StringToCoTaskMemUTF8

    let argv = Marshal.AllocCoTaskMem((argumentPointers.Length + 1) * IntPtr.Size)

    try
        argumentPointers
        |> Array.iteri (fun index pointer -> Marshal.WriteIntPtr(argv, index * IntPtr.Size, pointer))
        Marshal.WriteIntPtr(argv, argumentPointers.Length * IntPtr.Size, IntPtr.Zero)

        let result = execv(path, argv)
        let error = Marshal.GetLastPInvokeError()
        raise (InvalidOperationException($"Failed to replace the Jarvis process with {path}; execv returned {result} (errno {error})."))
    finally
        Marshal.FreeCoTaskMem argv
        argumentPointers |> Array.iter Marshal.FreeCoTaskMem

let private restoreBackup (update: PreparedUpdate) activationError =
    try
        if File.Exists update.LivePath then File.Delete update.LivePath
        if File.Exists update.BackupPath then File.Move(update.BackupPath, update.LivePath)
        activationError
    with rollbackError ->
        AggregateException("Self-update activation and rollback both failed.", activationError, rollbackError) :> exn

let internal activate (update: PreparedUpdate) =
    try
        if File.Exists update.BackupPath then File.Delete update.BackupPath
        File.Move(update.LivePath, update.BackupPath)
        try
            File.Move(update.StagedPath, update.LivePath)

            if OperatingSystem.IsWindows() then
                let startInfo = ProcessStartInfo(update.LivePath)
                startInfo.UseShellExecute <- false
                startInfo.WorkingDirectory <- Environment.CurrentDirectory
                update.Arguments |> Array.iter startInfo.ArgumentList.Add
                match Process.Start startInfo with
                | null -> raise (InvalidOperationException "Failed to start updated Jarvis client.")
                | _ ->
                    Environment.Exit 0
                    Ok true
            else
                execUnix update.LivePath update.Arguments
                Ok true
        with ex ->
            raise (restoreBackup update ex)
    with ex -> Error(Client.ExceptionError ex)

let activatePending () =
    let prepared = lock gate (fun () -> let value = pending in pending <- None; value)
    match prepared with
    | None -> Ok false
    | Some update -> activate update
