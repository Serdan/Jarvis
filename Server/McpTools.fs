namespace Server

open System
open System.ComponentModel
open System.Text.Json
open System.Threading.Tasks
open Common
open Microsoft.AspNetCore.Http
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

    let send (client: ClientService) (http: IHttpContextAccessor) command =
        task {
            match Option.ofObj http.HttpContext |> Option.bind (fun context -> Auth.tryUserId context.User) with
            | Some userId ->
                let! response = client.SendCommandToUser(userId, command)
                return toCallToolResult response
            | None ->
                return errorResult (PermissionDenied "The MCP request is missing an authenticated user identity.")
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
    static member ListCommands(client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http ListCommandsCommand

    [<McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof<JsonElement>); Description("List projects exposed by the connected Jarvis client.")>]
    static member ListProjects(client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http ListProjectsCommand

    [<McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof<JsonElement>); Description("Get project details, README, notes, TODO, and related special files.")>]
    static member GetProjectDetails(projectName: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (GetProjectDetailsCommand { ProjectName = projectName })

    [<McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof<JsonElement>); Description("List files and folders in a project directory.")>]
    static member ListDirectory(projectName: string, folderPath: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (ListDirectoryCommand { ProjectName = projectName; FolderPath = folderPath })

    [<McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof<JsonElement>); Description("Search project file and folder names.")>]
    static member SearchFiles(projectName: string, query: string, folderPath: string, maxResults: Nullable<int>, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (SearchFilesCommand {
            ProjectName = projectName
            Query = query
            FolderPath = McpToolHelpers.optionOfString folderPath
            MaxResults = McpToolHelpers.optionOfNullable maxResults })

    [<McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof<JsonElement>); Description("Search text inside project files.")>]
    static member SearchText(projectName: string, query: string, folderPath: string, includeGlobs: string array, excludeGlobs: string array, maxResults: Nullable<int>, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (SearchTextCommand {
            ProjectName = projectName
            Query = query
            FolderPath = McpToolHelpers.optionOfString folderPath
            IncludeGlobs = includeGlobs |> Array.toList
            ExcludeGlobs = excludeGlobs |> Array.toList
            MaxResults = McpToolHelpers.optionOfNullable maxResults })

    [<McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof<JsonElement>); Description("Read one project file.")>]
    static member ReadFile(projectName: string, filePath: string, startLine: Nullable<int>, endLine: Nullable<int>, includeLineNumbers: Nullable<bool>, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (ReadFileCommand {
            ProjectName = projectName
            FilePath = filePath
            StartLine = McpToolHelpers.optionOfNullable startLine
            EndLine = McpToolHelpers.optionOfNullable endLine
            IncludeLineNumbers = McpToolHelpers.optionOfNullableBool includeLineNumbers })

    [<McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof<JsonElement>); Description("Read multiple project files.")>]
    static member ReadFiles(projectName: string, filePaths: string array, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (ReadFilesCommand { ProjectName = projectName; FilePaths = filePaths |> Array.toList })

    [<McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof<JsonElement>); Description("Write or append to a project file. Requires approval in the local Jarvis client.")>]
    static member WriteFile(projectName: string, filePath: string, content: string, fileWriteMode: string, expectedHash: string, createParents: Nullable<bool>, client: ClientService, http: IHttpContextAccessor) =
        let mode = McpToolHelpers.parseFileWriteMode fileWriteMode
        McpToolHelpers.send client http (WriteFileCommand {
            ProjectName = projectName
            FilePath = filePath
            Content = content
            FileWriteMode = mode
            ExpectedHash = McpToolHelpers.optionOfString expectedHash
            CreateParents = McpToolHelpers.optionOfNullableBool createParents })

    [<McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof<JsonElement>); Description("Apply an atomic unified diff patch to one project file. Requires approval in the local Jarvis client.")>]
    static member PatchFile(projectName: string, filePath: string, patch: string, expectedHash: string, dryRun: Nullable<bool>, fuzzyContextLines: Nullable<int>, returnContent: Nullable<bool>, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (PatchFileCommand {
            ProjectName = projectName
            FilePath = filePath
            ExpectedHash = McpToolHelpers.optionOfString expectedHash
            Format = UnifiedDiff
            Patch = patch
            DryRun = McpToolHelpers.optionOfNullableBool dryRun
            FuzzyContextLines = McpToolHelpers.optionOfNullable fuzzyContextLines
            ReturnContent = McpToolHelpers.optionOfNullableBool returnContent })

    [<McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof<JsonElement>); Description("Run a bounded local command in a project. Requires approval in the local Jarvis client.")>]
    static member RunCommand(projectName: string, executable: string, args: string array, workingDirectory: string, timeoutSeconds: Nullable<int>, maxOutputBytes: Nullable<int>, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (RunCommandCommand {
            ProjectName = projectName
            Executable = executable
            Args = args |> Array.toList
            WorkingDirectory = McpToolHelpers.optionOfString workingDirectory
            TimeoutSeconds = McpToolHelpers.optionOfNullable timeoutSeconds
            MaxOutputBytes = McpToolHelpers.optionOfNullable maxOutputBytes })

    [<McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof<JsonElement>); Description("List locally configured project tasks from .jarvis.json.")>]
    static member ListProjectTasks(projectName: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (ListProjectTasksCommand { ProjectName = projectName })

    [<McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof<JsonElement>); Description("Run a named task configured in the project's .jarvis.json. Requires local process approval.")>]
    static member RunProjectTask(projectName: string, taskName: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (RunProjectTaskCommand { ProjectName = projectName; TaskName = taskName })

    [<McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof<JsonElement>); Description("Get git status for a project.")>]
    static member GetGitStatus(projectName: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (GetGitStatusCommand { ProjectName = projectName })

    [<McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof<JsonElement>); Description("Get git diff for a project or project-relative path.")>]
    static member GetGitDiff(projectName: string, path: string, maxOutputBytes: Nullable<int>, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (GetGitDiffCommand {
            ProjectName = projectName
            Path = McpToolHelpers.optionOfString path
            MaxOutputBytes = McpToolHelpers.optionOfNullable maxOutputBytes })

    [<McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof<JsonElement>); Description("Create a local git commit from selected paths. Requires approval in the local Jarvis client.")>]
    static member GitCommit(projectName: string, message: string, body: string, paths: string array, allowEmpty: bool, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (GitCommitCommand {
            ProjectName = projectName
            Message = message
            Body = McpToolHelpers.optionOfString body
            Paths = paths |> Array.toList
            AllowEmpty = allowEmpty })

    [<McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof<JsonElement>); Description("Start a long-running local job. Requires approval in the local Jarvis client.")>]
    static member StartJob(projectName: string, executable: string, args: string array, workingDirectory: string, maxOutputBytes: Nullable<int>, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (StartJobCommand {
            ProjectName = projectName
            Executable = executable
            Args = args |> Array.toList
            WorkingDirectory = McpToolHelpers.optionOfString workingDirectory
            MaxOutputBytes = McpToolHelpers.optionOfNullable maxOutputBytes })

    [<McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof<JsonElement>); Description("List running or completed Jarvis jobs.")>]
    static member ListJobs(projectName: string, includeCompleted: bool, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (ListJobsCommand {
            ProjectName = McpToolHelpers.optionOfString projectName
            IncludeCompleted = includeCompleted })

    [<McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof<JsonElement>); Description("Get buffered output and status for a Jarvis job.")>]
    static member GetJobResult(jobId: string, afterSequence: Nullable<int64>, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (GetJobResultCommand {
            JobId = jobId
            AfterSequence = McpToolHelpers.optionOfNullableInt64 afterSequence })

    [<McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof<JsonElement>); Description("Cancel a running Jarvis job. Requires approval in the local Jarvis client.")>]
    static member CancelJob(jobId: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (CancelJobCommand { JobId = jobId })
