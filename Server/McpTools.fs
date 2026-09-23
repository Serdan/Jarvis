namespace Server

open System
open System.ComponentModel
open System.Text.Json
open System.Threading.Tasks
open Common
open ModelContextProtocol.Protocol
open ModelContextProtocol.Server
open Server.Services

module McpToolHelpers =
    let private formatError error =
        match error with
        | NotFound message -> $"NotFound: {message}"
        | PermissionDenied message -> $"PermissionDenied: {message}"
        | ConfirmationRequired request -> $"ConfirmationRequired: {request.Summary}"
        | ValidationFailed message -> $"ValidationFailed: {message}"
        | Conflict message -> $"Conflict: {message}"
        | ExecutionFailed message -> $"ExecutionFailed: {message}"
        | OutputTruncated message -> $"OutputTruncated: {message}"

    let private errorPayload error =
        match error with
        | NotFound message -> JsonSerializer.SerializeToElement({| kind = "NotFound"; message = message |})
        | PermissionDenied message -> JsonSerializer.SerializeToElement({| kind = "PermissionDenied"; message = message |})
        | ConfirmationRequired request ->
            JsonSerializer.SerializeToElement(
                {| kind = "ConfirmationRequired"
                   message = request.Summary
                   confirmationRequest = request |})
        | ValidationFailed message -> JsonSerializer.SerializeToElement({| kind = "ValidationFailed"; message = message |})
        | Conflict message -> JsonSerializer.SerializeToElement({| kind = "Conflict"; message = message |})
        | ExecutionFailed message -> JsonSerializer.SerializeToElement({| kind = "ExecutionFailed"; message = message |})
        | OutputTruncated message -> JsonSerializer.SerializeToElement({| kind = "OutputTruncated"; message = message |})

    let private textContent text =
        TextContentBlock(Text = text) :> ContentBlock

    let private result structuredContent text isError =
        let result = CallToolResult()
        result.Content <- ResizeArray<ContentBlock>([ textContent text ])
        result.StructuredContent <- Nullable structuredContent
        result.IsError <- Nullable isError
        result

    let private errorResult error =
        result (errorPayload error) (formatError error) true

    let toCallToolResult (response: AgentCommandResponse) =
        match response.Result, response.Error with
        | Some serialized, None ->
            try
                use document = JsonDocument.Parse(serialized)
                result (document.RootElement.Clone()) serialized false
            with ex ->
                errorResult (ExecutionFailed $"Invalid serialized Jarvis result: {ex.Message}")
        | None, Some error ->
            errorResult error
        | Some _, Some error ->
            errorResult (ExecutionFailed $"Invalid Jarvis response: both result and error were set. {formatError error}")
        | None, None ->
            errorResult (ExecutionFailed "Invalid Jarvis response: neither result nor error was set.")

    let send (client: ClientService) key command =
        task {
            let! response = client.SendCommandToUser({ Key = key; Command = command })
            return toCallToolResult response
        }

    let optionOfString (value: string) =
        if String.IsNullOrWhiteSpace(value) then None else Some value

    let optionOfNullable (value: Nullable<int>) =
        if value.HasValue then Some value.Value else None

    let optionOfNullableInt64 (value: Nullable<int64>) =
        if value.HasValue then Some value.Value else None

    let optionOfNullableBool (value: Nullable<bool>) =
        if value.HasValue then Some value.Value else None

    let parseFileWriteMode (value: string) =
        if String.Equals(value, "Append", StringComparison.OrdinalIgnoreCase) then Append
        elif String.Equals(value, "Write", StringComparison.OrdinalIgnoreCase) then Write
        else invalidArg "fileWriteMode" $"Unknown file write mode: {value}"

