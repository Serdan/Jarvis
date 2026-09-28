namespace Server

[<CLIMutable>]
type ProfileResult = { id: string }

open System
open System.ComponentModel
open System.Runtime.InteropServices
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open Common
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Options
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

    let private textContent text =
        TextContentBlock(Text = text) :> ContentBlock

    let private textResult text isError =
        let result = CallToolResult()
        result.Content <- ResizeArray<ContentBlock>([ textContent text ])
        result.IsError <- Nullable isError
        result

    let private structuredResult structuredContent text isError =
        let result = textResult text isError
        result.StructuredContent <- Nullable structuredContent
        result

    let private errorResult error =
        textResult (formatError error) true

    let toCallToolResult (response: AgentCommandResponse) =
        match response.Result, response.Error with
        | Some serialized, None ->
            try
                use _ = JsonDocument.Parse(serialized)
                textResult serialized false
            with ex ->
                errorResult (ExecutionFailed $"Invalid serialized Jarvis result: {ex.Message}")
        | None, Some error ->
            errorResult error
        | Some _, Some error ->
            errorResult (ExecutionFailed $"Invalid Jarvis response: both result and error were set. {formatError error}")
        | None, None ->
            errorResult (ExecutionFailed "Invalid Jarvis response: neither result nor error was set.")

    let private requiredScope command =
        match command with
        | ListCommandsCommand
        | ListProjectsCommand
        | GetProjectDetailsCommand _
        | ListDirectoryCommand _
        | SearchFilesCommand _
        | SearchTextCommand _
        | ReadFileCommand _
        | ReadFilesCommand _
        | ListProjectTasksCommand _
        | GetGitStatusCommand _
        | GetGitDiffCommand _
        | ListJobsCommand _
        | GetJobResultCommand _ -> Auth.WorkspaceRead
        | WriteFileCommand _
        | PatchFileCommand _ -> Auth.WorkspaceWrite
        | RunCommandCommand _
        | RunProjectTaskCommand _
        | StartJobCommand _
        | CancelJobCommand _ -> Auth.ProcessExecute
        | GitCommitCommand _ -> Auth.GitWrite

    let private oauthErrorResult (context: HttpContext) errorCode description scope =
        let response = errorResult (PermissionDenied description)
        let options = context.RequestServices.GetRequiredService<IOptions<JarvisOptions>>().Value
        let quote = Char.ToString(char 34)
        let scopePart =
            match scope with
            | Some value -> ", scope=" + quote + value + quote
            | None -> ""

        let challenge =
            "Bearer resource_metadata=" + quote + Auth.resourceMetadataUri options + quote
            + ", error=" + quote + errorCode + quote
            + ", error_description=" + quote + description + quote
            + scopePart

        let challenges = JsonArray()
        challenges.Add(JsonValue.Create(challenge))

        let meta = JsonObject()
        meta["mcp/www_authenticate"] <- challenges
        response.Meta <- meta
        response

    let private authorizationErrorResult (context: HttpContext) scope =
        oauthErrorResult
            context
            "insufficient_scope"
            $"Missing required OAuth scope: {scope}"
            (Some scope)

    let private authenticationErrorResult (context: HttpContext) scope =
        oauthErrorResult context "invalid_token" "Authentication required." scope

    let send (client: ClientService) (http: IHttpContextAccessor) command =
        task {
            match Option.ofObj http.HttpContext with
            | None ->
                return errorResult (PermissionDenied "The MCP request is missing its HTTP context.")
            | Some context ->
                let scope = requiredScope command

                match Auth.tryUserId context.User with
                | Some userId when Auth.hasScope scope context.User ->
                    let! response = client.SendCommandToUser(userId, command)
                    return toCallToolResult response
                | Some _ ->
                    return authorizationErrorResult context scope
                | None ->
                    return authenticationErrorResult context (Some scope)
        }

    let getProfile (http: IHttpContextAccessor) =
        task {
            match Option.ofObj http.HttpContext with
            | None ->
                return errorResult (PermissionDenied "The MCP request is missing its HTTP context.")
            | Some context ->
                match Auth.tryUserId context.User with
                | Some userId ->
                    let profile = { id = Auth.profileId userId }
                    let structured = JsonSerializer.SerializeToElement(profile)
                    let serialized = JsonSerializer.Serialize(profile)
                    return structuredResult structured serialized false
                | None ->
                    return authenticationErrorResult context None
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
    [<McpServerTool(Title = "Get profile", UseStructuredContent = true, OutputSchemaType = typeof<ProfileResult>, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":[]}]"""); McpMeta("openai/profile", true); Description("Return the profile represented by the authenticated OAuth credentials.")>]
    static member GetProfile(http: IHttpContextAccessor) =
        McpToolHelpers.getProfile http

    [<McpServerTool(Title = "List commands", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("List the commands supported by the connected Jarvis client.")>]
    static member ListCommands(client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http ListCommandsCommand

    [<McpServerTool(Title = "List projects", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("List projects exposed by the connected Jarvis client.")>]
    static member ListProjects(client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http ListProjectsCommand

    [<McpServerTool(Title = "Get project details", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Get project details, README, notes, TODO, and related special files.")>]
    static member GetProjectDetails(projectName: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (GetProjectDetailsCommand { ProjectName = projectName })

    [<McpServerTool(Title = "List directory", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("List files and folders in a project directory.")>]
    static member ListDirectory(projectName: string, folderPath: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (ListDirectoryCommand { ProjectName = projectName; FolderPath = folderPath })

    [<McpServerTool(Title = "Search files", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Search project file and folder names.")>]
    static member SearchFiles(projectName: string, query: string, folderPath: string, maxResults: Nullable<int>, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (SearchFilesCommand {
            ProjectName = projectName
            Query = query
            FolderPath = McpToolHelpers.optionOfString folderPath
            MaxResults = McpToolHelpers.optionOfNullable maxResults })

    [<McpServerTool(Title = "Search text", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Search text inside project files.")>]
    static member SearchText(projectName: string, query: string, folderPath: string, includeGlobs: string array, excludeGlobs: string array, maxResults: Nullable<int>, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (SearchTextCommand {
            ProjectName = projectName
            Query = query
            FolderPath = McpToolHelpers.optionOfString folderPath
            IncludeGlobs = includeGlobs |> Array.toList
            ExcludeGlobs = excludeGlobs |> Array.toList
            MaxResults = McpToolHelpers.optionOfNullable maxResults })

    [<McpServerTool(Title = "Read file", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Read one project file.")>]
    static member ReadFile(projectName: string, filePath: string, startLine: Nullable<int>, endLine: Nullable<int>, includeLineNumbers: Nullable<bool>, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (ReadFileCommand {
            ProjectName = projectName
            FilePath = filePath
            StartLine = McpToolHelpers.optionOfNullable startLine
            EndLine = McpToolHelpers.optionOfNullable endLine
            IncludeLineNumbers = McpToolHelpers.optionOfNullableBool includeLineNumbers })

    [<McpServerTool(Title = "Read files", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Read multiple project files.")>]
    static member ReadFiles(projectName: string, filePaths: string array, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (ReadFilesCommand { ProjectName = projectName; FilePaths = filePaths |> Array.toList })

    [<McpServerTool(Title = "Write file", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:write"]}]"""); Description("Write or append to a project file. Requires approval in the local Jarvis client.")>]
    static member WriteFile(projectName: string, filePath: string, content: string, fileWriteMode: string, expectedHash: string, createParents: Nullable<bool>, client: ClientService, http: IHttpContextAccessor) =
        let mode = McpToolHelpers.parseFileWriteMode fileWriteMode
        McpToolHelpers.send client http (WriteFileCommand {
            ProjectName = projectName
            FilePath = filePath
            Content = content
            FileWriteMode = mode
            ExpectedHash = McpToolHelpers.optionOfString expectedHash
            CreateParents = McpToolHelpers.optionOfNullableBool createParents })

    [<McpServerTool(Title = "Patch file", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:write"]}]"""); Description("Apply an atomic unified diff patch to one project file. Requires approval in the local Jarvis client.")>]
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

    [<McpServerTool(Title = "Run command", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["process:execute"]}]"""); Description("Run a bounded local command in a project. Supply a short human-readable reason for why the command is needed. Requires approval in the local Jarvis client.")>]
    static member RunCommand(projectName: string, executable: string, args: string array, [<Optional; DefaultParameterValue("")>] reason: string, workingDirectory: string, timeoutSeconds: Nullable<int>, maxOutputBytes: Nullable<int>, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (RunCommandCommand {
            ProjectName = projectName
            Executable = executable
            Args = args |> Array.toList
            Reason = McpToolHelpers.optionOfString reason
            WorkingDirectory = McpToolHelpers.optionOfString workingDirectory
            TimeoutSeconds = McpToolHelpers.optionOfNullable timeoutSeconds
            MaxOutputBytes = McpToolHelpers.optionOfNullable maxOutputBytes })

    [<McpServerTool(Title = "List project tasks", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("List locally configured project tasks from .jarvis.json.")>]
    static member ListProjectTasks(projectName: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (ListProjectTasksCommand { ProjectName = projectName })

    [<McpServerTool(Title = "Run project task", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["process:execute"]}]"""); Description("Run a named task configured in the project's .jarvis.json. Requires local process approval.")>]
    static member RunProjectTask(projectName: string, taskName: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (RunProjectTaskCommand { ProjectName = projectName; TaskName = taskName })

    [<McpServerTool(Title = "Get git status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Get git status for a project.")>]
    static member GetGitStatus(projectName: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (GetGitStatusCommand { ProjectName = projectName })

    [<McpServerTool(Title = "Get git diff", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Get git diff for a project or project-relative path.")>]
    static member GetGitDiff(projectName: string, path: string, maxOutputBytes: Nullable<int>, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (GetGitDiffCommand {
            ProjectName = projectName
            Path = McpToolHelpers.optionOfString path
            MaxOutputBytes = McpToolHelpers.optionOfNullable maxOutputBytes })

    [<McpServerTool(Title = "Create git commit", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("Create a reversible local git commit from selected paths. Requires approval in the local Jarvis client and does not push to a remote.")>]
    static member GitCommit(projectName: string, message: string, body: string, paths: string array, allowEmpty: bool, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (GitCommitCommand {
            ProjectName = projectName
            Message = message
            Body = McpToolHelpers.optionOfString body
            Paths = paths |> Array.toList
            AllowEmpty = allowEmpty })

    [<McpServerTool(Title = "Start job", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["process:execute"]}]"""); Description("Start a long-running local job. Requires approval in the local Jarvis client.")>]
    static member StartJob(projectName: string, executable: string, args: string array, workingDirectory: string, maxOutputBytes: Nullable<int>, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (StartJobCommand {
            ProjectName = projectName
            Executable = executable
            Args = args |> Array.toList
            WorkingDirectory = McpToolHelpers.optionOfString workingDirectory
            MaxOutputBytes = McpToolHelpers.optionOfNullable maxOutputBytes })

    [<McpServerTool(Title = "List jobs", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("List running or completed Jarvis jobs.")>]
    static member ListJobs(projectName: string, includeCompleted: bool, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (ListJobsCommand {
            ProjectName = McpToolHelpers.optionOfString projectName
            IncludeCompleted = includeCompleted })

    [<McpServerTool(Title = "Get job result", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Get buffered output and status for a Jarvis job.")>]
    static member GetJobResult(jobId: string, afterSequence: Nullable<int64>, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (GetJobResultCommand {
            JobId = jobId
            AfterSequence = McpToolHelpers.optionOfNullableInt64 afterSequence })

    [<McpServerTool(Title = "Cancel job", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["process:execute"]}]"""); Description("Cancel a running Jarvis job. Requires approval in the local Jarvis client.")>]
    static member CancelJob(jobId: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (CancelJobCommand { JobId = jobId })
