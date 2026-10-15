module Client.AgentInstructions

open System
open System.IO
open System.Security.Cryptography
open System.Text
open Common
open Client
open Client.Effect

type private Target =
    | FolderTarget of string
    | FileTarget of string
    | ExistingTarget of string

let private limits = 16 * 1024, 32 * 1024, 16
let private utf8 = UTF8Encoding(false, true)

let private applicableTargets (rt: Runtime) command =
    match command with
    | ListCommandsCommand
    | ListProjectsCommand
    | UpdateClientCommand
    | GetJobResultCommand _
    | CancelJobCommand _
    | MessageCommand _
    | GetClientActivityCommand _ -> Ok None
    | ListSkillsCommand c when c.ProjectName = "@global" -> Ok None
    | GetSkillCommand c when c.ProjectName = "@global" -> Ok None
    | CreateSkillCommand c when c.ProjectName = "@global" -> Ok None
    | _ ->
        match AgentCommandInfo.projectName command with
        | None -> Ok None
        | Some projectName ->
            let roots = [ FolderTarget "" ]
            let files paths = paths |> List.map FileTarget
            let directories paths = paths |> List.map FolderTarget
            let targets =
                match command with
                | ListDirectoryCommand c -> Ok(directories [ c.FolderPath ])
                | SearchFilesCommand c -> Ok(directories [ defaultArg c.FolderPath "" ])
                | SearchTextCommand c -> Ok(directories [ defaultArg c.FolderPath "" ])
                | ReadFileCommand c -> Ok(files [ c.FilePath ])
                | ReadFilesCommand c -> Ok(files c.FilePaths)
                | ReadImageCommand c -> Ok(files [ c.FilePath ])
                | ExportFileCommand c -> Ok(files [ c.FilePath ])
                | ImportFileCommand c -> Ok(files [ c.FilePath ])
                | WriteFileCommand c -> Ok(files [ c.FilePath ])
                | PatchFileCommand c -> Ok(files [ c.FilePath ])
                | RunCommandCommand c -> Ok(directories [ defaultArg c.WorkingDirectory "" ])
                | StartJobCommand c -> Ok(directories [ defaultArg c.WorkingDirectory "" ])
                | AttachWorkTaskCommand c -> Ok(files [ c.TaskPath ])
                | RunProjectTaskCommand c ->
                    ClientShell.resolveProjectTask c rt
                    |> Result.map (fun definition -> directories [ defaultArg definition.WorkingDirectory "" ])
                | GitCommitCommand c -> Ok(c.Paths |> List.map ExistingTarget)
                | GetGitDiffCommand c -> Ok [ ExistingTarget(defaultArg c.Path "") ]
                | GetSkillCommand c ->
                    Ok(files [ Path.Combine(".jarvis", "skills", c.SkillName, "SKILL.md") ])
                | CreateSkillCommand c ->
                    Ok(files [ Path.Combine(".jarvis", "skills", c.SkillName, "SKILL.md") ])
                | _ -> Ok roots
            targets |> Result.map (fun resolved -> Some(projectName, if resolved.IsEmpty then roots else resolved))

let private targetDirectory rt projectName (projectRoot: string) target =
    let relative, isFolder =
        match target with
        | FolderTarget path -> path, true
        | FileTarget path -> path, false
        | ExistingTarget path ->
            path, true
    if String.IsNullOrWhiteSpace relative then Ok ""
    else
        match ProjectPaths.validateProjectRelativePath relative with
        | Error error -> Error error
        | Ok valid ->
            match ProjectPaths.resolveProjectPath projectName valid rt with
            | Error error -> Error error
            | Ok resolved ->
                let isFolder =
                    match target with ExistingTarget _ -> Directory.Exists resolved | _ -> isFolder
                let directory = if isFolder then resolved else Path.GetDirectoryName resolved
                let normalized = Path.GetRelativePath(projectRoot, directory)
                if normalized = "." then Ok ""
                else Ok(normalized.Replace('\\', '/'))

