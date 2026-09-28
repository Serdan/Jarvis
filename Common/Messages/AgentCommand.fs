namespace Common

open System
open System.Text.Json.Serialization
open FsCodec.SystemTextJson

[<JsonConverter(typeof<TypeSafeEnumConverter<FileWriteMode>>)>]
type FileWriteMode =
    | Append
    | Write

[<JsonConverter(typeof<TypeSafeEnumConverter<PatchFormat>>)>]
type PatchFormat =
    | UnifiedDiff

[<JsonConverter(typeof<TypeSafeEnumConverter<PermissionLevel>>)>]
type PermissionLevel =
    | ReadOnly
    | WorkspaceWrite
    | ProcessExecution
    | VersionControlWrite
    | NetworkAccess
    | Destructive

[<JsonConverter(typeof<TypeSafeEnumConverter<PermissionDecision>>)>]
type PermissionDecision =
    | Allow
    | RequireConfirmation
    | Deny

[<JsonConverter(typeof<UnionConverter<JobStatus>>)>]
type JobStatus =
    | Running
    | Completed of exitCode: int
    | FailedToStart of message: string
    | Canceled

type CommandCapability =
    { Name: string
      Description: string
      Permissions: PermissionLevel list
      MutatesState: bool
      RequiresConfirmation: bool
      SupportsDryRun: bool
      MaxInputBytes: int option
      MaxOutputBytes: int option
      InputSchemaJson: string option
      OutputSchemaJson: string option }

type CommandDefinition =
    { Name: string
      Description: string
      Permissions: PermissionLevel list
      MutatesState: bool
      RequiresConfirmation: bool
      SupportsDryRun: bool }

type ListCommandsCommand = struct end

type ListCommandsResult =
    { ProtocolVersion: string
      Commands: CommandCapability list }

type ListProjectsCommand = struct end

type GetProjectDetailsCommand = { ProjectName: string }

type ListDirectoryCommand =
    { ProjectName: string
      FolderPath: string }

type SearchFilesCommand =
    { ProjectName: string
      Query: string
      FolderPath: string option
      MaxResults: int option }

type SearchTextCommand =
    { ProjectName: string
      Query: string
      FolderPath: string option
      IncludeGlobs: string list
      ExcludeGlobs: string list
      MaxResults: int option }

type SearchTextMatch =
    { FilePath: string
      Line: int
      Column: int
      Preview: string }

type ReadFileCommand =
    { ProjectName: string
      FilePath: string
      StartLine: int option
      EndLine: int option
      IncludeLineNumbers: bool option }

type ReadFilesCommand =
    { ProjectName: string
      FilePaths: string list }

type WriteFileCommand =
    { ProjectName: string
      FilePath: string
      Content: string
      FileWriteMode: FileWriteMode
      ExpectedHash: string option
      CreateParents: bool option }

[<JsonConverter(typeof<UnionConverter<PatchHunkStatus>>)>]
type PatchHunkStatus =
    | AppliedStrict
    | AppliedWithOffset of offset: int
    | Failed

type PatchHunkDiagnostic =
    { HunkIndex: int
      Status: PatchHunkStatus
      OriginalStartLine: int option
      AppliedStartLine: int option
      Message: string
      ExpectedContext: string list
      ActualContext: string list }

type PatchFileResult =
    { Applied: bool
      DryRun: bool
      FilePath: string
      HunksApplied: int
      ChangedLines: int
      BeforeHash: string
      AfterHash: string option
      Content: string option
      Diagnostics: PatchHunkDiagnostic list }

type PatchFileCommand =
    { ProjectName: string
      FilePath: string
      ExpectedHash: string option
      Format: PatchFormat
      Patch: string
      DryRun: bool option
      FuzzyContextLines: int option
      ReturnContent: bool option }

type RunCommandCommand =
    { ProjectName: string
      Executable: string
      Args: string list
      WorkingDirectory: string option
      TimeoutSeconds: int option
      MaxOutputBytes: int option }

type ProjectTaskDefinition =
    { Name: string
      Description: string option
      Executable: string
      Args: string list
      WorkingDirectory: string option
      TimeoutSeconds: int option
      MaxOutputBytes: int option }

type ListProjectTasksCommand =
    { ProjectName: string }

type ListProjectTasksResult =
    { Tasks: ProjectTaskDefinition list }

type RunProjectTaskCommand =
    { ProjectName: string
      TaskName: string }

type RunCommandResult =
    { ExitCode: int
      TimedOut: bool
      StdOut: string
      StdErr: string
      Truncated: bool }

