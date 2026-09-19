namespace Server

open System
open System.ComponentModel
open System.Text.Json
open System.Threading.Tasks
open Common
open ModelContextProtocol.Server
open Server.Services

module private McpToolHelpers =
    let private formatError error =
        match error with
        | NotFound message -> $"NotFound: {message}"
        | PermissionDenied message -> $"PermissionDenied: {message}"
        | ConfirmationRequired request -> $"ConfirmationRequired: {request.Summary}"
        | ValidationFailed message -> $"ValidationFailed: {message}"
        | Conflict message -> $"Conflict: {message}"
        | ExecutionFailed message -> $"ExecutionFailed: {message}"
        | OutputTruncated message -> $"OutputTruncated: {message}"

    let send (client: ClientService) key command =
        task {
            let! response = client.SendCommandToUser({ Key = key; Command = command })

            match response.Result, response.Error with
            | Some result, None ->
                use document = JsonDocument.Parse(result)
                return document.RootElement.Clone()
            | None, Some error ->
                return raise (InvalidOperationException(formatError error))
            | Some _, Some error ->
                return raise (InvalidOperationException($"Invalid Jarvis response: both result and error were set. {formatError error}"))
            | None, None ->
                return raise (InvalidOperationException("Invalid Jarvis response: neither result nor error was set."))
        }

    let optionOfString (value: string) =
        if String.IsNullOrWhiteSpace(value) then None else Some value

    let optionOfNullable (value: Nullable<int>) =
        if value.HasValue then Some value.Value else None

    let optionOfNullableBool (value: Nullable<bool>) =
        if value.HasValue then Some value.Value else None

    let parseFileWriteMode (value: string) =
        if String.Equals(value, "Append", StringComparison.OrdinalIgnoreCase) then Append
        elif String.Equals(value, "Write", StringComparison.OrdinalIgnoreCase) then Write
        else invalidArg "fileWriteMode" $"Unknown file write mode: {value}"

