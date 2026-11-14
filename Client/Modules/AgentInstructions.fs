module Client.AgentInstructions

open System
open System.Collections.Concurrent
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
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
    | TaskReadCommand _
    | LedgerReadCommand _
    | LedgerWriteCommand _
    | ReviewReadCommand _
    | ReviewWriteCommand _ -> Ok None
    // These actions modify only the independently versioned Git ledger.
    // ExportTaskSpec and ImportTaskSpec touch source-tree files and therefore
    // retain normal source instruction validation.
    | TaskWriteCommand cmd when cmd.Action <> "ExportTaskSpec"
                                  && cmd.Action <> "ImportTaskSpec" -> Ok None
    | ListCommandsCommand
    | ListProjectsCommand
    | CreateProjectCommand _
    | UpdateClientCommand
    | GetJobResultCommand _
    | GetAgentInstructionsCommand _
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
                | ReadFilePageCommand c -> Ok(files [ c.FilePath ])
                | ReadFilesCommand c -> Ok(files c.FilePaths)
                | ReadImageCommand c -> Ok(files [ c.FilePath ])
                | ExportFileCommand c -> Ok(files [ c.FilePath ])
                | ImportFileCommand c -> Ok(files [ c.FilePath ])
                | WriteFileCommand c -> Ok(files [ c.FilePath ])
                | PatchFileCommand c -> Ok(files [ c.FilePath ])
                | RunCommandCommand c -> Ok(directories [ defaultArg c.WorkingDirectory "" ])
                | StartJobCommand c -> Ok(directories [ defaultArg c.WorkingDirectory "" ])
                | RunProjectTaskCommand _ -> Ok roots
                | TaskWriteCommand c when c.Action = "ExportTaskSpec" || c.Action = "ImportTaskSpec" ->
                    Ok(files [ c.SourceRef ])
                | GitCommitCommand c -> Ok(c.Paths |> List.map ExistingTarget)
                | GetGitDiffCommand c -> Ok [ ExistingTarget(defaultArg c.Path "") ]
                | GetSkillCommand c ->
                    Ok(files [ ProjectPaths.ProjectSkillPaths.primaryFile c.SkillName ])
                | CreateSkillCommand c ->
                    Ok(files [ ProjectPaths.ProjectSkillPaths.primaryFile c.SkillName ])
                | _ -> Ok roots
            targets |> Result.map (fun resolved -> Some(projectName, if resolved.IsEmpty then roots else resolved))

let private targetDirectory rt (project: ProjectPaths.ResolvedProject) (projectRoot: string) target =
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
            match ProjectPaths.resolveSourcePath project valid rt with
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

let private readDocument scope (relativePath: string) (path: string) (budget: int) =
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
            // Reject malformed sources regardless of whether they fit this response's budget.
            stream.Position <- 0L
            use reader = new StreamReader(stream, utf8, false, 8192, true)
            let scanBuffer = Array.zeroCreate<char> 8192
            while reader.Read(scanBuffer, 0, scanBuffer.Length) > 0 do ()
            let limit = min maxFileContentBytes (max 0 budget)
            if originalBytes = 0L then Ok None
            elif originalBytes > int64 limit then
                // No partial instructions: explicit omission can be retried on a
                // later call with a larger budget, or loaded with read_file.
                let warning =
                    $"[INCOMPLETE REPOSITORY INSTRUCTIONS] Source {relativePath} ({originalBytes} UTF-8 bytes; sha256:{digest}) was NOT delivered because it exceeds the {limit}-byte instruction budget for this response. No portion of this file has been loaded. Call get_agent_instructions for the affected directory to retrieve the complete hierarchy. [/INCOMPLETE REPOSITORY INSTRUCTIONS]"
                Ok(Some { Scope = "instruction-warning"; Path = relativePath; Sha256 = "sha256:" + digest; Content = warning })
            else
                stream.Position <- 0L
                let buffer = Array.zeroCreate<byte> (int originalBytes)
                stream.ReadExactly(buffer)
                Ok(Some { Scope = scope; Path = relativePath; Sha256 = "sha256:" + digest; Content = utf8.GetString(buffer) })
    with
    | :? FileNotFoundException
    | :? DirectoryNotFoundException -> Ok None
    | :? DecoderFallbackException ->
        Error(Client.ValidationError $"Instruction source {relativePath} is not valid UTF-8.")
    | ex -> Error(Client.ValidationError $"Cannot read instruction source {relativePath}: {ex.Message}")