let private ancestors (directory: string) =
    let pieces = directory.Split('/', StringSplitOptions.RemoveEmptyEntries)
    [ yield ""
      for depth in 1 .. pieces.Length do
          yield pieces |> Seq.take depth |> String.concat "/" ]

let private readDocument rt scope (relativePath: string) =
    let maxFileBytes, _, _ = limits
    let parts = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
    let resolved = ProjectPaths.resolveWorkspacePath parts rt
    match resolved with
    | Error error -> Error error
    | Ok path ->
        try
            let exactlyNamed =
                Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(path))
                |> Seq.exists (fun candidate -> String.Equals(Path.GetFileName(candidate), "AGENTS.md", StringComparison.Ordinal))
            if not exactlyNamed then raise (FileNotFoundException())
            let attributes = File.GetAttributes path
            if attributes.HasFlag FileAttributes.Directory then
                Error(Client.ValidationError $"Instruction source {relativePath} is a directory.")
            elif attributes.HasFlag FileAttributes.ReparsePoint then
                Error(Client.PermissionDenied $"Instruction source {relativePath} is a link.")
            else
                let size = FileInfo(path).Length
                if size > int64 maxFileBytes then
                    Error(Client.ValidationError $"Instruction source {relativePath} exceeds {maxFileBytes} bytes.")
                else
                    let bytes = File.ReadAllBytes path
                    if bytes.Length > maxFileBytes then
                        Error(Client.ValidationError $"Instruction source {relativePath} exceeds {maxFileBytes} bytes.")
                    else
                        let content = utf8.GetString bytes
                        if String.IsNullOrEmpty content then Ok None
                        else
                            let digest = SHA256.HashData bytes |> Convert.ToHexString |> fun s -> s.ToLowerInvariant()
                            Ok(Some { Scope = scope; Path = relativePath; Sha256 = "sha256:" + digest; Content = content })
        with
        | :? FileNotFoundException
        | :? DirectoryNotFoundException -> Ok None
        | :? DecoderFallbackException ->
            Error(Client.ValidationError $"Instruction source {relativePath} is not valid UTF-8.")
        | ex -> Error(Client.ValidationError $"Cannot read instruction source {relativePath}: {ex.Message}")

let resolve (rt: Runtime) command =
    match applicableTargets rt command with
    | Error error -> Error error
    | Ok None -> Ok []
    | Ok(Some(projectName, targets)) ->
        match ProjectPaths.resolveProjectRoot projectName rt with
        | Error error -> Error error
        | Ok(FolderPath projectRoot) ->
            let directories =
                targets
                |> List.fold (fun accumulated target ->
                    accumulated |> Result.bind (fun dirs ->
                        targetDirectory rt projectName projectRoot target |> Result.map (fun dir -> dir :: dirs))) (Ok [])
            match directories with
            | Error error -> Error error
            | Ok dirs ->
                let _, maxTotalBytes, maxDocuments = limits
                let candidates =
                    dirs
                    |> List.collect ancestors
                    |> List.distinct
                    |> List.sortBy (fun dir -> dir.Split('/', StringSplitOptions.RemoveEmptyEntries).Length, dir)
                    |> List.map (fun dir ->
                        "project", projectName + "/" + (if dir = "" then "" else dir + "/") + "AGENTS.md")
                let candidates = ("workspace", "AGENTS.md") :: candidates
                let rec collect pending docs total =
                    match pending with
                    | [] -> Ok(List.rev docs)
                    | (scope, path) :: tail ->
                        match readDocument rt scope path with
                        | Error error -> Error error
                        | Ok None -> collect tail docs total
                        | Ok(Some doc) ->
                            let count = List.length docs + 1
                            let bytes = Encoding.UTF8.GetByteCount doc.Content
                            if count > maxDocuments then
                                Error(Client.ValidationError $"Instruction source {path} exceeds the {maxDocuments}-document limit.")
                            elif total + bytes > maxTotalBytes then
                                Error(Client.ValidationError $"Instruction source {path} exceeds the {maxTotalBytes}-byte combined limit.")
                            else collect tail (doc :: docs) (total + bytes)
                collect candidates [] 0
