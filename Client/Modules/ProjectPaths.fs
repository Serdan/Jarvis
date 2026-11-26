module Client.ProjectPaths

open System
open System.Collections.Concurrent
open System.IO
open System.Diagnostics
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open Client
open Client.Effect
open Client.IO
open FsToolkit.ErrorHandling
open Kehlet.FSharp.IO
open Kehlet.FSharp.IO.Effect.Operators

// External protocol project selectors are parsed here, never used as paths.
[<StructuralEquality; StructuralComparison>]
type ProjectId = private ProjectId of string
[<StructuralEquality; StructuralComparison>]
type WorktreeId = private WorktreeId of string
type Checkout = Main | Registered of WorktreeId
type ProjectTarget = private { Project: ProjectId; Checkout: Checkout }
type ResolvedProject = private { Target: ProjectTarget; WorkspaceRoot: FolderPath; MainRoot: FolderPath; SourceRoot: FolderPath }

module ProjectTarget =
    let parse (input: string) : Client.Result<ProjectTarget> =
        match Common.ProjectSelector.parse input with
        | Some(project, None) ->
            Ok { Project = ProjectId project; Checkout = Main }
        | Some(project, Some checkout) ->
            Ok { Project = ProjectId project; Checkout = Registered(WorktreeId checkout) }
        | None when String.IsNullOrWhiteSpace input ->
            Error(ValidationError "Project identifier cannot be empty.")
        | None -> Error(ValidationError "Invalid project or worktree selector.")

    let projectId target = target.Project
    let projectName target = let (ProjectId name) = target.Project in name
    let checkout target = target.Checkout
    let worktreeName = function Registered (WorktreeId name) -> Some name | Main -> None
    let identity input = parse input |> Result.map projectName

module ProjectId =
    let value (ProjectId name) = name

module ResolvedProject =
    let target (project: ResolvedProject) = project.Target
    let sourceRoot (project: ResolvedProject) = project.SourceRoot
    let workspaceRoot (project: ResolvedProject) = project.WorkspaceRoot
    let mainRoot (project: ResolvedProject) = project.MainRoot

// One canonical directory layout for project-local skills.
module ProjectSkillPaths =
    let file (skillName: string) (fileName: string) =
        Path.Combine(".agents", "skills", skillName, fileName)

    let primaryFile (skillName: string) = file skillName "SKILL.md"

let isPathInRoot (root: string) (fullPath: string) =
    let normalize (path: string) =
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/')

    let comparison =
        if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase else StringComparison.Ordinal

    let normalizedRoot = normalize root
    let normalizedPath = normalize fullPath

    normalizedPath.Equals(normalizedRoot, comparison)
    || normalizedPath.StartsWith(normalizedRoot + "/", comparison)

let private ensureNoLinksInPath (root: string) (fullPath: string) =
    let relative = Path.GetRelativePath(root, fullPath)
    let parts =
        relative.Split(
            [| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |],
            StringSplitOptions.RemoveEmptyEntries
        )

    let rec loop current index =
        if index >= parts.Length then
            Ok()
        else
            let candidate = Path.Combine(current, parts[index])

            let linkCheck =
                try
                    let attributes = File.GetAttributes candidate
                    Ok(attributes.HasFlag FileAttributes.ReparsePoint)
                with
                | :? FileNotFoundException
                | :? DirectoryNotFoundException -> Ok false
                | ex -> Error(ExceptionError ex)

            match linkCheck with
            | Error error -> Error error
            | Ok true -> Error(PermissionDenied $"Symbolic links are not allowed in project paths: {candidate}")
            | Ok false -> loop candidate (index + 1)

    loop root 0

let resolveWorkspacePath (parts: string seq) : IO<'rt, string> =
    effect {
        let! (ProjectDirectory workspaceRoot) = ProjectIO.root
        let! fullPath = parts |> Path.combineAll |> Path.combine workspaceRoot |> FileIO.getFullPath

        match fullPath |> isPathInRoot workspaceRoot with
        | true ->
            do! fun _ -> ensureNoLinksInPath workspaceRoot fullPath
            return fullPath
        | false -> return! Client.PermissionDenied "Path is outside workspace root." |> Effect.ofError
    }