// Successful writes to oversized instruction files are allowed, but the agent
// must see a clear warning immediately rather than discovering it by accident.
let writeWarningsForContext (rt: Runtime) (project: ProjectPaths.ResolvedProject option) command response =
    // Advisory errors must never replace a successful source mutation.
    let inspect projectName filePath =
        try
            let sourcePath =
                match project with
                | Some resolved -> ProjectPaths.resolveSourcePath resolved filePath
                | None -> ProjectPaths.resolveProjectPath projectName filePath
            match sourcePath rt with
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

let writeWarnings (rt: Runtime) command response =
    writeWarningsForContext rt None command response

// All physical instruction paths derive from a resolved checkout, never a
// protocol selector or human-facing display key.  The displayed hierarchy is
// logical; the cache keeps checkout provenance separately.
let private resolveTargets (rt: Runtime) (project: ProjectPaths.ResolvedProject) targets =
    let (FolderPath sourceRoot) = ProjectPaths.ResolvedProject.sourceRoot project
    let logicalProject = ProjectPaths.ProjectTarget.projectName (ProjectPaths.ResolvedProject.target project)
    let directories =
        targets
        |> List.fold (fun accumulated target ->
            accumulated |> Result.bind (fun dirs ->
                targetDirectory rt project sourceRoot target |> Result.map (fun dir -> dir :: dirs))) (Ok [])
    match directories with
    | Error error -> Error error
    | Ok dirs ->
        let logicalDirectories =
            dirs
            |> List.collect ancestors
            |> List.distinct
            |> List.sortBy (fun dir -> dir.Split('/', StringSplitOptions.RemoveEmptyEntries).Length, dir)
        let candidates =
            logicalDirectories
            |> List.map (fun dir ->
                let suffix = if dir = "" then "AGENTS.md" else dir + "/AGENTS.md"
                "project", logicalProject + "/" + suffix, Some suffix)
        let candidates = ("workspace", "AGENTS.md", None) :: candidates
        let rec collect pending docs total =
            match pending with
            | [] -> Ok(List.rev docs)
            | (scope, displayPath, physicalSuffix) :: tail ->
                if List.length docs >= maxDocuments then
                    let marker =
                        $"[INCOMPLETE REPOSITORY INSTRUCTIONS] Document delivery reached the {maxDocuments}-file limit. Additional instruction paths (including {displayPath}) were omitted. Call get_agent_instructions for the applicable directory. [/INCOMPLETE REPOSITORY INSTRUCTIONS]"
                    Ok(List.rev docs @ [ { Scope = "instruction-warning"; Path = displayPath; Sha256 = ""; Content = marker } ])
                else
                    let reserve = min (maxTotalContentBytes / 2) (List.length tail * 1024)
                    let budget = max 0 (maxTotalContentBytes - total - reserve)
                    let resolved =
                        match physicalSuffix with
                        | None -> ProjectPaths.resolveWorkspacePath [ "AGENTS.md" ] rt
                        | Some suffix -> ProjectPaths.resolveSourcePath project suffix rt
                    match resolved with
                    | Error error -> Error error
                    | Ok fullPath ->
                        match readDocument scope displayPath fullPath budget with
                        | Error error -> Error error
                        | Ok None -> collect tail docs total
                        | Ok(Some doc) ->
                            let delivered =
                                if doc.Scope = "instruction-warning" then 0
                                else Encoding.UTF8.GetByteCount doc.Content
                            collect tail (doc :: docs) (total + delivered)
        collect candidates [] 0

// Explicit instruction retrieval has no file-size or hierarchy-count limit.
// The ordinary 900 KiB command transport remains bounded, so a large result is
// assembled into pages. Cursors are stateless and bound to the selected checkout,
// requested directory and current contents of every applicable instruction file.
[<CLIMutable>]
type private InstructionCursor =
    { Version: int
      Project: string
      Directory: string
      Snapshot: string
      Index: int
      Offset: int64 }