[<McpServerToolType>]
type JarvisMcpTools =
    [<McpServerTool(UseStructuredContent = true); Description("List the commands supported by the connected Jarvis client.")>]
    static member ListCommands([<Description("Jarvis client session key shown by the local client.")>] key: string, client: ClientService) =
        McpToolHelpers.send client key ListCommandsCommand

    [<McpServerTool(UseStructuredContent = true); Description("List projects exposed by the connected Jarvis client.")>]
    static member ListProjects([<Description("Jarvis client session key shown by the local client.")>] key: string, client: ClientService) =
        McpToolHelpers.send client key ListProjectsCommand

    [<McpServerTool(UseStructuredContent = true); Description("Get project details, README, notes, TODO, and related special files.")>]
    static member GetProjectDetails(key: string, projectName: string, client: ClientService) =
        McpToolHelpers.send client key (GetProjectDetailsCommand { ProjectName = projectName })

    [<McpServerTool(UseStructuredContent = true); Description("List files and folders in a project directory.")>]
    static member ListDirectory(key: string, projectName: string, folderPath: string, client: ClientService) =
        McpToolHelpers.send client key (ListDirectoryCommand { ProjectName = projectName; FolderPath = folderPath })

    [<McpServerTool(UseStructuredContent = true); Description("Search project file and folder names.")>]
    static member SearchFiles(key: string, projectName: string, query: string, folderPath: string, maxResults: Nullable<int>, client: ClientService) =
        McpToolHelpers.send client key (SearchFilesCommand {
            ProjectName = projectName
            Query = query
            FolderPath = McpToolHelpers.optionOfString folderPath
            MaxResults = McpToolHelpers.optionOfNullable maxResults })

    [<McpServerTool(UseStructuredContent = true); Description("Search text inside project files.")>]
    static member SearchText(key: string, projectName: string, query: string, folderPath: string, includeGlobs: string array, excludeGlobs: string array, maxResults: Nullable<int>, client: ClientService) =
        McpToolHelpers.send client key (SearchTextCommand {
            ProjectName = projectName
            Query = query
            FolderPath = McpToolHelpers.optionOfString folderPath
            IncludeGlobs = includeGlobs |> Array.toList
            ExcludeGlobs = excludeGlobs |> Array.toList
            MaxResults = McpToolHelpers.optionOfNullable maxResults })

    [<McpServerTool(UseStructuredContent = true); Description("Read one project file.")>]
    static member ReadFile(key: string, projectName: string, filePath: string, startLine: Nullable<int>, endLine: Nullable<int>, includeLineNumbers: Nullable<bool>, client: ClientService) =
        McpToolHelpers.send client key (ReadFileCommand {
            ProjectName = projectName
            FilePath = filePath
            StartLine = McpToolHelpers.optionOfNullable startLine
            EndLine = McpToolHelpers.optionOfNullable endLine
            IncludeLineNumbers = McpToolHelpers.optionOfNullableBool includeLineNumbers })

    [<McpServerTool(UseStructuredContent = true); Description("Read multiple project files.")>]
    static member ReadFiles(key: string, projectName: string, filePaths: string array, client: ClientService) =
        McpToolHelpers.send client key (ReadFilesCommand { ProjectName = projectName; FilePaths = filePaths |> Array.toList })

    [<McpServerTool(UseStructuredContent = true); Description("Write or append to a project file. Requires approval in the local Jarvis client.")>]
    static member WriteFile(key: string, projectName: string, filePath: string, content: string, fileWriteMode: string, expectedHash: string, createParents: Nullable<bool>, client: ClientService) =
        let mode = McpToolHelpers.parseFileWriteMode fileWriteMode
        McpToolHelpers.send client key (WriteFileCommand {
            ProjectName = projectName
            FilePath = filePath
            Content = content
            FileWriteMode = mode
            ExpectedHash = McpToolHelpers.optionOfString expectedHash
            CreateParents = McpToolHelpers.optionOfNullableBool createParents })

    [<McpServerTool(UseStructuredContent = true); Description("Apply an atomic unified diff patch to one project file. Requires approval in the local Jarvis client.")>]
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

    [<McpServerTool(UseStructuredContent = true); Description("Run a bounded local command in a project. Requires approval in the local Jarvis client.")>]
    static member RunCommand(key: string, projectName: string, executable: string, args: string array, workingDirectory: string, timeoutSeconds: Nullable<int>, maxOutputBytes: Nullable<int>, client: ClientService) =
        McpToolHelpers.send client key (RunCommandCommand {
            ProjectName = projectName
            Executable = executable
            Args = args |> Array.toList
            WorkingDirectory = McpToolHelpers.optionOfString workingDirectory
            TimeoutSeconds = McpToolHelpers.optionOfNullable timeoutSeconds
            MaxOutputBytes = McpToolHelpers.optionOfNullable maxOutputBytes })

    [<McpServerTool(UseStructuredContent = true); Description("Get git status for a project.")>]
    static member GetGitStatus(key: string, projectName: string, client: ClientService) =
        McpToolHelpers.send client key (GetGitStatusCommand { ProjectName = projectName })

    [<McpServerTool(UseStructuredContent = true); Description("Get git diff for a project or project-relative path.")>]
    static member GetGitDiff(key: string, projectName: string, path: string, maxOutputBytes: Nullable<int>, client: ClientService) =
        McpToolHelpers.send client key (GetGitDiffCommand {
            ProjectName = projectName
            Path = McpToolHelpers.optionOfString path
            MaxOutputBytes = McpToolHelpers.optionOfNullable maxOutputBytes })

    [<McpServerTool(UseStructuredContent = true); Description("Create a local git commit from selected paths. Requires approval in the local Jarvis client.")>]
    static member GitCommit(key: string, projectName: string, message: string, body: string, paths: string array, allowEmpty: bool, client: ClientService) =
        McpToolHelpers.send client key (GitCommitCommand {
            ProjectName = projectName
            Message = message
            Body = McpToolHelpers.optionOfString body
            Paths = paths |> Array.toList
            AllowEmpty = allowEmpty })

    [<McpServerTool(UseStructuredContent = true); Description("Start a long-running local job. Requires approval in the local Jarvis client.")>]
    static member StartJob(key: string, projectName: string, executable: string, args: string array, workingDirectory: string, maxOutputBytes: Nullable<int>, client: ClientService) =
        McpToolHelpers.send client key (StartJobCommand {
            ProjectName = projectName
            Executable = executable
            Args = args |> Array.toList
            WorkingDirectory = McpToolHelpers.optionOfString workingDirectory
            MaxOutputBytes = McpToolHelpers.optionOfNullable maxOutputBytes })

    [<McpServerTool(UseStructuredContent = true); Description("List running or completed Jarvis jobs.")>]
    static member ListJobs(key: string, projectName: string, includeCompleted: bool, client: ClientService) =
        McpToolHelpers.send client key (ListJobsCommand {
            ProjectName = McpToolHelpers.optionOfString projectName
            IncludeCompleted = includeCompleted })

    [<McpServerTool(UseStructuredContent = true); Description("Get buffered output and status for a Jarvis job.")>]
    static member GetJobResult(key: string, jobId: string, fromOffset: Nullable<int>, client: ClientService) =
        McpToolHelpers.send client key (GetJobResultCommand {
            JobId = jobId
            FromOffset = McpToolHelpers.optionOfNullable fromOffset })

    [<McpServerTool(UseStructuredContent = true); Description("Cancel a running Jarvis job. Requires approval in the local Jarvis client.")>]
    static member CancelJob(key: string, jobId: string, client: ClientService) =
        McpToolHelpers.send client key (CancelJobCommand { JobId = jobId })