[<McpServerToolType>]
type JarvisMcpTools =
    [<McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof<JsonElement>); Description("List the commands supported by the connected Jarvis client.")>]
    static member ListCommands([<Description("Jarvis client session key shown by the local client.")>] key: string, client: ClientService) =
        McpToolHelpers.send client key ListCommandsCommand

    [<McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof<JsonElement>); Description("List projects exposed by the connected Jarvis client.")>]
    static member ListProjects([<Description("Jarvis client session key shown by the local client.")>] key: string, client: ClientService) =
        McpToolHelpers.send client key ListProjectsCommand

    [<McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof<JsonElement>); Description("Get project details, README, notes, TODO, and related special files.")>]
    static member GetProjectDetails(key: string, projectName: string, client: ClientService) =
        McpToolHelpers.send client key (GetProjectDetailsCommand { ProjectName = projectName })

    [<McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof<JsonElement>); Description("List files and folders in a project directory.")>]
    static member ListDirectory(key: string, projectName: string, folderPath: string, client: ClientService) =
        McpToolHelpers.send client key (ListDirectoryCommand { ProjectName = projectName; FolderPath = folderPath })

    [<McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof<JsonElement>); Description("Search project file and folder names.")>]
    static member SearchFiles(key: string, projectName: string, query: string, folderPath: string, maxResults: Nullable<int>, client: ClientService) =
        McpToolHelpers.send client key (SearchFilesCommand {
            ProjectName = projectName
            Query = query
            FolderPath = McpToolHelpers.optionOfString folderPath
            MaxResults = McpToolHelpers.optionOfNullable maxResults })

    [<McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof<JsonElement>); Description("Search text inside project files.")>]
    static member SearchText(key: string, projectName: string, query: string, folderPath: string, includeGlobs: string array, excludeGlobs: string array, maxResults: Nullable<int>, client: ClientService) =
        McpToolHelpers.send client key (SearchTextCommand {
            ProjectName = projectName
            Query = query
            FolderPath = McpToolHelpers.optionOfString folderPath
            IncludeGlobs = includeGlobs |> Array.toList
            ExcludeGlobs = excludeGlobs |> Array.toList
            MaxResults = McpToolHelpers.optionOfNullable maxResults })

    [<McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof<JsonElement>); Description("Read one project file.")>]
    static member ReadFile(key: string, projectName: string, filePath: string, startLine: Nullable<int>, endLine: Nullable<int>, includeLineNumbers: Nullable<bool>, client: ClientService) =
        McpToolHelpers.send client key (ReadFileCommand {
            ProjectName = projectName
            FilePath = filePath
            StartLine = McpToolHelpers.optionOfNullable startLine
            EndLine = McpToolHelpers.optionOfNullable endLine
            IncludeLineNumbers = McpToolHelpers.optionOfNullableBool includeLineNumbers })

    [<McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof<JsonElement>); Description("Read multiple project files.")>]
    static member ReadFiles(key: string, projectName: string, filePaths: string array, client: ClientService) =
        McpToolHelpers.send client key (ReadFilesCommand { ProjectName = projectName; FilePaths = filePaths |> Array.toList })

    [<McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof<JsonElement>); Description("Write or append to a project file. Requires approval in the local Jarvis client.")>]
    static member WriteFile(key: string, projectName: string, filePath: string, content: string, fileWriteMode: string, expectedHash: string, createParents: Nullable<bool>, client: ClientService) =
        let mode = McpToolHelpers.parseFileWriteMode fileWriteMode
        McpToolHelpers.send client key (WriteFileCommand {
            ProjectName = projectName
            FilePath = filePath
            Content = content
            FileWriteMode = mode
            ExpectedHash = McpToolHelpers.optionOfString expectedHash
            CreateParents = McpToolHelpers.optionOfNullableBool createParents })

    [<McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof<JsonElement>); Description("Apply an atomic unified diff patch to one project file. Requires approval in the local Jarvis client.")>]
    static member PatchFile(key: string, projectName: string, filePath: string, patch: string, expectedHash: string, dryRun: Nullable<bool>, fuzzyContextLines: Nullable<int>, returnContent: Nullable<bool>, client: ClientService) =
        McpToolHelpers.send client key (PatchFileCommand {
            ProjectName = projectName
            FilePath = filePath
            ExpectedHash = McpToolHelpers.optionOfString expectedHash
            Format = UnifiedDiff
            Patch = patch
            DryRun = McpToolHelpers.optionOfNullableBool dryRun
            FuzzyContextLines = McpToolHelpers.optionOfNullable fuzzyContextLines
            ReturnContent = McpToolHelpers.optionOfNullableBool returnContent })

    [<McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof<JsonElement>); Description("Run a bounded local command in a project. Requires approval in the local Jarvis client.")>]
    static member RunCommand(key: string, projectName: string, executable: string, args: string array, workingDirectory: string, timeoutSeconds: Nullable<int>, maxOutputBytes: Nullable<int>, client: ClientService) =
        McpToolHelpers.send client key (RunCommandCommand {
            ProjectName = projectName
            Executable = executable
            Args = args |> Array.toList
            WorkingDirectory = McpToolHelpers.optionOfString workingDirectory
            TimeoutSeconds = McpToolHelpers.optionOfNullable timeoutSeconds
            MaxOutputBytes = McpToolHelpers.optionOfNullable maxOutputBytes })

    [<McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof<JsonElement>); Description("List locally configured project tasks from .jarvis.json.")>]
    static member ListProjectTasks(key: string, projectName: string, client: ClientService) =
        McpToolHelpers.send client key (ListProjectTasksCommand { ProjectName = projectName })

    [<McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof<JsonElement>); Description("Run a named task configured in the project's .jarvis.json. Requires local process approval.")>]
    static member RunProjectTask(key: string, projectName: string, taskName: string, client: ClientService) =
        McpToolHelpers.send client key (RunProjectTaskCommand { ProjectName = projectName; TaskName = taskName })

    [<McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof<JsonElement>); Description("Get git status for a project.")>]
    static member GetGitStatus(key: string, projectName: string, client: ClientService) =
        McpToolHelpers.send client key (GetGitStatusCommand { ProjectName = projectName })

    [<McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof<JsonElement>); Description("Get git diff for a project or project-relative path.")>]
    static member GetGitDiff(key: string, projectName: string, path: string, maxOutputBytes: Nullable<int>, client: ClientService) =
        McpToolHelpers.send client key (GetGitDiffCommand {
            ProjectName = projectName
            Path = McpToolHelpers.optionOfString path
            MaxOutputBytes = McpToolHelpers.optionOfNullable maxOutputBytes })

    [<McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof<JsonElement>); Description("Create a local git commit from selected paths. Requires approval in the local Jarvis client.")>]
    static member GitCommit(key: string, projectName: string, message: string, body: string, paths: string array, allowEmpty: bool, client: ClientService) =
        McpToolHelpers.send client key (GitCommitCommand {
            ProjectName = projectName
            Message = message
            Body = McpToolHelpers.optionOfString body
            Paths = paths |> Array.toList
            AllowEmpty = allowEmpty })

    [<McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof<JsonElement>); Description("Start a long-running local job. Requires approval in the local Jarvis client.")>]
    static member StartJob(key: string, projectName: string, executable: string, args: string array, workingDirectory: string, maxOutputBytes: Nullable<int>, client: ClientService) =
        McpToolHelpers.send client key (StartJobCommand {
            ProjectName = projectName
            Executable = executable
            Args = args |> Array.toList
            WorkingDirectory = McpToolHelpers.optionOfString workingDirectory
            MaxOutputBytes = McpToolHelpers.optionOfNullable maxOutputBytes })

    [<McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof<JsonElement>); Description("List running or completed Jarvis jobs.")>]
    static member ListJobs(key: string, projectName: string, includeCompleted: bool, client: ClientService) =
        McpToolHelpers.send client key (ListJobsCommand {
            ProjectName = McpToolHelpers.optionOfString projectName
            IncludeCompleted = includeCompleted })

    [<McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof<JsonElement>); Description("Get buffered output and status for a Jarvis job.")>]
    static member GetJobResult(key: string, jobId: string, afterSequence: Nullable<int64>, client: ClientService) =
        McpToolHelpers.send client key (GetJobResultCommand {
            JobId = jobId
            AfterSequence = McpToolHelpers.optionOfNullableInt64 afterSequence })

    [<McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof<JsonElement>); Description("Cancel a running Jarvis job. Requires approval in the local Jarvis client.")>]
    static member CancelJob(key: string, jobId: string, client: ClientService) =
        McpToolHelpers.send client key (CancelJobCommand { JobId = jobId })