type private ExplicitSource =
    { Key: string
      PhysicalPath: string
      Bytes: int64
      Sha256: string }

// Leave space for JSON overhead and the surrounding MCP/SignalR envelope.
// This is a *per-response transport* limit, never a document size limit.
let private explicitPageBytes = 640 * 1024

let private encodeCursor (value: InstructionCursor) =
    let payload = JsonObject()
    payload["version"] <- JsonValue.Create(value.Version)
    payload["project"] <- JsonValue.Create(value.Project)
    payload["directory"] <- JsonValue.Create(value.Directory)
    payload["snapshot"] <- JsonValue.Create(value.Snapshot)
    payload["index"] <- JsonValue.Create(value.Index)
    payload["offset"] <- JsonValue.Create(value.Offset)
    payload.ToJsonString()
    |> Encoding.UTF8.GetBytes
    |> Convert.ToBase64String
    |> fun encoded -> encoded.TrimEnd([| '=' |]).Replace('+', '-').Replace('/', '_')

let private decodeCursor (cursor: string) =
    try
        if String.IsNullOrWhiteSpace cursor || cursor.Length > 4096 then
            Error(Client.ValidationError "Invalid instruction continuation cursor.")
        else
            let normalized = cursor.Replace('-', '+').Replace('_', '/')
            let padded = normalized.PadRight(((normalized.Length + 3) / 4) * 4, '=')
            let decoded = Convert.FromBase64String padded |> Encoding.UTF8.GetString
            let payload = JsonNode.Parse(decoded).AsObject()
            let parsed =
                { Version = payload["version"].GetValue<int>()
                  Project = payload["project"].GetValue<string>()
                  Directory = payload["directory"].GetValue<string>()
                  Snapshot = payload["snapshot"].GetValue<string>()
                  Index = payload["index"].GetValue<int>()
                  Offset = payload["offset"].GetValue<int64>() }
            if parsed.Version <> 1 then
                Error(Client.ValidationError "Invalid instruction continuation cursor.")
            else Ok parsed
    with _ ->
        Error(Client.ValidationError "Invalid instruction continuation cursor.")

let private inspectExplicitSource (key: string) (physicalPath: string) =
    try
        let exactlyNamed =
            Directory.EnumerateFileSystemEntries(Path.GetDirectoryName physicalPath)
            |> Seq.exists (fun candidate ->
                String.Equals(Path.GetFileName candidate, "AGENTS.md", StringComparison.Ordinal))
        if not exactlyNamed then raise (FileNotFoundException())
        let attributes = File.GetAttributes physicalPath
        if attributes.HasFlag FileAttributes.Directory then
            Error(Client.ValidationError $"Instruction source {key}/AGENTS.md is a directory.")
        elif attributes.HasFlag FileAttributes.ReparsePoint then
            Error(Client.PermissionDenied $"Instruction source {key}/AGENTS.md is a link.")
        else
            use stream = new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.Read)
            let bytes = stream.Length
            let digest = SHA256.HashData(stream) |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()
            stream.Position <- 0L
            use reader = new StreamReader(stream, utf8, false, 8192, true)
            let scanBuffer = Array.zeroCreate<char> 8192
            while reader.Read(scanBuffer, 0, scanBuffer.Length) > 0 do ()
            Ok(Some { Key = key; PhysicalPath = physicalPath; Bytes = bytes; Sha256 = "sha256:" + digest })
    with
    | :? FileNotFoundException
    | :? DirectoryNotFoundException -> Ok None
    | :? DecoderFallbackException ->
        Error(Client.ValidationError $"Instruction source {key}/AGENTS.md is not valid UTF-8.")
    | ex ->
        Error(Client.ValidationError $"Cannot read instruction source {key}/AGENTS.md: {ex.Message}")

