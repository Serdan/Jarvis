namespace Server

open System
open System.ComponentModel
open System.Threading.Tasks
open Common
open ModelContextProtocol.Server
open Server.Services

module private McpToolHelpers =
    let send (client: ClientService) key command =
        client.SendCommandToUser({ Key = key; Command = command })

    let optionOfString (value: string) =
        if String.IsNullOrWhiteSpace(value) then None else Some value

    let optionOfNullable (value: Nullable<int>) =
        if value.HasValue then Some value.Value else None

[<McpServerToolType>]
type JarvisMcpTools =
    [<McpServerTool; Description("List the commands supported by the connected Jarvis client.")>]
    static member ListCommands([<Description("Jarvis client session key shown by the local client.")>] key: string, client: ClientService) =
        McpToolHelpers.send client key ListCommandsCommand

    [<McpServerTool; Description("List projects exposed by the connected Jarvis client.")>]
    static member ListProjects([<Description("Jarvis client session key shown by the local client.")>] key: string, client: ClientService) =
        McpToolHelpers.send client key ListProjectsCommand

    [<McpServerTool; Description("Get project details, README, notes, TODO, and related special files.")>]
    static member GetProjectDetails(key: string, projectName: string, client: ClientService) =
        McpToolHelpers.send client key (GetProjectDetailsCommand { ProjectName = projectName })

    [<McpServerTool; Description("List files and folders in a project directory.")>]
    static member ListDirectory(key: string, projectName: string, folderPath: string, client: ClientService) =
        McpToolHelpers.send client key (ListDirectoryCommand { ProjectName = projectName; FolderPath = folderPath })

    [<McpServerTool; Description("Search project file and folder names.")>]
    static member SearchFiles(key: string, projectName: string, query: string, folderPath: string, maxResults: Nullable<int>, client: ClientService) =
        McpToolHelpers.send client key (SearchFilesCommand {
            ProjectName = projectName
            Query = query
            FolderPath = McpToolHelpers.optionOfString folderPath
            MaxResults = McpToolHelpers.optionOfNullable maxResults })

    [<McpServerTool; Description("Search text inside project files.")>]
    static member SearchText(key: string, projectName: string, query: string, folderPath: string, includeGlobs: string array, excludeGlobs: string array, maxResults: Nullable<int>, client: ClientService) =
        McpToolHelpers.send client key (SearchTextCommand {
            ProjectName = projectName
            Query = query
            FolderPath = McpToolHelpers.optionOfString folderPath
            IncludeGlobs = includeGlobs |> Array.toList
            ExcludeGlobs = excludeGlobs |> Array.toList
            MaxResults = McpToolHelpers.optionOfNullable maxResults })

    [<McpServerTool; Description("Read one project file.")>]
    static member ReadFile(key: string, projectName: string, filePath: string, client: ClientService) =
        McpToolHelpers.send client key (ReadFileCommand { ProjectName = projectName; FilePath = filePath })

    [<McpServerTool; Description("Read multiple project files.")>]
    static member ReadFiles(key: string, projectName: string, filePaths: string array, client: ClientService) =
        McpToolHelpers.send client key (ReadFilesCommand { ProjectName = projectName; FilePaths = filePaths |> Array.toList })

    [<McpServerTool; Description("Write or append to a project file. Requires approval in the local Jarvis client.")>]
    static member WriteFile(key: string, projectName: string, filePath: string, content: string, fileWriteMode: string, expectedHash: string, client: ClientService) =
        let mode = if String.Equals(fileWriteMode, "Append", StringComparison.OrdinalIgnoreCase) then Append else Write
        McpToolHelpers.send client key (WriteFileCommand {
            ProjectName = projectName
            FilePath = filePath
            Content = content
            FileWriteMode = mode
            ExpectedHash = McpToolHelpers.optionOfString expectedHash })

    [<McpServerTool; Description("Apply an atomic unified diff patch to one project file. Requires approval in the local Jarvis client.")>]
    static member PatchFile(key: string, projectName: string, filePath: string, patch: string, expectedHash: string, dryRun: Nullable<bool>, client: ClientService) =
        McpToolHelpers.send client key (PatchFileCommand {
            ProjectName = projectName
            FilePath = filePath
            ExpectedHash = McpToolHelpers.optionOfString expectedHash
            Format = UnifiedDiff
            Patch = patch
            DryRun = if dryRun.HasValue then Some dryRun.Value else None
            FuzzyContextLines = None
            ReturnContent = None })

    [<McpServerTool; Description("Run a bounded local command in a project. Requires approval in the local Jarvis client.")>]
    static member RunCommand(key: string, projectName: string, executable: string, args: string array, workingDirectory: string, timeoutSeconds: Nullable<int>, maxOutputBytes: Nullable<int>, client: ClientService) =
        McpToolHelpers.send client key (RunCommandCommand {
            ProjectName = projectName
            Executable = executable
            Args = args |> Array.toList
            WorkingDirectory = McpToolHelpers.optionOfString workingDirectory
            TimeoutSeconds = McpToolHelpers.optionOfNullable timeoutSeconds
            MaxOutputBytes = McpToolHelpers.optionOfNullable maxOutputBytes })

    [<McpServerTool; Description("Get git status for a project.")>]
    static member GetGitStatus(key: string, projectName: string, client: ClientService) =
        McpToolHelpers.send client key (GetGitStatusCommand { ProjectName = projectName })

    [<McpServerTool; Description("Get git diff for a project or project-relative path.")>]
    static member GetGitDiff(key: string, projectName: string, path: string, maxOutputBytes: Nullable<int>, client: ClientService) =
        McpToolHelpers.send client key (GetGitDiffCommand {
            ProjectName = projectName
            Path = McpToolHelpers.optionOfString path
            MaxOutputBytes = McpToolHelpers.optionOfNullable maxOutputBytes })

    [<McpServerTool; Description("Create a local git commit from selected paths. Requires approval in the local Jarvis client.")>]
    static member GitCommit(key: string, projectName: string, message: string, body: string, paths: string array, allowEmpty: bool, client: ClientService) =
        McpToolHelpers.send client key (GitCommitCommand {
            ProjectName = projectName
            Message = message
            Body = McpToolHelpers.optionOfString body
            Paths = paths |> Array.toList
            AllowEmpty = allowEmpty })

    [<McpServerTool; Description("Start a long-running local job. Requires approval in the local Jarvis client.")>]
    static member StartJob(key: string, projectName: string, executable: string, args: string array, workingDirectory: string, maxOutputBytes: Nullable<int>, client: ClientService) =
        McpToolHelpers.send client key (StartJobCommand {
            ProjectName = projectName
            Executable = executable
            Args = args |> Array.toList
            WorkingDirectory = McpToolHelpers.optionOfString workingDirectory
            MaxOutputBytes = McpToolHelpers.optionOfNullable maxOutputBytes })

    [<McpServerTool; Description("List running or completed Jarvis jobs.")>]
    static member ListJobs(key: string, projectName: string, includeCompleted: bool, client: ClientService) =
        McpToolHelpers.send client key (ListJobsCommand {
            ProjectName = McpToolHelpers.optionOfString projectName
            IncludeCompleted = includeCompleted })

    [<McpServerTool; Description("Get buffered output and status for a Jarvis job.")>]
    static member GetJobResult(key: string, jobId: string, fromOffset: Nullable<int>, client: ClientService) =
        McpToolHelpers.send client key (GetJobResultCommand {
            JobId = jobId
            FromOffset = McpToolHelpers.optionOfNullable fromOffset })

    [<McpServerTool; Description("Cancel a running Jarvis job. Requires approval in the local Jarvis client.")>]
    static member CancelJob(key: string, jobId: string, client: ClientService) =
        McpToolHelpers.send client key (CancelJobCommand { JobId = jobId })
