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
      OperationId: string
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

module AgentProtocol =
    let version = "3.0"
    let defaultPatchFuzzyContextLines = 3
    let maxResponseBytes = 900 * 1024

    let private definition name operationId description permissions mutates requiresConfirmation supportsDryRun : CommandDefinition =
        { Name = name
          OperationId = operationId
          Description = description
          Permissions = permissions
          MutatesState = mutates
          RequiresConfirmation = requiresConfirmation
          SupportsDryRun = supportsDryRun }

    let commandDefinitions =
        [ definition "ListCommands" "listCommands" "Lists supported Jarvis commands." [ ReadOnly ] false false false
          definition "ListProjects" "listProjects" "Lists configured projects." [ ReadOnly ] false false false
          definition "GetProjectDetails" "getProjectDetails" "Reads project summary details and special files." [ ReadOnly ] false false false
          definition "ListDirectory" "listDirectory" "Lists files and folders in a project directory." [ ReadOnly ] false false false
          definition "SearchFiles" "searchFiles" "Searches project file names." [ ReadOnly ] false false false
          definition "SearchText" "searchText" "Searches project file contents." [ ReadOnly ] false false false
          definition "ReadFile" "readFile" "Reads one file." [ ReadOnly ] false false false
          definition "ReadFiles" "readFiles" "Reads multiple files." [ ReadOnly ] false false false
          definition "WriteFile" "writeFile" "Writes or appends one file." [ WorkspaceWrite ] true true false
          definition "PatchFile" "patchFile" "Applies an atomic unified diff to one file." [ WorkspaceWrite ] true true true
          definition "RunCommand" "runCommand" "Runs a bounded local process." [ ProcessExecution ] true true false
          definition "ListProjectTasks" "listProjectTasks" "Lists locally configured project tasks." [ ReadOnly ] false false false
          definition "RunProjectTask" "runProjectTask" "Runs a locally configured project task." [ ProcessExecution ] true true false
          definition "GetGitStatus" "getGitStatus" "Reads git status." [ ReadOnly ] false false false
          definition "GetGitDiff" "getGitDiff" "Reads git diff." [ ReadOnly ] false false false
          definition "GitCommit" "gitCommit" "Creates a local git commit." [ VersionControlWrite ] true true false
          definition "StartJob" "startJob" "Starts a long-running process." [ ProcessExecution ] true true false
          definition "ListJobs" "listJobs" "Lists known jobs." [ ReadOnly ] false false false
          definition "GetJobResult" "getJobResult" "Reads buffered job output." [ ReadOnly ] false false false
          definition "CancelJob" "cancelJob" "Cancels a running job." [ ProcessExecution ] true true false ]

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

    let legacyRoute name =
        match tryFindDefinition name with
        | Some definition -> "/" + definition.OperationId
        | None -> invalidArg "name" $"Unknown Jarvis command: {name}"

    let listCommandsResult =
        { ProtocolVersion = version
          Commands = capabilities }