let private explicitSources (rt: Runtime) (project: ProjectPaths.ResolvedProject) directory =
    let (FolderPath sourceRoot) = ProjectPaths.ResolvedProject.sourceRoot project
    let projectName = ProjectPaths.ProjectTarget.projectName (ProjectPaths.ResolvedProject.target project)
    targetDirectory rt project sourceRoot (FolderTarget directory)
    |> Result.bind (fun normalized ->
        let candidates =
            (".", None) ::
            [ for ancestor in ancestors normalized do
                let suffix = if ancestor = "" then "AGENTS.md" else ancestor + "/AGENTS.md"
                let key = if ancestor = "" then projectName else projectName + "/" + ancestor
                yield key, Some suffix ]
        candidates
        |> List.fold (fun result (key, suffix) ->
            result
            |> Result.bind (fun found ->
                let path =
                    match suffix with
                    | None -> ProjectPaths.resolveWorkspacePath [ "AGENTS.md" ] rt
                    | Some value -> ProjectPaths.resolveSourcePath project value rt
                path
                |> Result.bind (inspectExplicitSource key)
                |> Result.map (function Some source -> source :: found | None -> found))) (Ok [])
        |> Result.map (fun reversed ->
            let sources = List.rev reversed
            let fingerprint =
                sources
                |> List.map (fun source ->
                    source.Key + "\u0000" + string source.Bytes + "\u0000" + source.Sha256 + "\n")
                |> String.concat ""
                |> Encoding.UTF8.GetBytes
                |> SHA256.HashData
                |> Convert.ToHexString
            normalized, sources, fingerprint))

// Read a bounded amount from an arbitrarily large, previously validated UTF-8
// document. Never end in the middle of a code point. A forged mid-character
// cursor fails instead of returning corrupted content.
let private readExplicitSegment (source: ExplicitSource) (offset: int64) maxBytes =
    try
        use stream = new FileStream(source.PhysicalPath, FileMode.Open, FileAccess.Read, FileShare.Read)
        if stream.Length <> source.Bytes || offset < 0L || offset > source.Bytes then
            Error(Client.ValidationError "AGENTS.md changed during retrieval; restart without a cursor.")
        else
            stream.Position <- offset
            let count = int (min (int64 maxBytes) (source.Bytes - offset))
            let buffer = Array.zeroCreate<byte> count
            stream.ReadExactly(buffer)
            let rec decode length attempts =
                if length <= 0 && count > 0 || attempts > 4 then
                    Error(Client.ValidationError "Invalid UTF-8 continuation offset in AGENTS.md cursor.")
                else
                    try Ok(utf8.GetString(buffer, 0, length))
                    with :? DecoderFallbackException -> decode (length - 1) (attempts + 1)
            decode count 0
    with
    | :? EndOfStreamException ->
        Error(Client.ValidationError "AGENTS.md changed during retrieval; restart without a cursor.")
    | ex ->
        Error(Client.ValidationError $"Cannot read instruction source {source.Key}/AGENTS.md: {ex.Message}")

let private explicitPage (documents: (string * string) list) (segment: (ExplicitSource * int64 * int64) option) (cursor: string option) =
    let result = JsonObject()
    let hierarchy = JsonObject()
    for key, content in documents do
        hierarchy[key] <- JsonValue.Create(content)
    result["AGENTS.md"] <- hierarchy
    match segment with
    | Some(source, offset, endOffset) ->
        let chunk = JsonObject()
        chunk["path"] <- JsonValue.Create(source.Key)
        chunk["offsetBytes"] <- JsonValue.Create(offset)
        chunk["endOffsetBytes"] <- JsonValue.Create(endOffset)
        chunk["totalBytes"] <- JsonValue.Create(source.Bytes)
        chunk["sha256"] <- JsonValue.Create(source.Sha256)
        result["chunk"] <- chunk
    | None -> ()
    match cursor with
    | Some continuation -> result["nextCursor"] <- JsonValue.Create(continuation)
    | None -> ()
    result

let private explicitPageSize (page: JsonObject) =
    page.ToJsonString() |> Encoding.UTF8.GetByteCount