type private WorktreeCacheInvalidation() =
    let mutable invalidated = 0

    member _.Invalidate() = Interlocked.Exchange(&invalidated, 1) |> ignore
    member _.IsInvalidated = Volatile.Read(&invalidated) <> 0

type private WorktreeCacheEntry =
    { Paths: string array
      Watcher: FileSystemWatcher
      Invalidation: WorktreeCacheInvalidation
      Generation: int64 }

type internal WorktreeListing =
    { Paths: string array
      WasCacheHit: bool
      Generation: int64 }

// Reuse the bounded subprocess executor; this must finish even if Git exits
// but a descendant retains its stdout/stderr file descriptors. Cache only the
// registered paths consumed by ProjectPaths: HEAD, branch, dirty and detached
// state are deliberately not cached or used as source/ref identity.
module internal WorktreeGit =
    let private pathComparison =
        if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase else StringComparison.Ordinal

    let private pathComparer =
        if OperatingSystem.IsWindows() then StringComparer.OrdinalIgnoreCase else StringComparer.Ordinal

    let private cache = ConcurrentDictionary<string, WorktreeCacheEntry>(pathComparer)
    let private gates = ConcurrentDictionary<string, obj>(pathComparer)
    let mutable private nextGeneration = 0L

    let private cacheKey (mainRoot: string) =
        Path.TrimEndingDirectorySeparator(Path.GetFullPath mainRoot)

    // A project may itself be a linked worktree. Resolve its common Git dir
    // without spawning another Git process so the registration watcher is
    // attached to the repository-wide worktrees directory in either layout.
    let private tryCommonGitDirectory (mainRoot: string) =
        try
            let dotGit = Path.Combine(mainRoot, ".git")
            if Directory.Exists dotGit then
                let attributes = File.GetAttributes dotGit
                if attributes.HasFlag FileAttributes.ReparsePoint then None
                else Some(Path.GetFullPath dotGit)
            elif File.Exists dotGit then
                let attributes = File.GetAttributes dotGit
                if attributes.HasFlag FileAttributes.ReparsePoint then None
                else
                    let contents = File.ReadAllText dotGit |> fun value -> value.Trim()
                    if contents.StartsWith("gitdir: ", StringComparison.Ordinal) then
                        let rawGitDirectory = contents.Substring("gitdir: ".Length)
                        let gitDirectory =
                            if Path.IsPathRooted rawGitDirectory then rawGitDirectory
                            else Path.Combine(mainRoot, rawGitDirectory)
                        let gitDirectory = Path.GetFullPath gitDirectory
                        let commonDirectoryFile = Path.Combine(gitDirectory, "commondir")
                        if File.Exists commonDirectoryFile then
                            let rawCommonDirectory = File.ReadAllText commonDirectoryFile |> fun value -> value.Trim()
                            let commonDirectory =
                                if Path.IsPathRooted rawCommonDirectory then rawCommonDirectory
                                else Path.Combine(gitDirectory, rawCommonDirectory)
                            Some(Path.GetFullPath commonDirectory)
                        else Some gitDirectory
                    else None
            else None
        with _ -> None

    // A cached path is only a hint. Verify that its .git pointer still refers
    // to the exact registered worktree and that Git's reverse pointer agrees.
    // This closes the FileSystemWatcher delivery window for moves/replacements
    // without spawning Git or caching HEAD, branch, or dirty state.
    let isRegisteredWorktreePath (mainRoot: string) (worktreePath: string) =
        let pathEquals (left: string) (right: string) =
            String.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath left),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath right),
                pathComparison
            )

        try
            match tryCommonGitDirectory mainRoot with
            | None -> false
            | Some commonGitDirectory ->
                let dotGit = Path.Combine(worktreePath, ".git")
                let dotGitAttributes = File.GetAttributes dotGit
                if dotGitAttributes.HasFlag FileAttributes.ReparsePoint then false
                elif dotGitAttributes.HasFlag FileAttributes.Directory then
                    pathEquals dotGit commonGitDirectory
                else
                    let pointer = File.ReadAllText dotGit |> fun value -> value.Trim()
                    if not (pointer.StartsWith("gitdir: ", StringComparison.Ordinal)) then false
                    else
                        let rawGitDirectory = pointer.Substring("gitdir: ".Length)
                        let gitDirectory =
                            if Path.IsPathRooted rawGitDirectory then rawGitDirectory
                            else Path.Combine(worktreePath, rawGitDirectory)
                        let gitDirectory = Path.GetFullPath gitDirectory
                        let registryDirectory = Path.Combine(commonGitDirectory, "worktrees")
                        let registryAttributes = File.GetAttributes registryDirectory
                        let gitDirectoryAttributes = File.GetAttributes gitDirectory
                        let commonDirectoryFile = Path.Combine(gitDirectory, "commondir")
                        let commonDirectoryAttributes = File.GetAttributes commonDirectoryFile
                        let reversePointerFile = Path.Combine(gitDirectory, "gitdir")
                        let reversePointerAttributes = File.GetAttributes reversePointerFile

                        if registryAttributes.HasFlag FileAttributes.ReparsePoint
                           || gitDirectoryAttributes.HasFlag FileAttributes.ReparsePoint
                           || commonDirectoryAttributes.HasFlag FileAttributes.ReparsePoint
                           || reversePointerAttributes.HasFlag FileAttributes.ReparsePoint
                           || not (pathEquals (Path.GetDirectoryName gitDirectory) registryDirectory) then
                            false
                        else
                            let rawCommonDirectory = File.ReadAllText commonDirectoryFile |> fun value -> value.Trim()
                            let worktreeCommonDirectory =
                                if Path.IsPathRooted rawCommonDirectory then rawCommonDirectory
                                else Path.Combine(gitDirectory, rawCommonDirectory)
                            let rawReversePointer = File.ReadAllText reversePointerFile |> fun value -> value.Trim()
                            let reversePointer =
                                if Path.IsPathRooted rawReversePointer then rawReversePointer
                                else Path.Combine(gitDirectory, rawReversePointer)
                            pathEquals worktreeCommonDirectory commonGitDirectory
                            && pathEquals reversePointer dotGit
        with _ -> false

    let private tryCreateWatcher (mainRoot: string) =
        try
            match tryCommonGitDirectory mainRoot with
            | None -> None
            | Some commonGitDirectory ->
                let registryDirectory = Path.Combine(commonGitDirectory, "worktrees")
                if not (Directory.Exists registryDirectory) then None
                else
                    let attributes = File.GetAttributes registryDirectory
                    if attributes.HasFlag FileAttributes.ReparsePoint then None
                    else
                        let invalidation = WorktreeCacheInvalidation()
                        let watcher = new FileSystemWatcher(registryDirectory)
                        try
                            watcher.IncludeSubdirectories <- true
                            watcher.NotifyFilter <-
                                NotifyFilters.FileName ||| NotifyFilters.DirectoryName ||| NotifyFilters.LastWrite
                            watcher.InternalBufferSize <- 32 * 1024

                            let isRegistrationPath (path: string) =
                                try
                                    let fullPath = Path.GetFullPath path
                                    let name = Path.GetFileName fullPath
                                    let parent = Path.GetDirectoryName fullPath
                                    let isDirectChild =
                                        not (String.IsNullOrEmpty parent)
                                        && String.Equals(
                                            Path.TrimEndingDirectorySeparator parent,
                                            Path.TrimEndingDirectorySeparator registryDirectory,
                                            pathComparison
                                        )
                                    isDirectChild
                                    || String.Equals(name, "gitdir", StringComparison.Ordinal)
                                    || String.Equals(name, "commondir", StringComparison.Ordinal)
                                with _ -> true

                            let isMetadataPointer (path: string) =
                                let name = Path.GetFileName path
                                String.Equals(name, "gitdir", StringComparison.Ordinal)
                                || String.Equals(name, "commondir", StringComparison.Ordinal)

                            // HEAD/index/locked changes do not alter path membership. In
                            // particular, commits and detach operations must not turn
                            // ordinary path resolution back into a Git subprocess.
                            watcher.Changed.Add(fun event ->
                                if isMetadataPointer event.FullPath then invalidation.Invalidate())
                            watcher.Created.Add(fun event ->
                                if isRegistrationPath event.FullPath then invalidation.Invalidate())
                            watcher.Deleted.Add(fun event ->
                                if isRegistrationPath event.FullPath then invalidation.Invalidate())
                            watcher.Renamed.Add(fun event ->
                                if isRegistrationPath event.FullPath || isRegistrationPath event.OldFullPath then
                                    invalidation.Invalidate())
                            watcher.Error.Add(fun _ -> invalidation.Invalidate())
                            watcher.EnableRaisingEvents <- true
                            Some(watcher, invalidation)
                        with _ ->
                            try watcher.Dispose() with _ -> ()
                            None
        with _ -> None

    let private disposeWatcher (watcher: FileSystemWatcher) =
        try watcher.Dispose() with _ -> ()

    let private removeCache key =
        let mutable removed = Unchecked.defaultof<WorktreeCacheEntry>
        if cache.TryRemove(key, &removed) then disposeWatcher removed.Watcher

    let private tryCached key =
        match cache.TryGetValue key with
        | true, entry when not entry.Invalidation.IsInvalidated ->
            Some
                { Paths = entry.Paths
                  WasCacheHit = true
                  Generation = entry.Generation }
        | _ -> None

    let private parsePaths (output: string) : Client.Result<string array> =
        try
            let paths =
                output.Split('\000', StringSplitOptions.RemoveEmptyEntries)
                |> Array.choose (fun field ->
                    if field.StartsWith("worktree ", StringComparison.Ordinal) then
                        let path = field.Substring("worktree ".Length)
                        if String.IsNullOrWhiteSpace path || not (Path.IsPathFullyQualified path) then
                            raise (InvalidDataException "Git returned an invalid worktree path.")
                        Some(Path.GetFullPath path)
                    else None)
            Ok paths
        with ex -> Error(ExceptionError ex)

    let private discover executable mainRoot timeout key =
        let rec run attempt =
            let watcher = tryCreateWatcher mainRoot
            let result =
                BoundedProcess.run
                    { Executable = executable
                      Args = [ "worktree"; "list"; "--porcelain"; "-z" ]
                      WorkingDirectory = mainRoot
                      Timeout = timeout
                      MaxOutputBytes = Some(1024 * 1024)
                      StandardInput = BoundedProcess.InheritInput
                      ExtraEnvironment = []
                      ConfigureStart = None }
                |> Result.bind (fun outcome ->
                    if outcome.TimedOut then
                        Error(ValidationError "Git worktree discovery timed out.")
                    elif outcome.ExitCode <> 0 || outcome.StdOutTruncated || outcome.StdErrTruncated then
                        Error(ValidationError "Cannot inspect worktree registration.")
                    else parsePaths outcome.StdOut)

            match result with
            | Error error ->
                watcher |> Option.iter (fun (instance, _) -> disposeWatcher instance)
                Error error
            | Ok paths ->
                match watcher with
                | Some(instance, invalidation) when invalidation.IsInvalidated ->
                    disposeWatcher instance
                    if attempt = 0 then run 1
                    else Error(ValidationError "Git worktree registration changed during discovery.")
                | Some(instance, invalidation) ->
                    let generation = Interlocked.Increment(&nextGeneration)
                    let entry =
                        { Paths = paths
                          Watcher = instance
                          Invalidation = invalidation
                          Generation = generation }
                    cache[key] <- entry
                    if invalidation.IsInvalidated then
                        removeCache key
                        if attempt = 0 then run 1
                        else Error(ValidationError "Git worktree registration changed during discovery.")
                    else
                        Ok { Paths = paths; WasCacheHit = false; Generation = generation }
                | None -> Ok { Paths = paths; WasCacheHit = false; Generation = 0L }

        run 0

    let list (executable: string) (mainRoot: string) (timeout: TimeSpan) : Client.Result<WorktreeListing> =
        try
            let key = cacheKey mainRoot
            match tryCached key with
            | Some listing -> Ok listing
            | None ->
                let gate = gates.GetOrAdd(key, fun _ -> obj())
                lock gate (fun () ->
                    match tryCached key with
                    | Some listing -> Ok listing
                    | None ->
                        removeCache key
                        discover executable mainRoot timeout key)
        with ex -> Error(ExceptionError ex)

    let isCurrent (mainRoot: string) (listing: WorktreeListing) =
        if listing.Generation = 0L then true
        else
            try
                let key = cacheKey mainRoot
                match cache.TryGetValue key with
                | true, entry ->
                    entry.Generation = listing.Generation && not entry.Invalidation.IsInvalidated
                | _ -> false
            with _ -> false

    let invalidate (mainRoot: string) (listing: WorktreeListing) =
        if listing.Generation <> 0L then
            try
                let key = cacheKey mainRoot
                match cache.TryGetValue key with
                | true, entry when entry.Generation = listing.Generation -> entry.Invalidation.Invalidate()
                | _ -> ()
            with _ -> ()

