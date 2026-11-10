module Client.ProjectPaths

open System
open System.IO
open System.Diagnostics
open System.Runtime.InteropServices
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
        if String.IsNullOrWhiteSpace input then
            Error(ValidationError "Project identifier cannot be empty.")
        else
            let sections = input.Split([| "::" |], StringSplitOptions.None)
            let validComponent (part: string) =
                not (String.IsNullOrWhiteSpace part)
                && part <> "." && part <> ".."
                && not (part.Contains('/')) && not (part.Contains('\\'))
                && not (part.Contains("..", StringComparison.Ordinal))
                && not (part.Contains(':'))
            match sections with
            | [| project |] when validComponent project ->
                Ok { Project = ProjectId project; Checkout = Main }
            | [| project; checkout |] when validComponent project && validComponent checkout ->
                Ok { Project = ProjectId project; Checkout = Registered(WorktreeId checkout) }
            | _ -> Error(ValidationError "Invalid project or worktree selector.")

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

// Reuse the bounded subprocess executor; this must finish even if Git exits
// but a descendant retains its stdout/stderr file descriptors.
module WorktreeGit =
    let list (executable: string) (mainRoot: string) (timeout: TimeSpan) : Client.Result<string> =
        BoundedProcess.run
            { Executable = executable
              Args = [ "worktree"; "list"; "--porcelain" ]
              WorkingDirectory = mainRoot
              Timeout = timeout
              MaxOutputBytes = Some(1024 * 1024)
              StandardInput = BoundedProcess.InheritInput
              ExtraEnvironment = []
              ConfigureStart = None }
        |> Result.bind (fun result ->
            if result.TimedOut then
                Error(ValidationError "Git worktree discovery timed out.")
            elif result.ExitCode <> 0 || result.StdOutTruncated || result.StdErrTruncated then
                Error(ValidationError "Cannot inspect worktree registration.")
            else Ok result.StdOut)

let private resolveRegisteredWorktree (mainRoot: string) (selector: string) : Client.Result<string> =
    if String.IsNullOrWhiteSpace selector
       || selector = "." || selector = ".."
       || selector.Contains('/') || selector.Contains('\\')
       || selector.Contains("..", StringComparison.Ordinal) then
        Error(Client.ValidationError "Invalid worktree selector.")
    else
        match WorktreeGit.list "git" mainRoot (TimeSpan.FromSeconds 10.) with
        | Error error -> Error error
        | Ok output ->
            try
                let comparison = if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase else StringComparison.Ordinal
                let matches =
                    output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    |> Array.choose (fun line ->
                        if line.StartsWith("worktree ", StringComparison.Ordinal) then
                            let path = Path.GetFullPath(line.Substring("worktree ".Length).TrimEnd('\r'))
                            if String.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)), selector, comparison) then Some path else None
                        else None)
                match matches with
                | [| path |] when Directory.Exists(path) ->
                    let rec containsLink (dir: DirectoryInfo) =
                        if dir = null then false
                        elif dir.Attributes.HasFlag(FileAttributes.ReparsePoint) then true
                        else containsLink dir.Parent
                    if containsLink (DirectoryInfo(path)) then
                        Error(Client.PermissionDenied "Worktree path contains a symbolic link.")
                    else Ok path
                | [||] -> Error(Client.NotFoundError "Unknown Git-registered worktree selector.")
                | _ -> Error(Client.ValidationError "Ambiguous worktree selector.")
            with ex -> Error(Client.ExceptionError ex)

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
