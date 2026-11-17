namespace Server

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Jarvis.WorkSnapshots

/// A separate Git backup history, never the Work SQLite authority.
type IWorkBackupGitSink =
    abstract member PublishAsync: committedSequence: int64 * bundle: WorkSnapshotBundle * CancellationToken -> Task

/// Default-off outbox: capture is a read-only SQLite operation plus local
/// crash-durable staging. No Git transport is called on the Work mutation path.
/// A host must explicitly arrange a separate background pump after approval.
type WorkBackupOutbox(authorityPath: string, spoolPath: string, sink: IWorkBackupGitSink) =
    let singlePump = new SemaphoreSlim(1, 1)
    let sequenceName (sequence: int64) = "s-" + sequence.ToString("D20", Globalization.CultureInfo.InvariantCulture)
    let safeName (name: string) =
        Regex.IsMatch(name, @"^s-[0-9]{20}$", RegexOptions.CultureInvariant)

    let readSequence (bundle: WorkSnapshotBundle) =
        use manifest = JsonDocument.Parse(bundle.Files.["manifest.json"])
        let root = manifest.RootElement
        root.GetProperty("committedSequence").GetInt64()

    let readFiles directory =
        let paths = Directory.GetFiles(directory) |> Array.map Path.GetFileName |> Set.ofArray
        if paths <> set [ "manifest.json"; "authority.json" ] then
            invalidOp "Backup outbox item contains missing or unexpected files"
        { Files = Map.ofList [
            "manifest.json", File.ReadAllBytes(Path.Combine(directory, "manifest.json"))
            "authority.json", File.ReadAllBytes(Path.Combine(directory, "authority.json")) ] }

    let sameFiles first second =
        (first: WorkSnapshotBundle).Files.Count = (second: WorkSnapshotBundle).Files.Count
        && (first.Files |> Map.forall (fun path bytes ->
            second.Files.ContainsKey path && bytes.AsSpan().SequenceEqual(second.Files.[path])))

    member _.Capture() : Result<int64, string> =
        try
            match WorkSqliteBackup.exportSnapshot authorityPath with
            | Error reason -> Error reason
            | Ok bundle ->
                let sequence = readSequence bundle
                if sequence < 0L then invalidOp "Negative committed backup sequence"
                Directory.CreateDirectory(spoolPath) |> ignore
                let destination = Path.Combine(spoolPath, sequenceName sequence)
                // A crash may leave an orphan temporary folder. Never
                // advertise it as a committed outbox entry.
                let temporary = Path.Combine(spoolPath, ".staging-" + Guid.NewGuid().ToString("N"))
                Directory.CreateDirectory temporary |> ignore
                try
                    for KeyValue(name, bytes) in bundle.Files do
                        use output =
                            new FileStream(Path.Combine(temporary, name),
                                           FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                           4096, FileOptions.WriteThrough)
                        output.Write(bytes)
                        output.Flush(true)
                    if Directory.Exists destination then
                        if not (sameFiles bundle (readFiles destination)) then
                            invalidOp "Conflicting backup bytes at an existing committed sequence"
                    else
                        Directory.Move(temporary, destination)
                    Ok sequence
                finally
                    if Directory.Exists temporary then Directory.Delete(temporary, true)
        with ex -> Error ex.Message

    /// Publication retries after outages; pending bundles are deleted ONLY
    /// after a successful idempotent Git publication. No background worker or
    /// production Git credentials are created by this default-off type.
    member _.PublishPendingAsync(cancellationToken: CancellationToken) : Task<Result<int64 list, string>> =
        task {
            do! singlePump.WaitAsync(cancellationToken)
            try
                try
                    if not (Directory.Exists spoolPath) then
                        return Ok []
                    else
                        let items =
                            Directory.GetDirectories(spoolPath)
                            |> Array.choose (fun path ->
                                let name = Path.GetFileName path
                                if name.StartsWith(".staging-", StringComparison.Ordinal) then None
                                elif safeName name then Some(Int64.Parse(name.Substring(2)), path)
                                else invalidOp "Unexpected directory in Work backup outbox")
                            |> Array.sortBy fst
                        let published = ResizeArray<int64>()
                        for sequence, directory in items do
                            cancellationToken.ThrowIfCancellationRequested()
                            let bundle = readFiles directory
                            match WorkSqliteBackup.validateSnapshot bundle with
                            | Ok validatedSequence when validatedSequence = sequence -> ()
                            | _ -> invalidOp "Backup outbox failed checksum, format, or sequence verification"
                            // This sink must validate its own manifest before
                            // any Git write, and publication must be idempotent.
                            do! sink.PublishAsync(sequence, bundle, cancellationToken)
                            Directory.Delete(directory, true)
                            published.Add sequence
                        return Ok(List.ofSeq published)
                with ex ->
                    return Error ex.Message
            finally
                singlePump.Release() |> ignore
        }

    member _.PendingSequences() : Result<int64 list, string> =
        try
            if not (Directory.Exists spoolPath) then Ok []
            else
                Directory.GetDirectories spoolPath
                |> Array.choose (fun path ->
                    let name = Path.GetFileName path
                    if name.StartsWith(".staging-", StringComparison.Ordinal) then None
                    elif safeName name then Some(Int64.Parse(name.Substring(2)))
                    else invalidOp "Unexpected directory in Work backup outbox")
                |> Array.sort
                |> Array.toList
                |> Ok
        with ex -> Error ex.Message

