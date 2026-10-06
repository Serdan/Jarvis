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

type ReadImageCommand =
    { ProjectName: string
      FilePath: string }

type ReadImageResult =
    { FilePath: string
      MimeType: string
      Data: byte[] }

type ImportFileCommand =
    { ProjectName: string
      FilePath: string
      DownloadUrl: string
      FileId: string
      MimeType: string option
      FileName: string option
      Overwrite: bool
      CreateParents: bool
      MaxBytes: int64 option }

type ImportFileResult =
    { FilePath: string
      FileId: string
      BytesWritten: int64
      MimeType: string option
      Sha256: string }

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
      Reason: string option
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

type ListSkillsCommand =
    { ProjectName: string }

type SkillSummary =
    { Name: string
      Description: string option }

type ListSkillsResult =
    { Skills: SkillSummary list }

type GetSkillCommand =
    { ProjectName: string
      SkillName: string }

type GetSkillResult =
    { Name: string
      Content: string }

type CreateSkillCommand =
    { ProjectName: string
      SkillName: string
      Content: string
      Overwrite: bool }

type CreateSkillResult =
    { Name: string
      Path: string
      Hash: string
      Overwritten: bool }

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
      Reason: string option
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
      Reason: string option
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

type MessageCommand =
    { ProjectName: string option
      Message: string }

type GetClientActivityCommand =
    { ProjectName: string option
      Limit: int option }

type ClientActivityEntry =
    { StartedAt: DateTimeOffset
      AgeMs: int64
      ProjectName: string option
      CommandName: string option
      Message: string
      Reason: string option
      Detail: string option
      Status: string
      DurationMs: int64 option
      Result: string option
      Error: string option }

