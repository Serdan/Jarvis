namespace Client

open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks

/// A persistent lock-file pathname, not a persistent lock. The OS releases the
/// exclusive FileStream handle when its owning process exits or crashes.
/// Never delete the lock file: unlinking it could create two lock domains.
module SharedFileLock =
    let private openExclusive (lockPath: string) =
        let options = FileStreamOptions()
        options.Mode <- FileMode.OpenOrCreate
        options.Access <- FileAccess.ReadWrite
        options.Share <- FileShare.None
        if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
            options.UnixCreateMode <- UnixFileMode.UserRead ||| UnixFileMode.UserWrite
        new FileStream(lockPath, options)

    let private preparePath (lockPath: string) =
        let fullPath = Path.GetFullPath(lockPath)
        let directory = Path.GetDirectoryName(fullPath)
        let directoryWasMissing = not (Directory.Exists directory)
        Directory.CreateDirectory(directory) |> ignore
        if directoryWasMissing && (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) then
            File.SetUnixFileMode(directory, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
        if FileInfo(fullPath).LinkTarget <> null
           || (File.Exists(fullPath) && File.GetAttributes(fullPath).HasFlag(FileAttributes.ReparsePoint)) then
            invalidOp $"Jarvis lock path must not be a symbolic link: {fullPath}"
        fullPath

    let acquire (lockPath: string) (timeout: TimeSpan) =
        if timeout <= TimeSpan.Zero then invalidArg "timeout" "A positive timeout is required."
        let path = preparePath lockPath
        let watch = Stopwatch.StartNew()
        let mutable owner: FileStream = null
        while isNull owner do
            try owner <- openExclusive path
            with :? IOException when watch.Elapsed < timeout -> Thread.Sleep(20)
        owner

    let acquireAsync (lockPath: string) (timeout: TimeSpan) =
        task {
            if timeout <= TimeSpan.Zero then invalidArg "timeout" "A positive timeout is required."
            let path = preparePath lockPath
            let watch = Stopwatch.StartNew()
            let mutable owner: FileStream = null
            while isNull owner do
                try owner <- openExclusive path
                with :? IOException when watch.Elapsed < timeout ->
                    do! Task.Delay(40)
            return owner
        }