let internal resolveRegisteredWorktreeWith (executable: string) (mainRoot: string) (selector: string) : Client.Result<string> =
    if String.IsNullOrWhiteSpace selector
       || selector = "." || selector = ".."
       || selector.Contains('/') || selector.Contains('\\')
       || selector.Contains("..", StringComparison.Ordinal) then
        Error(Client.ValidationError "Invalid worktree selector.")
    else
        let resolve (listing: WorktreeListing) =
            try
                let comparison = if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase else StringComparison.Ordinal
                let matches =
                    listing.Paths
                    |> Array.filter (fun path ->
                        String.Equals(
                            Path.GetFileName(Path.TrimEndingDirectorySeparator(path)),
                            selector,
                            comparison
                        ))
                match matches with
                | [| path |] ->
                    if not (Directory.Exists path) then
                        Error(Client.NotFoundError "Registered worktree path no longer exists.")
                    else
                        let rec containsLink (dir: DirectoryInfo) =
                            if dir = null then false
                            elif dir.Attributes.HasFlag(FileAttributes.ReparsePoint) then true
                            else containsLink dir.Parent
                        if containsLink (DirectoryInfo(path)) then
                            Error(Client.PermissionDenied "Worktree path contains a symbolic link.")
                        elif not (WorktreeGit.isRegisteredWorktreePath mainRoot path) then
                            Error(Client.ValidationError "Git worktree registration changed during resolution.")
                        elif not (WorktreeGit.isCurrent mainRoot listing) then
                            Error(Client.ValidationError "Git worktree registration changed during resolution.")
                        else Ok path
                | [||] -> Error(Client.NotFoundError "Unknown Git-registered worktree selector.")
                | _ -> Error(Client.ValidationError "Ambiguous worktree selector.")
            with ex -> Error(Client.ExceptionError ex)

        match WorktreeGit.list executable mainRoot (TimeSpan.FromSeconds 10.) with
        | Error error -> Error error
        | Ok listing ->
            match resolve listing with
            | Ok path -> Ok path
            | Error error when listing.WasCacheHit || not (WorktreeGit.isCurrent mainRoot listing) ->
                // A cache hit is only a hint. Re-discover when the selector is
                // absent/ambiguous or its registered path no longer validates;
                // a failed refresh never falls back to stale cached paths.
                WorktreeGit.invalidate mainRoot listing
                match WorktreeGit.list executable mainRoot (TimeSpan.FromSeconds 10.) with
                | Error refreshError -> Error refreshError
                | Ok refreshed ->
                    match resolve refreshed with
                    | Error _ when not (WorktreeGit.isCurrent mainRoot refreshed) ->
                        Error(Client.ValidationError "Git worktree registration changed during resolution.")
                    | result -> result
            | Error error -> Error error

