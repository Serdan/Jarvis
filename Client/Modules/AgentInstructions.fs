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

// The source document may be arbitrarily large; only the delivered context
// is bounded. Reserve space for specific (deeper) instructions and tool results.
let private maxFileContentBytes = 64 * 1024
let private maxTotalContentBytes = 128 * 1024
let private maxDocuments = 32
let private writeWarningBytes = 32 * 1024
let private utf8 = UTF8Encoding(false, true)

let private applicableTargets (rt: Runtime) command =
    match command with
    // Orchestration ledger operations are independent of repository source
    // instructions. In particular, a malformed or long AGENTS.md must never
    // prevent an agent from checkpointing, yielding or acknowledging work.
    | V2TaskReadCommand _
    | V2LedgerReadCommand _
    | V2LedgerWriteCommand _
    | V2ReviewReadCommand _
    | V2ReviewWriteCommand _ -> Ok None
    // These actions modify only the independently versioned Git ledger.
    // ExportTaskSpec and ImportTaskSpec touch source-tree files and therefore
    // retain normal source instruction validation.
    | V2TaskWriteCommand cmd when cmd.Action <> "ExportTaskSpec"
                                  && cmd.Action <> "ImportTaskSpec" -> Ok None
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

let private utf8Prefix (bytes: byte[]) length =
    let rec take count =
        if count = 0 then ""
        else
            try utf8.GetString(bytes, 0, count)
            with :? DecoderFallbackException -> take (count - 1)
    take length

let private readDocument rt scope (relativePath: string) (budget: int) =
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
                use stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
                let originalBytes = stream.Length
                let digest = SHA256.HashData(stream) |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()
                // Validate the entire source even when only its prefix is delivered:
                // malformed bytes in the omitted suffix must not masquerade as good UTF-8.
                stream.Position <- 0L
                use reader = new StreamReader(stream, utf8, false, 8192, true)
                let scanBuffer = Array.zeroCreate<char> 8192
                while reader.Read(scanBuffer, 0, scanBuffer.Length) > 0 do ()
                stream.Position <- 0L
                let limit = min maxFileContentBytes (max 0 budget)
                let buffer = Array.zeroCreate<byte> (int (min originalBytes (int64 limit)))
                stream.ReadExactly(buffer)
                let content = utf8Prefix buffer buffer.Length
                let delivered = Encoding.UTF8.GetByteCount content
                // Fully delivered files must be valid UTF-8; invalid un-delivered
                // bytes are not silently represented as complete instructions.
                if originalBytes <= int64 limit && delivered <> int originalBytes then
                    Error(Client.ValidationError $"Instruction source {relativePath} is not valid UTF-8.")
                elif originalBytes = 0L then Ok None
                else
                    let truncated = int64 delivered < originalBytes
                    let body =
                        if truncated then
                            $"[INCOMPLETE REPOSITORY INSTRUCTIONS — TRUNCATED]\nSource: {relativePath}\nDelivered: {delivered} of {originalBytes} UTF-8 bytes. Full SHA-256: sha256:{digest}.\nThe omitted instructions have NOT been loaded. Use read_file on this path to retrieve the complete file before relying on omitted guidance.\n[/INCOMPLETE REPOSITORY INSTRUCTIONS]\n\n" + content
                        else content
                    Ok(Some { Scope = scope; Path = relativePath; Sha256 = "sha256:" + digest; Content = body })
        with
        | :? FileNotFoundException
        | :? DirectoryNotFoundException -> Ok None
        | :? DecoderFallbackException ->
            Error(Client.ValidationError $"Instruction source {relativePath} is not valid UTF-8.")
        | ex -> Error(Client.ValidationError $"Cannot read instruction source {relativePath}: {ex.Message}")

// Successful writes to oversized instruction files are allowed, but the agent
// must see a clear warning immediately rather than discovering it by accident.
let writeWarnings (rt: Runtime) command response =
    // Advisory errors must never replace a successful source mutation.
    let inspect projectName filePath =
        try
            match ProjectPaths.resolveProjectPath projectName filePath rt with
            | Ok path when File.Exists path ->
                let length = FileInfo(path).Length
                if length > int64 writeWarningBytes then
                    let content =
                        $"WARNING: {projectName}/{filePath} is {length} bytes after this write (recommended maximum {writeWarningBytes} bytes / 32 KiB). Consider extracting detailed guidance into referenced docs and keeping AGENTS.md concise. The write succeeded; this is a warning, not an error."
                    [ { Scope = "write-warning"; Path = projectName + "/" + filePath
                        Sha256 = ""; Content = content } ]
                else []
            | _ -> []
        with _ -> []
    match response, command with
    | Ok _, WriteFileCommand c when Path.GetFileName(c.FilePath) = "AGENTS.md" ->
        inspect c.ProjectName c.FilePath
    | Ok _, PatchFileCommand c when Path.GetFileName(c.FilePath) = "AGENTS.md"
                                && not (defaultArg c.DryRun false) ->
        inspect c.ProjectName c.FilePath
    | _ -> []

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
                        if List.length docs >= maxDocuments then
                            // The remaining paths are reported, never silently lost.
                            let marker =
                                $"[INCOMPLETE REPOSITORY INSTRUCTIONS] Document delivery reached the {maxDocuments}-file limit. Additional instruction paths (including {path}) were omitted. Load them manually using read_file. [/INCOMPLETE REPOSITORY INSTRUCTIONS]"
                            Ok(List.rev docs @ [ { Scope = "instruction-warning"; Path = path; Sha256 = ""; Content = marker } ])
                        else
                            // Keep at least 1 KiB available for each later candidate,
                            // so a long workspace file cannot crowd out specific guidance.
                            let reserve = min (maxTotalContentBytes / 2) (List.length tail * 1024)
                            let budget = max 0 (maxTotalContentBytes - total - reserve)
                            match readDocument rt scope path budget with
                            | Error error -> Error error
                            | Ok None -> collect tail docs total
                            | Ok(Some doc) ->
                                let delivered =
                                    if doc.Content.StartsWith("[INCOMPLETE REPOSITORY INSTRUCTIONS", StringComparison.Ordinal) then
                                        // The header adds metadata but not instruction source bytes.
                                        let index = doc.Content.IndexOf("\n\n", StringComparison.Ordinal)
                                        if index >= 0 then Encoding.UTF8.GetByteCount(doc.Content.Substring(index + 2))
                                        else 0
                                    else Encoding.UTF8.GetByteCount doc.Content
                                collect tail (doc :: docs) (total + delivered)
                collect candidates [] 0