type GetClientActivityResult =
    { Entries: ClientActivityEntry list }

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
    | ReadImageCommand of ReadImageCommand
    | ImportFileCommand of ImportFileCommand
    | WriteFileCommand of WriteFileCommand
    | PatchFileCommand of PatchFileCommand
    | RunCommandCommand of RunCommandCommand
    | ListProjectTasksCommand of ListProjectTasksCommand
    | RunProjectTaskCommand of RunProjectTaskCommand
    | ListSkillsCommand of ListSkillsCommand
    | GetSkillCommand of GetSkillCommand
    | CreateSkillCommand of CreateSkillCommand
    | GetGitStatusCommand of GitStatusCommand
    | GetGitDiffCommand of GitDiffCommand
    | GitCommitCommand of GitCommitCommand
    | StartJobCommand of StartJobCommand
    | ListJobsCommand of ListJobsCommand
    | GetJobResultCommand of GetJobResultCommand
    | CancelJobCommand of CancelJobCommand
    | MessageCommand of MessageCommand
    | GetClientActivityCommand of GetClientActivityCommand

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
        | ReadImageCommand _ -> "ReadImageCommand"
        | ImportFileCommand _ -> "ImportFileCommand"
        | WriteFileCommand _ -> "WriteFileCommand"
        | PatchFileCommand _ -> "PatchFileCommand"
        | RunCommandCommand _ -> "RunCommandCommand"
        | ListProjectTasksCommand _ -> "ListProjectTasksCommand"
        | RunProjectTaskCommand _ -> "RunProjectTaskCommand"
        | ListSkillsCommand _ -> "ListSkillsCommand"
        | GetSkillCommand _ -> "GetSkillCommand"
        | CreateSkillCommand _ -> "CreateSkillCommand"
        | GetGitStatusCommand _ -> "GetGitStatusCommand"
        | GetGitDiffCommand _ -> "GetGitDiffCommand"
        | GitCommitCommand _ -> "GitCommitCommand"
        | StartJobCommand _ -> "StartJobCommand"
        | ListJobsCommand _ -> "ListJobsCommand"
        | GetJobResultCommand _ -> "GetJobResultCommand"
        | CancelJobCommand _ -> "CancelJobCommand"
        | MessageCommand _ -> "MessageCommand"
        | GetClientActivityCommand _ -> "GetClientActivityCommand"

    let displayName = function
        | ListCommandsCommand -> "ListCommands"
        | ListProjectsCommand -> "ListProjects"
        | GetProjectDetailsCommand _ -> "GetProjectDetails"
        | ListDirectoryCommand _ -> "ListDirectory"
        | SearchFilesCommand _ -> "SearchFiles"
        | SearchTextCommand _ -> "SearchText"
        | ReadFileCommand _ -> "ReadFile"
        | ReadFilesCommand _ -> "ReadFiles"
        | ReadImageCommand _ -> "ReadImage"
        | ImportFileCommand _ -> "ImportFile"
        | WriteFileCommand _ -> "WriteFile"
        | PatchFileCommand _ -> "PatchFile"
        | RunCommandCommand _ -> "RunCommand"
        | ListProjectTasksCommand _ -> "ListProjectTasks"
        | RunProjectTaskCommand _ -> "RunTask"
        | ListSkillsCommand _ -> "ListSkills"
        | GetSkillCommand _ -> "GetSkill"
        | CreateSkillCommand _ -> "CreateSkill"
        | GetGitStatusCommand _ -> "GitStatus"
        | GetGitDiffCommand _ -> "GitDiff"
        | GitCommitCommand _ -> "GitCommit"
        | StartJobCommand _ -> "StartJob"
        | ListJobsCommand _ -> "ListJobs"
        | GetJobResultCommand _ -> "GetJobResult"
        | CancelJobCommand _ -> "CancelJob"
        | MessageCommand _ -> "Message"
        | GetClientActivityCommand _ -> "GetClientActivity"

    let projectName = function
        | ListCommandsCommand
        | ListProjectsCommand
        | GetJobResultCommand _
        | CancelJobCommand _ -> None
        | MessageCommand cmd -> cmd.ProjectName
        | GetClientActivityCommand cmd -> cmd.ProjectName
        | GetProjectDetailsCommand cmd -> Some cmd.ProjectName
        | ListDirectoryCommand cmd -> Some cmd.ProjectName
        | SearchFilesCommand cmd -> Some cmd.ProjectName
        | SearchTextCommand cmd -> Some cmd.ProjectName
        | ReadFileCommand cmd -> Some cmd.ProjectName
        | ReadFilesCommand cmd -> Some cmd.ProjectName
        | ReadImageCommand cmd -> Some cmd.ProjectName
        | ImportFileCommand cmd -> Some cmd.ProjectName
        | WriteFileCommand cmd -> Some cmd.ProjectName
        | PatchFileCommand cmd -> Some cmd.ProjectName
        | RunCommandCommand cmd -> Some cmd.ProjectName
        | ListProjectTasksCommand cmd -> Some cmd.ProjectName
        | RunProjectTaskCommand cmd -> Some cmd.ProjectName
        | ListSkillsCommand cmd -> Some cmd.ProjectName
        | GetSkillCommand cmd -> Some cmd.ProjectName
        | CreateSkillCommand cmd -> Some cmd.ProjectName
        | GetGitStatusCommand cmd -> Some cmd.ProjectName
        | GetGitDiffCommand cmd -> Some cmd.ProjectName
        | GitCommitCommand cmd -> Some cmd.ProjectName
        | StartJobCommand cmd -> Some cmd.ProjectName
        | ListJobsCommand cmd -> cmd.ProjectName

    let private compact (value: string) =
        value.Replace("\r", " ").Replace("\n", " ").Trim()

    let private truncate maxLength value =
        let value = compact value
        if value.Length <= maxLength then value
        else value.Substring(0, maxLength - 1) + "…"

    let private quoted value =
        "\"" + truncate 48 value + "\""

    let private commandPreview executable args =
        let argsPreview =
            args
            |> List.truncate 2
            |> List.map (truncate 28)

        let suffix =
            match args, argsPreview with
            | [], _ -> ""
            | original, preview when original.Length > preview.Length -> " " + String.concat " " preview + " …"
            | _, preview -> " " + String.concat " " preview

        truncate 72 (compact executable + suffix)

    let private shortId value =
        let value = compact value
        if value.Length <= 8 then value else value.Substring(0, 8)

    let private fullCommand executable args =
        executable :: args
        |> List.map compact
        |> List.filter (String.IsNullOrWhiteSpace >> not)
        |> String.concat " "

    let fullDetail = function
        | ListDirectoryCommand cmd -> Some(compact cmd.FolderPath)
        | SearchFilesCommand cmd -> Some(sprintf "\"%s\"" (compact cmd.Query))
        | SearchTextCommand cmd -> Some(sprintf "\"%s\"" (compact cmd.Query))
        | ReadFileCommand cmd -> Some(compact cmd.FilePath)
        | ReadFilesCommand cmd -> Some(String.concat ", " (cmd.FilePaths |> List.map compact))
        | ReadImageCommand cmd -> Some(compact cmd.FilePath)
        | ImportFileCommand cmd -> Some(compact cmd.FilePath)
        | WriteFileCommand cmd -> Some(compact cmd.FilePath)
        | PatchFileCommand cmd -> Some(compact cmd.FilePath)
        | RunCommandCommand cmd -> Some(fullCommand cmd.Executable cmd.Args)
        | RunProjectTaskCommand cmd -> Some(compact cmd.TaskName)
        | GetSkillCommand cmd -> Some(compact cmd.SkillName)
        | CreateSkillCommand cmd -> Some(compact cmd.SkillName)
        | ListSkillsCommand _ -> None
        | GetGitDiffCommand cmd -> cmd.Path |> Option.map compact
        | GitCommitCommand cmd -> Some(compact cmd.Message)
        | StartJobCommand cmd -> Some(fullCommand cmd.Executable cmd.Args)
        | GetJobResultCommand cmd -> Some(compact cmd.JobId)
        | CancelJobCommand cmd -> Some(compact cmd.JobId)
        | MessageCommand cmd -> Some(compact cmd.Message)
        | GetClientActivityCommand _ -> None
        | ListCommandsCommand
        | ListProjectsCommand
        | GetProjectDetailsCommand _
        | ListProjectTasksCommand _
        | GetGitStatusCommand _
        | ListJobsCommand _ -> None

    let detail = function
        | ListDirectoryCommand cmd -> Some(truncate 64 cmd.FolderPath)
        | SearchFilesCommand cmd -> Some(quoted cmd.Query)
        | SearchTextCommand cmd -> Some(quoted cmd.Query)
        | ReadFileCommand cmd -> Some(truncate 64 cmd.FilePath)
        | ReadFilesCommand cmd -> Some $"{cmd.FilePaths.Length} files"
        | ReadImageCommand cmd -> Some(truncate 64 cmd.FilePath)
        | ImportFileCommand cmd -> Some(truncate 64 cmd.FilePath)
        | WriteFileCommand cmd -> Some(truncate 64 cmd.FilePath)
        | PatchFileCommand cmd -> Some(truncate 64 cmd.FilePath)
        | RunCommandCommand cmd -> Some(commandPreview cmd.Executable cmd.Args)
        | RunProjectTaskCommand cmd -> Some(truncate 48 cmd.TaskName)
        | GetSkillCommand cmd -> Some(truncate 48 cmd.SkillName)
        | CreateSkillCommand cmd -> Some(truncate 48 cmd.SkillName)
        | ListSkillsCommand _ -> None
        | GetGitDiffCommand cmd -> cmd.Path |> Option.map (truncate 64)
        | GitCommitCommand cmd -> Some(quoted cmd.Message)
        | StartJobCommand cmd -> Some(commandPreview cmd.Executable cmd.Args)
        | GetJobResultCommand cmd -> Some(shortId cmd.JobId)
        | CancelJobCommand cmd -> Some(shortId cmd.JobId)
        | MessageCommand cmd -> Some(truncate 64 cmd.Message)
        | GetClientActivityCommand _ -> None
        | ListCommandsCommand
        | ListProjectsCommand
        | GetProjectDetailsCommand _
        | ListProjectTasksCommand _
        | GetGitStatusCommand _
        | ListJobsCommand _ -> None

    let fullReason = function
        | RunCommandCommand cmd ->
            cmd.Reason
            |> Option.map compact
            |> Option.filter (String.IsNullOrWhiteSpace >> not)
        | StartJobCommand cmd ->
            cmd.Reason
            |> Option.map compact
            |> Option.filter (String.IsNullOrWhiteSpace >> not)
        | _ -> None

    let reason = function
        | RunCommandCommand cmd ->
            cmd.Reason
            |> Option.map (truncate 64)
            |> Option.filter (String.IsNullOrWhiteSpace >> not)
        | StartJobCommand cmd ->
            cmd.Reason
            |> Option.map (truncate 64)
            |> Option.filter (String.IsNullOrWhiteSpace >> not)
        | _ -> None

    let invocation command =
        match reason command, detail command with
        | Some reason, Some detail when not (String.IsNullOrWhiteSpace detail) ->
            $"{reason} · {detail}"
        | Some reason, _ ->
            reason
        | None, Some detail when not (String.IsNullOrWhiteSpace detail) ->
            $"{displayName command}({detail})"
        | None, _ ->
            displayName command

    let activityLabel command =
        match projectName command with
        | Some projectName -> $"@{projectName} {invocation command}"
        | None -> invocation command