let private resolveRegisteredWorktree (mainRoot: string) (selector: string) : Client.Result<string> =
    resolveRegisteredWorktreeWith "git" mainRoot selector

let resolveProjectContextForTarget (target: ProjectTarget) : IO<'rt, ResolvedProject> =
    effect {
        let! (ProjectDirectory workspaceRoot) = ProjectIO.root
        let mainName = ProjectTarget.projectName target
        let! (FolderPath mainRoot) = resolveWorkspacePath [ mainName ] >>= FileIO.parseFolder
        let! (FolderPath sourceRoot) =
            match target.Checkout with
            | Main -> fun _ -> Ok(FolderPath mainRoot)
            | Registered worktree ->
                let (WorktreeId name) = worktree
                fun _ -> resolveRegisteredWorktree mainRoot name |> Result.map FolderPath
        return
            { Target = target
              WorkspaceRoot = FolderPath workspaceRoot
              MainRoot = FolderPath mainRoot
              SourceRoot = FolderPath sourceRoot }
    }

let resolveProjectContext (projectName: string) : IO<'rt, ResolvedProject> =
    effect {
        let! target = ProjectTarget.parse projectName |> Effect.ofResult
        return! resolveProjectContextForTarget target
    }

let resolveProjectRoot (projectName: string) : IO<'rt, FolderPath> =
    resolveProjectContext projectName |>> fun resolved -> resolved.SourceRoot