let private getExplicitRepository (target: ProjectPaths.ProjectTarget) (command: GetAgentInstructionsCommand) (rt: Runtime) pageBudget =
    ProjectPaths.resolveProjectContextForTarget target rt
    |> Result.bind (fun project ->
        explicitSources rt project (if isNull command.Directory then "" else command.Directory)
        |> Result.bind (fun (directory, sources, fingerprint) ->
            let count = List.length sources
            let snapshot = fingerprint.ToLowerInvariant()
            let cursorAt index offset =
                if index >= count then None
                else
                    Some(encodeCursor {
                        Version = 1
                        Project = command.ProjectName
                        Directory = directory
                        Snapshot = snapshot
                        Index = index
                        Offset = offset })
            let state =
                match command.Cursor with
                | None -> Ok(0, 0L)
                | Some encoded ->
                    decodeCursor encoded
                    |> Result.bind (fun value ->
                        if value.Project <> command.ProjectName
                           || value.Directory <> directory
                           || value.Snapshot <> snapshot
                           || value.Index < 0 || value.Index >= count
                           || value.Offset < 0L || value.Offset > sources[value.Index].Bytes then
                            Error(Client.ValidationError "Instruction hierarchy or cursor target changed; restart without a cursor.")
                        else Ok(value.Index, value.Offset))
            state
            |> Result.bind (fun (start, offset) ->
                let items = List.toArray sources
                let rec assemble index offset documents =
                    if index >= items.Length then
                        Ok(explicitPage documents None None)
                    else
                        let source = items[index]
                        readExplicitSegment source offset pageBudget
                        |> Result.bind (fun text ->
                            let endOffset = offset + int64 (utf8.GetByteCount text)
                            let complete = endOffset = source.Bytes
                            let nextIndex, nextOffset =
                                if complete then index + 1, 0L else index, endOffset
                            let candidate = documents @ [ source.Key, text ]
                            let chunk =
                                if offset > 0L || not complete then Some(source, offset, endOffset)
                                else None
                            let page = explicitPage candidate chunk (cursorAt nextIndex nextOffset)
                            if complete && explicitPageSize page <= pageBudget then
                                if offset > 0L then
                                    // Final continuation piece still needs its
                                    // byte-offset metadata; do not discard it
                                    // while assembling later full documents.
                                    Ok page
                                else
                                    assemble (index + 1) 0L candidate
                            elif documents.Length > 0 then
                                Ok(explicitPage documents None (cursorAt index offset))
                            else
                                // The current file alone exceeds the transport budget.
                                // Shrink a UTF-8-safe segment, never discard its tail.
                                let rec fit chars =
                                    if chars <= 0 && source.Bytes > offset then
                                        Error(Client.ValidationError "Cannot fit even one AGENTS.md character in a response.")
                                    else
                                        let chars =
                                            if chars > 0 && chars < text.Length && Char.IsHighSurrogate(text[chars - 1]) then
                                                chars - 1
                                            else chars
                                        let prefix = text.Substring(0, chars)
                                        let endByte = offset + int64 (utf8.GetByteCount prefix)
                                        let isComplete = endByte = source.Bytes
                                        let nextIndex, nextOffset =
                                            if isComplete then index + 1, 0L else index, endByte
                                        let part =
                                            if offset > 0L || not isComplete then Some(source, offset, endByte)
                                            else None
                                        let fitted =
                                            explicitPage [source.Key, prefix] part (cursorAt nextIndex nextOffset)
                                        if explicitPageSize fitted <= pageBudget then Ok fitted
                                        else fit (chars / 2)
                                fit text.Length)
                assemble start offset [])))

// Durable role and agent layers are read from the verified project ledger,
// never from the source checkout or a cached instruction delivery response.
// The first page resolves roles from the live ledger; local AGENTS.md must
// remain retrievable if that remote ledger is transiently unavailable.
// Continuation pages refer to the initial role resolution instead of making
// one new remote request per repository chunk.
let private unresolvedInstructionLayers (status: string) (code: string) (message: string) =
    let node = JsonObject()
    node["status"] <- JsonValue.Create(status)
    node["complete"] <- JsonValue.Create(false)
    node["code"] <- JsonValue.Create(code)
    node["message"] <- JsonValue.Create(message)
    node :> JsonNode

let private transientLedgerError = function
    | Client.LedgerFailureError failure
        when failure.Code = "RemoteUnavailable" || failure.Code = "LedgerContended" -> true
    | _ -> false