module AgentProtocol =
    let version = "3.7"
    let defaultPatchFuzzyContextLines = 3
    let maxResponseBytes = 900 * 1024
    let maxImageBytes = 8 * 1024 * 1024
    let defaultMaxImportBytes = 32L * 1024L * 1024L

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
          definition "ReadImage" "Reads one PNG, JPEG, or WebP image for model vision." [ ReadOnly ] false false false
          definition "ImportFile" "Downloads a ChatGPT-provided file into a project path." [ WorkspaceWrite; NetworkAccess ] true true false
          definition "WriteFile" "Writes or appends one file." [ WorkspaceWrite ] true true false
          definition "PatchFile" "Applies an atomic unified diff to one file." [ WorkspaceWrite ] true true true
          definition "RunCommand" "Runs a bounded local process." [ ProcessExecution ] true true false
          definition "ListProjectTasks" "Lists locally configured project tasks." [ ReadOnly ] false false false
          definition "RunProjectTask" "Runs a locally configured project task." [ ProcessExecution ] true true false
          definition "ListSkills" "Lists project-local Jarvis skills." [ ReadOnly ] false false false
          definition "GetSkill" "Reads a project-local Jarvis skill." [ ReadOnly ] false false false
          definition "CreateSkill" "Creates a project-local Jarvis skill." [ WorkspaceWrite ] true true false
          definition "GetGitStatus" "Reads git status." [ ReadOnly ] false false false
          definition "GetGitDiff" "Reads git diff." [ ReadOnly ] false false false
          definition "GitCommit" "Creates a local git commit." [ VersionControlWrite ] true true false
          definition "StartJob" "Starts a long-running process." [ ProcessExecution ] true true false
          definition "ListJobs" "Lists known jobs." [ ReadOnly ] false false false
          definition "GetJobResult" "Reads buffered job output." [ ReadOnly ] false false false
          definition "CancelJob" "Cancels a running job." [ ProcessExecution ] true true false
          definition "Message" "Adds an informational message to client activity." [] true false false
          definition "GetClientActivity" "Reads recent in-memory Jarvis client activity, optionally filtered by project." [ ReadOnly ] false false false ]

    let private toCapability (definition: CommandDefinition) : CommandCapability =
        { Name = definition.Name
          Description = definition.Description
          Permissions = definition.Permissions
          MutatesState = definition.MutatesState
          RequiresConfirmation = definition.RequiresConfirmation
          SupportsDryRun = definition.SupportsDryRun
          MaxInputBytes = None
          MaxOutputBytes =
              if definition.Name = "ReadImage" then Some maxImageBytes
              else Some maxResponseBytes
          InputSchemaJson = None
          OutputSchemaJson = None }

    let capabilities = commandDefinitions |> List.map toCapability

    let tryFindDefinition name =
        commandDefinitions |> List.tryFind (fun definition -> definition.Name = name)


    let listCommandsResult =
        { ProtocolVersion = version
          Commands = capabilities }