type GitStatusCommand = { ProjectName: string }

type GitDiffCommand =
    { ProjectName: string
      Path: string option
      MaxOutputBytes: int option }

type GitCommitCommand =
    { ProjectName: string
      Message: string
      Body: string option
      Paths: string list
      AllowEmpty: bool }

type GitCommitResult =
    { CommitHash: string
      Summary: string
      StdOut: string
      StdErr: string }

type StartJobCommand =
    { ProjectName: string
      Executable: string
      Args: string list
      WorkingDirectory: string option
      MaxOutputBytes: int option }

type StartJobResult =
    { JobId: string
      StartedAt: DateTimeOffset }

type ListJobsCommand =
    { ProjectName: string option
      IncludeCompleted: bool }

type JobSummary =
    { JobId: string
      ProjectName: string
      Executable: string
      Args: string list
      WorkingDirectory: string option
      Status: JobStatus
      StartedAt: DateTimeOffset
      CompletedAt: DateTimeOffset option }

type ListJobsResult = { Jobs: JobSummary list }

[<JsonConverter(typeof<TypeSafeEnumConverter<JobOutputStream>>)>]
type JobOutputStream =
    | StdOut
    | StdErr

type JobOutputEvent =
    { Sequence: int64
      Stream: JobOutputStream
      Text: string }

type GetJobResultCommand =
    { JobId: string
      AfterSequence: int64 option }

type JobResult =
    { JobId: string
      Status: JobStatus
      Events: JobOutputEvent list
      NextSequence: int64
      Truncated: bool }

type CancelJobCommand = { JobId: string }

type ConfirmationRequest =
    { CommandName: string
      ProjectName: string option
      Permissions: PermissionLevel list
      Summary: string
      Paths: string list
      Executable: string option
      Args: string list
      EstimatedImpact: string
      SupportsDryRun: bool }

[<JsonConverter(typeof<UnionConverter<AgentError>>)>]
type AgentError =
    | NotFound of string
    | PermissionDenied of string
    | ConfirmationRequired of ConfirmationRequest
    | ValidationFailed of string
    | Conflict of string
    | ExecutionFailed of string
    | OutputTruncated of string

type AgentCommandResponse =
    { Result: string option
      Error: AgentError option }

[<JsonConverter(typeof<UnionConverter<AgentCommand>>)>]
type AgentCommand =
    | ListCommandsCommand
    | ListProjectsCommand
    | GetProjectDetailsCommand of GetProjectDetailsCommand
    | ListDirectoryCommand of ListDirectoryCommand
    | SearchFilesCommand of SearchFilesCommand
    | SearchTextCommand of SearchTextCommand
    | ReadFileCommand of ReadFileCommand
    | ReadFilesCommand of ReadFilesCommand
    | WriteFileCommand of WriteFileCommand
    | PatchFileCommand of PatchFileCommand
    | RunCommandCommand of RunCommandCommand
    | ListProjectTasksCommand of ListProjectTasksCommand
    | RunProjectTaskCommand of RunProjectTaskCommand
    | GetGitStatusCommand of GitStatusCommand
    | GetGitDiffCommand of GitDiffCommand
    | GitCommitCommand of GitCommitCommand
    | StartJobCommand of StartJobCommand
    | ListJobsCommand of ListJobsCommand
    | GetJobResultCommand of GetJobResultCommand
    | CancelJobCommand of CancelJobCommand

