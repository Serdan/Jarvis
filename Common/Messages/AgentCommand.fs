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

type ReadFileCommand =
    { ProjectName: string
      FilePath: string }

type ReadFilesCommand =
    { ProjectName: string
      FilePaths: string list }

type WriteFileCommand =
    { ProjectName: string
      FilePath: string
      Content: string
      FileWriteMode: FileWriteMode
      ExpectedHash: string option }

type PatchFileCommand =
    { ProjectName: string
      FilePath: string
      ExpectedHash: string option
      Format: PatchFormat
      Patch: string }

type RunCommandCommand =
    { ProjectName: string
      Executable: string
      Args: string list
      WorkingDirectory: string option
      TimeoutSeconds: int option
      MaxOutputBytes: int option }

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

type GetJobResultCommand =
    { JobId: string
      FromOffset: int option }

type JobResult =
    { JobId: string
      Status: JobStatus
      StdOut: string
      StdErr: string
      OutputOffset: int
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
    | GetGitStatusCommand of GitStatusCommand
    | GetGitDiffCommand of GitDiffCommand
    | GitCommitCommand of GitCommitCommand
    | StartJobCommand of StartJobCommand
    | ListJobsCommand of ListJobsCommand
    | GetJobResultCommand of GetJobResultCommand
    | CancelJobCommand of CancelJobCommand

type AgentMessage<'a> = { Key: string; Command: 'a }
type AgentMessage = { Key: string; Command: AgentCommand }

module AgentProtocol =
    let version = "2.0"

    let private capability name description permissions mutates requiresConfirmation supportsDryRun =
        { Name = name
          Description = description
          Permissions = permissions
          MutatesState = mutates
          RequiresConfirmation = requiresConfirmation
          SupportsDryRun = supportsDryRun
          MaxInputBytes = None
          MaxOutputBytes = None
          InputSchemaJson = None
          OutputSchemaJson = None }

    let capabilities =
        [ capability "ListCommands" "Lists supported Jarvis commands." [ ReadOnly ] false false false
          capability "ListProjects" "Lists configured projects." [ ReadOnly ] false false false
          capability "GetProjectDetails" "Reads project summary details and special files." [ ReadOnly ] false false false
          capability "ListDirectory" "Lists files and folders in a project directory." [ ReadOnly ] false false false
          capability "SearchFiles" "Searches project file names." [ ReadOnly ] false false false
          capability "SearchText" "Searches project file contents." [ ReadOnly ] false false false
          capability "ReadFile" "Reads one file." [ ReadOnly ] false false false
          capability "ReadFiles" "Reads multiple files." [ ReadOnly ] false false false
          capability "WriteFile" "Writes or appends one file." [ WorkspaceWrite ] true true true
          capability "PatchFile" "Applies an atomic unified diff to one file." [ WorkspaceWrite ] true true true
          capability "RunCommand" "Runs a bounded local process." [ ProcessExecution ] true true true
          capability "GetGitStatus" "Reads git status." [ ReadOnly ] false false false
          capability "GetGitDiff" "Reads git diff." [ ReadOnly ] false false false
          capability "GitCommit" "Creates a local git commit." [ VersionControlWrite ] true true true
          capability "StartJob" "Starts a long-running process." [ ProcessExecution ] true true true
          capability "ListJobs" "Lists known jobs." [ ReadOnly ] false false false
          capability "GetJobResult" "Reads buffered job output." [ ReadOnly ] false false false
          capability "CancelJob" "Cancels a running job." [ ProcessExecution ] true true false ]

    let listCommandsResult =
        { ProtocolVersion = version
          Commands = capabilities }

module AgentMessage =
    let create f (message: AgentMessage<'a>) =
        { Key = message.Key
          Command = f message.Command }