let getExplicit (target: ProjectPaths.ProjectTarget) (command: GetAgentInstructionsCommand) (rt: Runtime) =
    ProjectPaths.resolveProjectContextForTarget target rt
    |> Result.bind (fun project ->
        let projectName =
            ProjectPaths.ProjectTarget.projectName (ProjectPaths.ResolvedProject.target project)
        ProjectPaths.resolveProjectRoot projectName rt
        |> Result.bind (fun (FolderPath root) ->
            // Resolve the local hierarchy independently, including its
            // source-validation errors, before any optional remote ledger I/O.
            getExplicitRepository target command rt (explicitPageBytes - 1024)
            |> Result.bind (fun localPage ->
                let ledgerConfigured =
                    File.Exists(OrchestrationApi.LedgerBindingConfig.pathForRoot root)
                let layerNode =
                    if String.IsNullOrWhiteSpace command.AgentId || not ledgerConfigured then Ok None
                    elif command.Cursor.IsSome then
                        Ok(Some(unresolvedInstructionLayers "continuation" "RoleLayersNotRepeated"
                            "Role and individual instructions are checked only on the initial hierarchy page, not repeated here. Use that page's instructionLayers if present; restart without a cursor to refresh."))
                    else
                        OrchestrationApi.LedgerBindingConfig.loadWithSnapshot projectName rt
                        |> Result.bind (fun (binding, snapshot) ->
                            RoleInstructions.RoleInstructions.tryResolveAt binding snapshot command.AgentId
                            |> Result.mapError OrchestrationApi.LedgerErrors.toEffect)
                        |> function
                            | Ok resolved -> Ok(resolved |> Option.map JsonSerializer.SerializeToNode)
                            | Error failure when transientLedgerError failure ->
                                let code =
                                    match failure with
                                    | Client.LedgerFailureError detail -> detail.Code
                                    | _ -> "RoleLayersUnavailable"
                                Ok(Some(unresolvedInstructionLayers "unavailable" code
                                    "INCOMPLETE INSTRUCTIONS: the canonical role and agent layers could not be verified and were NOT loaded. Local AGENTS.md is available independently. Retry the initial request when the ledger is reachable."))
                            | Error failure -> Error failure
                layerNode
                |> Result.bind (fun layerNode ->
                    // A role body may be large. Repage only if the local page
                    // does not fit alongside the verified role payload.
                    let layerBytes =
                        layerNode
                        |> Option.map (fun node -> Encoding.UTF8.GetByteCount(node.ToJsonString()))
                        |> Option.defaultValue 0
                    let pageBudget = max 1024 (explicitPageBytes - layerBytes - 1024)
                    let fitted =
                        if explicitPageSize localPage <= pageBudget then Ok localPage
                        else getExplicitRepository target command rt pageBudget
                    fitted
                    |> Result.map (fun page ->
                        match layerNode with
                        | Some node -> page["instructionLayers"] <- node
                        | None -> ()
                        page)))))

// This snapshot must travel from instruction selection to authorization and
// execution: a mutable .jarvis.json must not be parsed twice for one call.
type PreparedOperation =
    { Instructions: InstructionDocument list
      Source: ProjectPaths.ResolvedProject option
      ProjectTask: ProjectTaskDefinition option }

// A registered worktree and configured task are each resolved exactly once.
let prepareOperationWithTarget (rt: Runtime) (target: ProjectPaths.ProjectTarget option) command =
    match applicableTargets rt command with
    | Error error -> Error error
    | Ok None -> Ok { Instructions = []; Source = None; ProjectTask = None }
    | Ok(Some(_, targets)) ->
        match target with
        | None -> Error(Client.ValidationError "Project target is required for instruction resolution.")
        | Some typed ->
            match ProjectPaths.resolveProjectContextForTarget typed rt with
            | Error error -> Error error
            | Ok project ->
                let definition =
                    match command with
                    | RunProjectTaskCommand cmd ->
                        ClientShell.resolveProjectTaskIn project cmd rt |> Result.map Some
                    | _ -> Ok None
                definition
                |> Result.bind (fun task ->
                    let targetPaths =
                        match task with
                        | Some configured -> [ FolderTarget(defaultArg configured.WorkingDirectory "") ]
                        | None -> targets
                    resolveTargets rt project targetPaths
                    |> Result.map (fun instructions ->
                        { Instructions = instructions; Source = Some project; ProjectTask = task }))

let prepareWithTarget (rt: Runtime) (target: ProjectPaths.ProjectTarget option) command =
    prepareOperationWithTarget rt target command
    |> Result.map (fun prepared -> prepared.Instructions, prepared.Source)

