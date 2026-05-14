module Client.ProjectPaths

open System
open System.IO
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

let resolveWorkspacePath (parts: string seq) : IO<'rt, string> =
    effect {
        let! (ProjectDirectory workspaceRoot) = ProjectIO.root
        let! fullPath = parts |> Path.combineAll |> Path.combine workspaceRoot |> FileIO.getFullPath

        match fullPath |> isPathInRoot workspaceRoot with
        | true -> return fullPath
        | false -> return! Client.PermissionDenied "Path is outside workspace root." |> Effect.ofError
    }

let resolveProjectRoot projectName : IO<'rt, FolderPath> =
    resolveWorkspacePath [ projectName ] >>= FileIO.parseFolder

let resolveProjectPath projectName relativePath : IO<'rt, string> =
    effect {
        let! (FolderPath projectRoot) = resolveProjectRoot projectName
        let! fullPath = [ relativePath ] |> Path.combineAll |> Path.combine projectRoot |> FileIO.getFullPath

        match fullPath |> isPathInRoot projectRoot with
        | true -> return fullPath
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
