module Client.IO.AtomicTextFiles

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
open Client

// All cooperating Jarvis client processes acquire the same stable lock for a
// canonical filesystem path. Locks deliberately live outside the Git worktree
// and are never unlinked (unlinking would allow two live lock inodes).
let private lockDirectory () =
    let root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
    let directory = Path.Combine(root, "Jarvis", "file-edit-locks")
    if DirectoryInfo(directory).LinkTarget <> null then
        raise (UnauthorizedAccessException "The Jarvis file-lock directory is a symbolic link.")
    Directory.CreateDirectory(directory) |> ignore
    if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
        File.SetUnixFileMode(
            directory,
            UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
    directory

let private lockKey (path: string) =
    let key =
        if OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() then path.ToUpperInvariant()
        else path
    SHA256.HashData(Encoding.UTF8.GetBytes key)
    |> Convert.ToHexString
    |> fun hash -> hash.ToLowerInvariant() + ".lock"

let private acquireLock path =
    let lockPath = Path.Combine(lockDirectory(), lockKey path)
    let deadline = Stopwatch.StartNew()
    let rec acquire () =
        if FileInfo(lockPath).LinkTarget <> null then
            raise (UnauthorizedAccessException "The file-mutation lock path is a symbolic link.")
        try
            let stream = new FileStream(lockPath, FileMode.OpenOrCreate,
                                        FileAccess.ReadWrite, FileShare.None)
            try
                if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
                    File.SetUnixFileMode(lockPath, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
                stream
            with _ ->
                // The exclusive OS handle must never survive an unsuccessful
                // lock-file mode check.
                stream.Dispose()
                reraise()
        with :? IOException when deadline.Elapsed < TimeSpan.FromSeconds(30.) ->
            Thread.Sleep 25
            acquire ()
    acquire ()

let private isUnderRoot (root: string) (target: string) =
    let comparison =
        if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase
        else StringComparison.Ordinal
    let root = Path.TrimEndingDirectorySeparator root
    target.StartsWith(root + string Path.DirectorySeparatorChar, comparison)

// Never use the original checkout-relative resolution as the sole guard:
// parent directories may have been created or replaced since resolution.
let private validateAndPrepareParents (root: string) (target: string) createParents allowMissing =
    if not (isUnderRoot root target) then
        raise (UnauthorizedAccessException "File mutation must stay inside the selected checkout.")
    if DirectoryInfo(root).LinkTarget <> null then
        raise (UnauthorizedAccessException "Checkout root must not be a symbolic link.")
    if not (Directory.Exists root) then
        raise (DirectoryNotFoundException "Checkout root no longer exists.")
    let parent = Path.GetDirectoryName target
    let relative = Path.GetRelativePath(root, parent)
    let parts =
        if relative = "." then [||]
        else relative.Split(
            [| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |],
            StringSplitOptions.RemoveEmptyEntries)
    let mutable here = root
    for part in parts do
        here <- Path.Combine(here, part)
        if DirectoryInfo(here).LinkTarget <> null
           || FileInfo(here).LinkTarget <> null then
            raise (UnauthorizedAccessException $"Symbolic-link parent is forbidden: {here}")
        if Directory.Exists here then ()
        elif File.Exists here then
            raise (IOException $"File occupies destination parent directory: {here}")
        elif createParents then
            Directory.CreateDirectory(here) |> ignore
        elif allowMissing then ()
        else
            raise (DirectoryNotFoundException $"Parent directory does not exist: {here}")
        if DirectoryInfo(here).LinkTarget <> null then
            raise (UnauthorizedAccessException $"Symbolic-link parent is forbidden: {here}")
    if FileInfo(target).LinkTarget <> null
       || DirectoryInfo(target).LinkTarget <> null then
        raise (UnauthorizedAccessException "Destination symbolic links are forbidden.")
    if Directory.Exists target then
        raise (IOException "Destination is a directory.")

let private currentContent path =
    if File.Exists path then Some(File.ReadAllText path)
    else None

let private currentUnchanged target initial =
    match currentContent target, initial with
    | Some value, Some original -> String.Equals(value, original, StringComparison.Ordinal)
    | None, None -> true
    | _ -> false

// The transaction holds its cross-process lock from snapshot read through
// publication. Staging is on the destination filesystem and never truncates
// the current file. A rename is atomic but is not a power-failure directory
// fsync guarantee; unrelated non-cooperating native writers remain outside
// Jarvis's advisory locking contract.
let internal editWithBeforePublish (beforePublish: unit -> unit)
    (FilePath requestedPath) (options: TextEditOptions)
    (plan: string option -> Result<TextFileChange * 'T>) : Result<'T> =
    try
        let (FolderPath requestedRoot) = options.SourceRoot
        let root = Path.GetFullPath requestedRoot
        let target = Path.GetFullPath requestedPath
        use _gate = acquireLock target
        // Failed preconditions must not leave newly created parent directories.
        // Validate the existing path first, but only create parents after the
        // edit plan has accepted the observed snapshot.
        validateAndPrepareParents root target false options.CreateParents
        let before = currentContent target
        match plan before with
        | Error problem -> Error problem
        | Ok (_, response) when options.DryRun -> Ok response
        | Ok (change, response) ->
            let newText =
                match change with
                | ReplaceText text | AppendUtf8Text text -> text
            if isNull newText then
                Error(ValidationError "File mutation generated null content.")
            else
                // Preserve the old write/append policy for existing read-only
                // files. A directory rename can succeed even when the
                // original inode itself cannot be opened for writing.
                match before with
                | Some _ ->
                    use permissionProbe =
                        new FileStream(target, FileMode.Open, FileAccess.Write,
                                       FileShare.ReadWrite ||| FileShare.Delete)
                    ()
                | None -> ()
                validateAndPrepareParents root target options.CreateParents false
                let parent = Path.GetDirectoryName target
                let stage =
                    Path.Combine(parent, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.jarvis-tmp")
                try
                    let mutable defaultCreationMode = None
                    do
                        use output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                        if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
                            // Never expose the file's content with the
                            // destination's potentially broad access bits
                            // during staging. Restore the requested mode
                            // only after data has been flushed.
                            defaultCreationMode <- Some(File.GetUnixFileMode stage)
                            File.SetUnixFileMode(stage, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
                        use writer = new StreamWriter(output, new UTF8Encoding(false))
                        match change with
                        | ReplaceText replacement -> writer.Write(replacement)
                        | AppendUtf8Text suffix ->
                            // File.AppendAllText historically appends UTF-8 bytes
                            // without changing the existing bytes or BOM. Keep
                            // that exact behavior in the atomically staged file.
                            if File.Exists target then
                                use original = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read)
                                original.CopyTo(output)
                            writer.Write(suffix)
                        writer.Flush()
                        output.Flush(true)

                    // A replacement changes the directory entry, not any
                    // outside hard-link target that shares the old inode.
                    if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
                        match before, defaultCreationMode with
                        | Some _, _ -> File.SetUnixFileMode(stage, File.GetUnixFileMode(target))
                        | None, Some mode -> File.SetUnixFileMode(stage, mode)
                        | None, None -> ()

                    beforePublish ()
                    validateAndPrepareParents root target false false
                    if not (currentUnchanged target before) then
                        Error(ValidationError "Destination changed during file mutation.")
                    else
                        match before with
                        | None -> File.Move(stage, target, false)
                        | Some _ when OperatingSystem.IsWindows() ->
                            File.Replace(stage, target, null)
                        | Some _ ->
                            File.Move(stage, target, true)
                        Ok response
                finally
                    if File.Exists stage then File.Delete stage
    with
    | :? UnauthorizedAccessException as ex -> Error(PermissionDenied ex.Message)
    | :? IOException as ex -> Error(ExceptionError ex)
    | ex -> Error(ExceptionError ex)


// The testable pre-publication hook is internal. Production callers have no
// additional operations between staging and destination revalidation.
let edit path options plan =
    editWithBeforePublish ignore path options plan