let resolveWithTarget (rt: Runtime) (target: ProjectPaths.ProjectTarget option) command =
    prepareWithTarget rt target command |> Result.map fst

let resolve (rt: Runtime) command =
    let target =
        match AgentCommandInfo.projectName command with
        | None -> Ok None
        | Some projectName -> ProjectPaths.ProjectTarget.parse projectName |> Result.map Some
    target |> Result.bind (fun validated -> resolveWithTarget rt validated command)

// Ledger inboxes do not resolve arbitrary source directories, but still
// bootstrap workspace and selected checkout root instructions.
let rootsForTarget (rt: Runtime) (target: ProjectPaths.ProjectTarget) =
    ProjectPaths.resolveProjectContextForTarget target rt
    |> Result.bind (fun project -> resolveTargets rt project [ FolderTarget "" ])

let roots (rt: Runtime) projectName =
    ProjectPaths.ProjectTarget.parse projectName
    |> Result.bind (rootsForTarget rt)

// This is a best-effort per-process delivery cache, never authoritative state.
// A new working turn resets the set; another client may harmlessly redeliver.
// Track the *current observation* for each logical instruction path.
// A warning and a successful delivery are alternative states of the same
// source, not two independent cache entries.
type private DeliveryFingerprint =
    | Delivered of scope: string * sha256: string
    | Omitted of warning: string

type private DeliveryEntry =
    { Gate: obj
      mutable Turn: string
      // Resolve each checkout independently, but deduplicate identical
      // logical instruction paths across the project's worktrees.
      mutable BootstrappedCheckouts: Set<ProjectPaths.ProjectTarget>
      mutable Observed: Map<string, DeliveryFingerprint> }

// A completed source hierarchy lookup is authoritative even when no AGENTS.md
// was found. Ledger-only commands need a separate one-time root bootstrap.
type BootstrapProvenance =
    | HierarchyLoaded
    | RootBootstrapRequired

type DeliveryCache() =
    let entries = ConcurrentDictionary<ProjectPaths.ProjectId * string, DeliveryEntry>()

    member _.Filter(checkout: ProjectPaths.ProjectTarget, agentId: string, turn: string,
                    provenance: BootstrapProvenance, documents: InstructionDocument list,
                    loadRoots: unit -> Client.Result<InstructionDocument list>) =
        let key = ProjectPaths.ProjectTarget.projectId checkout, agentId
        let entry =
            entries.GetOrAdd(key, fun _ ->
                { Gate = obj(); Turn = turn; BootstrappedCheckouts = Set.empty; Observed = Map.empty })
        lock entry.Gate (fun () ->
            if entry.Turn <> turn then
                entry.Turn <- turn
                entry.BootstrappedCheckouts <- Set.empty
                entry.Observed <- Map.empty
            // A completed source lookup already checked all ancestors. Its
            // empty result is as conclusive as a non-empty one.
            let initial =
                if provenance = HierarchyLoaded then
                    entry.BootstrappedCheckouts <- Set.add checkout entry.BootstrappedCheckouts
                    []
                elif Set.contains checkout entry.BootstrappedCheckouts then
                    []
                else
                    match loadRoots() with
                    | Ok roots ->
                        entry.BootstrappedCheckouts <- Set.add checkout entry.BootstrappedCheckouts
                        roots
                    | Error error ->
                        // A ledger status operation must remain usable, but an
                        // unsuccessful bootstrap must remain retryable.
                        [ { Scope = "instruction-warning"; Path = "AGENTS.md"; Sha256 = ""
                            Content = $"Unable to load initial AGENTS.md instructions: {error}. The operation is unaffected; Jarvis will retry instruction delivery on the next tool call." } ]
            initial @ documents
            |> List.filter (fun document ->
                if document.Scope = "write-warning" || document.Scope = "working-turn" then true
                else
                    let key = document.Path
                    let fingerprint =
                        if document.Scope = "instruction-warning" then
                            Omitted document.Content
                        else Delivered(document.Scope, document.Sha256)
                    match Map.tryFind key entry.Observed with
                    | Some previous when previous = fingerprint -> false
                    | _ ->
                        entry.Observed <- Map.add key fingerprint entry.Observed
                        true))
