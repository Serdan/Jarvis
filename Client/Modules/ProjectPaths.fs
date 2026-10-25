module Client.ProjectPaths

open System
open System.IO
open System.Diagnostics
open Client
open Client.Effect
open Client.IO
open FsToolkit.ErrorHandling
open Kehlet.FSharp.IO
open Kehlet.FSharp.IO.Effect.Operators

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

let private resolveRegisteredWorktree (mainRoot: string) (selector: string) : Client.Result<string> =
    if String.IsNullOrWhiteSpace selector
       || selector = "." || selector = ".."
       || selector.Contains('/') || selector.Contains('\\')
       || selector.Contains("..", StringComparison.Ordinal) then
        Error(Client.ValidationError "Invalid worktree selector.")
    else
        try
            use proc = new Process()
            proc.StartInfo.FileName <- "git"
            proc.StartInfo.WorkingDirectory <- mainRoot
            proc.StartInfo.UseShellExecute <- false
            proc.StartInfo.RedirectStandardOutput <- true
            proc.StartInfo.RedirectStandardError <- true
            proc.StartInfo.ArgumentList.Add("worktree")
            proc.StartInfo.ArgumentList.Add("list")
            proc.StartInfo.ArgumentList.Add("--porcelain")
            if not (proc.Start()) then
                Error(Client.ValidationError "Cannot inspect worktree registration.")
            else
                let output = proc.StandardOutput.ReadToEnd()
                if not (proc.WaitForExit(10000)) then
                    proc.Kill(true)
                    Error(Client.ValidationError "Git worktree discovery timed out.")
                elif proc.ExitCode <> 0 then
                    Error(Client.ValidationError "Cannot inspect worktree registration.")
                else
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

let resolveProjectRoot (projectName: string) : IO<'rt, FolderPath> =
    let separator = projectName.IndexOf("::", StringComparison.Ordinal)
    if separator < 0 then
        resolveWorkspacePath [ projectName ] >>= FileIO.parseFolder
    else
        effect {
            let mainName = projectName.Substring(0, separator)
            let selector = projectName.Substring(separator + 2)
            if String.IsNullOrWhiteSpace mainName || mainName = "." || mainName = ".."
               || mainName.Contains('/') || mainName.Contains('\\') then
                return! Client.ValidationError "Invalid project identifier." |> Effect.ofError
            else
                let! (FolderPath mainRoot) = resolveWorkspacePath [ mainName ] >>= FileIO.parseFolder
                let! path = fun _ -> resolveRegisteredWorktree mainRoot selector
                return FolderPath path
        }

let resolveProjectPath projectName relativePath : IO<'rt, string> =
    effect {
        let! (FolderPath projectRoot) = resolveProjectRoot projectName
        let! fullPath = [ relativePath ] |> Path.combineAll |> Path.combine projectRoot |> FileIO.getFullPath

        match fullPath |> isPathInRoot projectRoot with
        | true ->
            do! fun _ -> ensureNoLinksInPath projectRoot fullPath
            return fullPath
        | false -> return! Client.PermissionDenied "Path is outside project root." |> Effect.ofError
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