/// Exact Git publication is intentionally separate from the hot path and
/// operates only on a dedicated checkout/branch. No force pushes, remote
/// mutations, implicit repository creation, or live Work registration.
type WorkBackupGitCheckoutSink(checkoutPath: string, remote: string, branch: string) =
    let validAtom (part: string) =
        not (String.IsNullOrWhiteSpace part) &&
        part.Length <= 128 &&
        Char.IsAsciiLetterOrDigit part.[0] &&
        (part |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || c = '_' || c = '-' || c = '.'))
        && not (part.Contains("..", StringComparison.Ordinal))
        && not (part.EndsWith(".", StringComparison.Ordinal))
    let validBranch =
        let parts = branch.Split('/')
        parts.Length > 0 && (parts |> Array.forall validAtom)

    let git (args: string list) (ct: CancellationToken) =
        task {
            let info = Diagnostics.ProcessStartInfo()
            info.FileName <- "git"
            info.WorkingDirectory <- checkoutPath
            info.RedirectStandardOutput <- true
            info.RedirectStandardError <- true
            info.UseShellExecute <- false
            for arg in args do info.ArgumentList.Add(arg)
            use gitProcess = new Diagnostics.Process()
            gitProcess.StartInfo <- info
            if not (gitProcess.Start()) then invalidOp "Could not start backup Git process"
            let stdout = gitProcess.StandardOutput.ReadToEndAsync(ct)
            let stderr = gitProcess.StandardError.ReadToEndAsync(ct)
            do! gitProcess.WaitForExitAsync(ct)
            let! result = stdout
            let! ignoredError = stderr
            if gitProcess.ExitCode <> 0 then
                invalidOp $"Git backup command failed ({args.Head}, exit {gitProcess.ExitCode}); changes retained in outbox"
            return result.Trim()
        }

    interface IWorkBackupGitSink with
        member _.PublishAsync(sequence, bundle, cancellationToken) =
            task {
                if not (Directory.Exists checkoutPath) || not (validAtom remote && validBranch) then
                    invalidOp "Backup Git checkout or ref configuration invalid"
                match WorkSqliteBackup.validateSnapshot bundle with
                | Ok validatedSequence when validatedSequence = sequence -> ()
                | _ -> invalidOp "Git backup input failed strict format and sequence verification"
                // Bind to the operator-configured dedicated checkout. Refuse a
                // dirty checkout except for byte-identical files prepared by
                // an interrupted publication of this exact sequence.
                let! headBranch = git [ "symbolic-ref"; "--quiet"; "--short"; "HEAD" ] cancellationToken
                if headBranch <> branch then invalidOp "Git backup checkout branch changed"
                let name = "s-" + sequence.ToString("D20", Globalization.CultureInfo.InvariantCulture)
                let relative = $"snapshots/{name}"
                let allowedFiles =
                    set [ $"{relative}/manifest.json"; $"{relative}/authority.json" ]
                let! status = git [ "status"; "--porcelain"; "--untracked-files=all" ] cancellationToken
                if status <> "" then
                    let permitted (line: string) =
                        // Interrupted materialization can leave these exact
                        // untracked or staged additions. Other dirty paths,
                        // renames, edits and deletions remain forbidden.
                        (line.StartsWith("?? ", StringComparison.Ordinal)
                         || line.StartsWith("A  ", StringComparison.Ordinal))
                        && allowedFiles.Contains(line.Substring(3).TrimEnd('\r'))
                    if not (status.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                            |> Array.forall permitted) then
                        invalidOp "Git backup checkout has unrelated changes"
                let root = Path.Combine(checkoutPath, "snapshots")
                Directory.CreateDirectory root |> ignore
                if File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint) then
                    invalidOp "Symlinked Git backup snapshot directory forbidden"
                let destination = Path.Combine(root, name)
                let staging = Path.Combine(root, ".staging-" + Guid.NewGuid().ToString("N"))
                try
                    if Directory.Exists destination then
                        if File.GetAttributes(destination).HasFlag(FileAttributes.ReparsePoint) then
                            invalidOp "Symlinked Git backup destination forbidden"
                        // Existing publish after transport timeout is only
                        // replay-safe when byte identical.
                        let names = Directory.GetFiles(destination) |> Array.map Path.GetFileName |> Set.ofArray
                        if names <> (bundle.Files.Keys |> Set.ofSeq) then
                            invalidOp "Git backup path already contains divergent files"
                        for KeyValue(path, bytes) in bundle.Files do
                            let pathOnDisk = Path.Combine(destination, path)
                            if File.GetAttributes(pathOnDisk).HasFlag(FileAttributes.ReparsePoint) then
                                invalidOp "Symlinked Git backup artifact forbidden"
                            let existing = File.ReadAllBytes(pathOnDisk)
                            if not (bytes.AsSpan().SequenceEqual(existing)) then
                                invalidOp "Git backup path contains conflicting bytes"
                    else
                        Directory.CreateDirectory staging |> ignore
                        for KeyValue(path, bytes) in bundle.Files do
                            if path <> "manifest.json" && path <> "authority.json" then
                                invalidOp "Unexpected Git backup artifact path"
                            File.WriteAllBytes(Path.Combine(staging, path), bytes)
                        Directory.Move(staging, destination)
                    let! _ = git [ "add"; "--"; relative ] cancellationToken
                    // A prior commit may have succeeded when push failed.
                    // A clean retry only pushes the exact existing head.
                    // Never commit unrelated files staged by another process.
                    let! staged = git [ "diff"; "--cached"; "--name-only"; "--"; relative ] cancellationToken
                    if staged <> "" then
                        let! _ = git [ "-c"; "user.name=Jarvis Work Backup";
                                      "-c"; "user.email=work-backup@localhost";
                                      "commit"; "--only"; "-m"; $"work-backup: sequence {sequence}";
                                      "--"; relative ] cancellationToken
                        ()
                    let! _ = git [ "push"; remote; $"HEAD:refs/heads/{branch}" ] cancellationToken
                    return ()
                finally
                    if Directory.Exists staging then Directory.Delete(staging, true)
            }