let resolveSourcePath (project: ResolvedProject) (relativePath: string) : IO<'rt, string> =
    effect {
        if Path.IsPathRooted relativePath
           || relativePath.Split([| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |],
                                 StringSplitOptions.RemoveEmptyEntries)
               |> Array.exists (fun part -> part = "..") then
            return! Client.PermissionDenied "Source path must be checkout-relative." |> Effect.ofError
        else
            let (FolderPath root) = project.SourceRoot
            let! fullPath = [ relativePath ] |> Path.combineAll |> Path.combine root |> FileIO.getFullPath
            if isPathInRoot root fullPath then
                do! fun _ -> ensureNoLinksInPath root fullPath
                return fullPath
            else
                return! Client.PermissionDenied "Source path is outside checkout root." |> Effect.ofError
    }

let resolveProjectPath projectName relativePath : IO<'rt, string> =
    effect {
        let! context = resolveProjectContext projectName
        return! resolveSourcePath context relativePath
    }

let resolveProjectFolder projectName relativePath : IO<'rt, FolderPath> =
    resolveProjectPath projectName relativePath >>= FileIO.parseFolder

let resolveProjectFile projectName relativePath : IO<'rt, FilePath> =
    resolveProjectPath projectName relativePath >>= FileIO.parseFile

let resolveWritableProjectFile projectName relativePath : IO<'rt, FilePath> =
    resolveProjectPath projectName relativePath |>> FilePath

let validateProjectRelativePath path =
    if String.IsNullOrWhiteSpace path then
        Error(Client.ValidationError "Path cannot be empty.")
    elif Path.IsPathRooted path then
        Error(Client.PermissionDenied $"Path must be project-relative: {path}")
    elif path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) |> Array.exists ((=) "..") then
        Error(Client.PermissionDenied $"Path cannot escape project root: {path}")
    elif path.StartsWith("-", StringComparison.Ordinal) then
        Error(Client.ValidationError $"Path cannot start with '-': {path}")
    else
        Ok path