module AgentCommandInfo =
    let name = function
        | ListCommandsCommand -> "ListCommandsCommand"
        | ListProjectsCommand -> "ListProjectsCommand"
        | GetProjectDetailsCommand _ -> "GetProjectDetailsCommand"
        | ListDirectoryCommand _ -> "ListDirectoryCommand"
        | SearchFilesCommand _ -> "SearchFilesCommand"
        | SearchTextCommand _ -> "SearchTextCommand"
        | ReadFileCommand _ -> "ReadFileCommand"
        | ReadFilesCommand _ -> "ReadFilesCommand"
        | WriteFileCommand _ -> "WriteFileCommand"
        | PatchFileCommand _ -> "PatchFileCommand"
        | RunCommandCommand _ -> "RunCommandCommand"
        | ListProjectTasksCommand _ -> "ListProjectTasksCommand"
        | RunProjectTaskCommand _ -> "RunProjectTaskCommand"
        | GetGitStatusCommand _ -> "GetGitStatusCommand"
        | GetGitDiffCommand _ -> "GetGitDiffCommand"
        | GitCommitCommand _ -> "GitCommitCommand"
        | StartJobCommand _ -> "StartJobCommand"
        | ListJobsCommand _ -> "ListJobsCommand"
        | GetJobResultCommand _ -> "GetJobResultCommand"
        | CancelJobCommand _ -> "CancelJobCommand"

    let projectName = function
        | ListCommandsCommand
        | ListProjectsCommand
        | GetJobResultCommand _
        | CancelJobCommand _ -> None
        | GetProjectDetailsCommand cmd -> Some cmd.ProjectName
        | ListDirectoryCommand cmd -> Some cmd.ProjectName
        | SearchFilesCommand cmd -> Some cmd.ProjectName
        | SearchTextCommand cmd -> Some cmd.ProjectName
        | ReadFileCommand cmd -> Some cmd.ProjectName
        | ReadFilesCommand cmd -> Some cmd.ProjectName
        | WriteFileCommand cmd -> Some cmd.ProjectName
        | PatchFileCommand cmd -> Some cmd.ProjectName
        | RunCommandCommand cmd -> Some cmd.ProjectName
        | ListProjectTasksCommand cmd -> Some cmd.ProjectName
        | RunProjectTaskCommand cmd -> Some cmd.ProjectName
        | GetGitStatusCommand cmd -> Some cmd.ProjectName
        | GetGitDiffCommand cmd -> Some cmd.ProjectName
        | GitCommitCommand cmd -> Some cmd.ProjectName
        | StartJobCommand cmd -> Some cmd.ProjectName
        | ListJobsCommand cmd -> cmd.ProjectName

    let describe command =
        match projectName command with
        | Some projectName -> $"{name command} project={projectName}"
        | None -> name command

module AgentProtocol =
    let version = "3.0"
    let defaultPatchFuzzyContextLines = 3
    let maxResponseBytes = 900 * 1024

    let private definition name description permissions mutates requiresConfirmation supportsDryRun : CommandDefinition =
        { Name = name
          Description = description
          Permissions = permissions
          MutatesState = mutates
          RequiresConfirmation = requiresConfirmation
          SupportsDryRun = supportsDryRun }

    let commandDefinitions =
        [ definition "ListCommands" "Lists supported Jarvis commands." [ ReadOnly ] false false false
          definition "ListProjects" "Lists configured projects." [ ReadOnly ] false false false
          definition "GetProjectDetails" "Reads project summary details and special files." [ ReadOnly ] false false false
          definition "ListDirectory" "Lists files and folders in a project directory." [ ReadOnly ] false false false
          definition "SearchFiles" "Searches project file names." [ ReadOnly ] false false false
          definition "SearchText" "Searches project file contents." [ ReadOnly ] false false false
          definition "ReadFile" "Reads one file." [ ReadOnly ] false false false
          definition "ReadFiles" "Reads multiple files." [ ReadOnly ] false false false
          definition "WriteFile" "Writes or appends one file." [ WorkspaceWrite ] true true false
          definition "PatchFile" "Applies an atomic unified diff to one file." [ WorkspaceWrite ] true true true
          definition "RunCommand" "Runs a bounded local process." [ ProcessExecution ] true true false
          definition "ListProjectTasks" "Lists locally configured project tasks." [ ReadOnly ] false false false
          definition "RunProjectTask" "Runs a locally configured project task." [ ProcessExecution ] true true false
          definition "GetGitStatus" "Reads git status." [ ReadOnly ] false false false
          definition "GetGitDiff" "Reads git diff." [ ReadOnly ] false false false
          definition "GitCommit" "Creates a local git commit." [ VersionControlWrite ] true true false
          definition "StartJob" "Starts a long-running process." [ ProcessExecution ] true true false
          definition "ListJobs" "Lists known jobs." [ ReadOnly ] false false false
          definition "GetJobResult" "Reads buffered job output." [ ReadOnly ] false false false
          definition "CancelJob" "Cancels a running job." [ ProcessExecution ] true true false ]

    let private toCapability (definition: CommandDefinition) : CommandCapability =
        { Name = definition.Name
          Description = definition.Description
          Permissions = definition.Permissions
          MutatesState = definition.MutatesState
          RequiresConfirmation = definition.RequiresConfirmation
          SupportsDryRun = definition.SupportsDryRun
          MaxInputBytes = None
          MaxOutputBytes = Some maxResponseBytes
          InputSchemaJson = None
          OutputSchemaJson = None }

    let capabilities = commandDefinitions |> List.map toCapability

    let tryFindDefinition name =
        commandDefinitions |> List.tryFind (fun definition -> definition.Name = name)


    let listCommandsResult =
        { ProtocolVersion = version
          Commands = capabilities }

