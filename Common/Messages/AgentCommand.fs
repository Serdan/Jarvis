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

type ListWorktreesCommand = { ProjectName: string }
type GetWorktreeCommand = { ProjectName: string; WorktreePath: string }
type CreateWorktreeCommand =
    { ProjectName: string
      Name: string
      Branch: string
      BaseRef: string }
type InspectWorktreeRemovalCommand =
    { ProjectName: string
      WorktreePath: string
      TargetRef: string option }
type RemoveWorktreeCommand =
    { ProjectName: string
      WorktreePath: string
      TargetRef: string option }


type ListTasksCommand = { ProjectName: string; Status: string option; Owner: string option; DependsOn: string option; NextActor: string option }
type GetTaskCommand = { ProjectName: string; TaskId: string }
type CompareWorktreeTasksCommand = { ProjectName: string; NextActor: string option }
type GetAgentAssignmentCommand = { ProjectName: string; Agent: string }
type GetOrchestrationSnapshotCommand =
    { ProjectName: string
      Status: string option
      Owner: string option
      NextActor: string option
      ReviewerOnly: bool
      Offset: int
      Limit: int
      IncludeRetirement: bool }
type CreateTaskCommand =
    { ProjectName: string
      TaskId: string option
      Title: string
      Html: string
      Dependencies: string list }
type UpdateTaskCommand =
    { ProjectName: string
      TaskId: string
      ExpectedRevision: string option
      Title: string option
      Status: string option
      Dependencies: string list option
      DescriptionCommit: string option
      AssignmentInstructions: string option
      AssignmentBaseCommit: string option
      ExpectedAssignmentRevision: string option
      NextActor: string option
      ActorId: string option }
type AssignTaskCommand =
    { ProjectName: string
      Agent: string
      TaskId: string
      WorktreeName: string option
      Branch: string option
      Instructions: string
      BaseCommit: string option
      ExpectedRevision: string option
      ExpectedAssignmentRevision: string option
      ActorId: string }
type RetireWorktreeCommand = { ProjectName: string; WorktreePath: string; TargetRef: string option; DeleteBranch: bool; DryRun: bool }
type BeginTaskCommand = { ProjectName: string; TaskId: string; Agent: string; AssignmentRevision: string; ExpectedRevision: string option }
type AcknowledgeTaskCommand =
    { ProjectName: string
      TaskId: string
      Agent: string
      AssignmentRevision: string
      ExpectedRevision: string option }
type ClaimTaskCommand =
    { ProjectName: string
      TaskId: string
      ExpectedRevision: string option
      Owner: string
      AllowReassign: bool }
type CheckpointTaskCommand =
    { ProjectName: string
      TaskId: string
      ExpectedRevision: string option
      Status: string
      Summary: string
      NextAction: string
      Verified: string list
      Blocker: string option
      LastCommit: string option
      DescriptionCommit: string option
      AcknowledgedAssignmentRevision: string option
      ActorId: string option }
type CompleteTaskCommand =
    { ProjectName: string
      TaskId: string
      ExpectedRevision: string option
      Verified: string list
      FinalCommit: string option
      AcknowledgedAssignmentRevision: string option
      ActorId: string option }

type V2TaskReadCommand =
    { ProjectName: string
      AgentId: string
      Action: string
      TaskId: string
      AssignmentGeneration: int
      CheckpointSequence: int64
      Cursor: string
      PageSize: int
      BeforeOid: string
      AfterOid: string
      RelativePath: string }

type V2TaskWriteCommand =
    { ProjectName: string
      Action: string
      OperationId: string
      TaskId: string
      AttemptId: string
      ActorId: string
      OwnerId: string
      ReviewerId: string
      Title: string
      Html: string
      ExpectedVersion: int
      ExpectedAgentVersion: int
      ExpectedTaskVersion: int
      AssignmentGeneration: int
      Summary: string
      NextAction: string
      Reason: string
      BaseOid: string
      TargetRef: string
      SourceRef: string
      AllowDetached: bool
      AllowUncommitted: bool
      DecisionSequence: int64
      Evidence: string }

type V2LedgerReadCommand =
    { ProjectName: string
      Action: string
      OperationId: string }

type V2LedgerWriteCommand =
    { ProjectName: string
      Action: string
      OperationId: string
      ProjectId: string
      LedgerId: string
      Epoch: int
      RemoteName: string
      ApprovedFetchUrl: string
      ApprovedPushUrl: string
      ExpectedTrustedHead: string
      ExpectedRequestHash: string
      AcknowledgeVisibility: bool }

type V2IntegrationReadCommand =
    { ProjectName: string
      TaskId: string
      ActionId: string }

type V2IntegrationWriteCommand =
    { ProjectName: string
      Action: string
      OperationId: string
      TaskId: string
      ActionId: string
      RequestId: string
      ExpectedVersion: int
      TargetRef: string
      ExpectedTargetHead: string }

type V2ReviewReadCommand =
    { ProjectName: string
      Action: string
      TaskId: string
      RequestId: string
      ReviewerId: string }

type V2ReviewWriteCommand =
    { ProjectName: string
      Action: string
      OperationId: string
      TaskId: string
      RequestId: string
      ActorId: string
      ReviewerId: string
      ExpectedVersion: int
      Generation: int
      SourceRef: string
      HeadOid: string
      Summary: string
      Verdict: string
      Feedback: string
      TestEvidence: string
      Inspected: bool
      Tested: bool
      TestDetails: string }

type GetTaskHistoryCommand =
    { ProjectName: string
      TaskId: string
      Limit: int option }



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

type ExportFileCommand =
    { ProjectName: string
      FilePath: string }

/// The private client-to-server payload. The public MCP result is ExportFileResult.
type ExportFilePayload =
    { FilePath: string
      Data: byte[] }

type ExportFileResult =
    { FilePath: string
      FileName: string
      MimeType: string
      Bytes: int64
      DownloadUrl: string
      ExpiresAt: DateTimeOffset }

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

type UpdateClientCommand = struct end

type UpdateClientResult =
    { RuntimeIdentifier: string
      Sha256: string
      DownloadUrl: string
      RestartScheduled: bool }

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

type AttachWorkTaskCommand =
    { ProjectName: string
      TaskId: string
      TaskPath: string }

type GetWorkTaskCommand =
    { ProjectName: string
      TaskId: string }

type ListWorkTasksCommand =
    { ProjectName: string }

type CheckpointWorkTaskCommand =
    { ProjectName: string
      TaskId: string
      Status: string
      Summary: string
      NextAction: string
      Verified: string list
      Blocker: string option
      LastCommit: string option }

type CompleteWorkTaskCommand =
    { ProjectName: string
      TaskId: string }

type WorkTask =
    { TaskId: string
      TaskPath: string
      TaskCommit: string
      CurrentTaskCommit: string option
      TaskChanged: bool
      TaskDirty: bool
      Status: string
      Summary: string option
      NextAction: string option
      Verified: string list
      Blocker: string option
      CreatedAt: DateTimeOffset
      UpdatedAt: DateTimeOffset
      LastCommit: string option
      TaskContent: string option }

type ListWorkTasksResult = { Tasks: WorkTask list }

type CompleteWorkTaskResult =
    { TaskId: string
      TaskPath: string
      TaskCommit: string
      FinalCommit: string
      CompletedAt: DateTimeOffset }

type ListSkillsCommand =
    { ProjectName: string }

type SkillSummary =
    { Name: string
      Scope: string
      Description: string option }

type ListSkillsResult =
    { Skills: SkillSummary list }

type GetSkillCommand =
    { ProjectName: string
      SkillName: string }

type GetSkillResult =
    { Name: string
      Scope: string
      Content: string }

type CreateSkillCommand =
    { ProjectName: string
      SkillName: string
      Content: string
      Overwrite: bool }

type CreateSkillResult =
    { Name: string
      Scope: string
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
      Limit: int option
      IncludeObservers: bool option }

type ClientActivityEntry =
    { Origin: string
      StartedAt: DateTimeOffset
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

type V2LedgerFailureInfo =
    { Code: string
      Message: string
      OperationId: string option
      VerifiedLedgerHead: string option }

[<JsonConverter(typeof<UnionConverter<AgentError>>)>]
type AgentError =
    | NotFound of string
    | PermissionDenied of string
    | ConfirmationRequired of ConfirmationRequest
    | ValidationFailed of string
    | Conflict of string
    | ExecutionFailed of string
    | OutputTruncated of string
    | V2LedgerFailure of V2LedgerFailureInfo

type InstructionDocument =
    { Scope: string
      Path: string
      Sha256: string
      Content: string }

type AgentCommandResponse =
    { Result: string option
      Error: AgentError option
      Instructions: InstructionDocument list }

[<JsonConverter(typeof<UnionConverter<AgentCommand>>)>]
type AgentCommand =
    | ListCommandsCommand
    | ListProjectsCommand
    | ListWorktreesCommand of ListWorktreesCommand
    | GetWorktreeCommand of GetWorktreeCommand
    | CreateWorktreeCommand of CreateWorktreeCommand
    | InspectWorktreeRemovalCommand of InspectWorktreeRemovalCommand
    | RemoveWorktreeCommand of RemoveWorktreeCommand
    | ListTasksCommand of ListTasksCommand
    | GetTaskCommand of GetTaskCommand
    | CompareWorktreeTasksCommand of CompareWorktreeTasksCommand
    | GetAgentAssignmentCommand of GetAgentAssignmentCommand
    | GetOrchestrationSnapshotCommand of GetOrchestrationSnapshotCommand
    | AssignTaskCommand of AssignTaskCommand
    | AcknowledgeTaskCommand of AcknowledgeTaskCommand
    | BeginTaskCommand of BeginTaskCommand
    | RetireWorktreeCommand of RetireWorktreeCommand
    | CreateTaskCommand of CreateTaskCommand
    | UpdateTaskCommand of UpdateTaskCommand
    | ClaimTaskCommand of ClaimTaskCommand
    | CheckpointTaskCommand of CheckpointTaskCommand
    | CompleteTaskCommand of CompleteTaskCommand
    | GetTaskHistoryCommand of GetTaskHistoryCommand
    | V2TaskReadCommand of V2TaskReadCommand
    | V2TaskWriteCommand of V2TaskWriteCommand
    | V2LedgerReadCommand of V2LedgerReadCommand
    | V2LedgerWriteCommand of V2LedgerWriteCommand
    | V2IntegrationReadCommand of V2IntegrationReadCommand
    | V2IntegrationWriteCommand of V2IntegrationWriteCommand
    | V2ReviewReadCommand of V2ReviewReadCommand
    | V2ReviewWriteCommand of V2ReviewWriteCommand
    | GetProjectDetailsCommand of GetProjectDetailsCommand
    | ListDirectoryCommand of ListDirectoryCommand
    | SearchFilesCommand of SearchFilesCommand
    | SearchTextCommand of SearchTextCommand
    | ReadFileCommand of ReadFileCommand
    | ReadFilesCommand of ReadFilesCommand
    | ReadImageCommand of ReadImageCommand
    | ExportFileCommand of ExportFileCommand
    | ImportFileCommand of ImportFileCommand
    | UpdateClientCommand
    | WriteFileCommand of WriteFileCommand
    | PatchFileCommand of PatchFileCommand
    | RunCommandCommand of RunCommandCommand
    | ListProjectTasksCommand of ListProjectTasksCommand
    | RunProjectTaskCommand of RunProjectTaskCommand
    | AttachWorkTaskCommand of AttachWorkTaskCommand
    | GetWorkTaskCommand of GetWorkTaskCommand
    | ListWorkTasksCommand of ListWorkTasksCommand
    | CheckpointWorkTaskCommand of CheckpointWorkTaskCommand
    | CompleteWorkTaskCommand of CompleteWorkTaskCommand
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
        | ListWorktreesCommand _ -> "ListWorktreesCommand"
        | GetWorktreeCommand _ -> "GetWorktreeCommand"
        | CreateWorktreeCommand _ -> "CreateWorktreeCommand"
        | InspectWorktreeRemovalCommand _ -> "InspectWorktreeRemovalCommand"
        | RemoveWorktreeCommand _ -> "RemoveWorktreeCommand"
        | ListTasksCommand _ -> "ListTasksCommand"
        | GetTaskCommand _ -> "GetTaskCommand"
        | CompareWorktreeTasksCommand _ -> "CompareWorktreeTasksCommand"
        | GetAgentAssignmentCommand _ -> "GetAgentAssignmentCommand"
        | GetOrchestrationSnapshotCommand _ -> "GetOrchestrationSnapshotCommand"
        | AssignTaskCommand _ -> "AssignTaskCommand"
        | AcknowledgeTaskCommand _ -> "AcknowledgeTaskCommand"
        | BeginTaskCommand _ -> "BeginTaskCommand"
        | RetireWorktreeCommand _ -> "RetireWorktreeCommand"
        | CreateTaskCommand _ -> "CreateTaskCommand"
        | UpdateTaskCommand _ -> "UpdateTaskCommand"
        | ClaimTaskCommand _ -> "ClaimTaskCommand"
        | CheckpointTaskCommand _ -> "CheckpointTaskCommand"
        | CompleteTaskCommand _ -> "CompleteTaskCommand"
        | GetTaskHistoryCommand _ -> "GetTaskHistoryCommand"
        | V2TaskReadCommand _ -> "V2TaskReadCommand"
        | V2TaskWriteCommand _ -> "V2TaskWriteCommand"
        | V2LedgerReadCommand _ -> "V2LedgerReadCommand"
        | V2LedgerWriteCommand _ -> "V2LedgerWriteCommand"
        | V2IntegrationReadCommand _ -> "V2IntegrationReadCommand"
        | V2IntegrationWriteCommand _ -> "V2IntegrationWriteCommand"
        | V2ReviewReadCommand _ -> "V2ReviewReadCommand"
        | V2ReviewWriteCommand _ -> "V2ReviewWriteCommand"
        | GetProjectDetailsCommand _ -> "GetProjectDetailsCommand"
        | ListDirectoryCommand _ -> "ListDirectoryCommand"
        | SearchFilesCommand _ -> "SearchFilesCommand"
        | SearchTextCommand _ -> "SearchTextCommand"
        | ReadFileCommand _ -> "ReadFileCommand"
        | ReadFilesCommand _ -> "ReadFilesCommand"
        | ReadImageCommand _ -> "ReadImageCommand"
        | ExportFileCommand _ -> "ExportFileCommand"
        | ImportFileCommand _ -> "ImportFileCommand"
        | UpdateClientCommand -> "UpdateClientCommand"
        | WriteFileCommand _ -> "WriteFileCommand"
        | PatchFileCommand _ -> "PatchFileCommand"
        | RunCommandCommand _ -> "RunCommandCommand"
        | ListProjectTasksCommand _ -> "ListProjectTasksCommand"
        | RunProjectTaskCommand _ -> "RunProjectTaskCommand"
        | AttachWorkTaskCommand _ -> "AttachWorkTaskCommand"
        | GetWorkTaskCommand _ -> "GetWorkTaskCommand"
        | ListWorkTasksCommand _ -> "ListWorkTasksCommand"
        | CheckpointWorkTaskCommand _ -> "CheckpointWorkTaskCommand"
        | CompleteWorkTaskCommand _ -> "CompleteWorkTaskCommand"
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

    let protocolName command =
        let commandName = name command
        if commandName.EndsWith("Command", StringComparison.Ordinal) then
            commandName.Substring(0, commandName.Length - "Command".Length)
        else
            commandName

    let displayName = function
        | ListCommandsCommand -> "ListCommands"
        | ListProjectsCommand -> "ListProjects"
        | ListWorktreesCommand _ -> "ListWorktrees"
        | GetWorktreeCommand _ -> "GetWorktree"
        | CreateWorktreeCommand _ -> "CreateWorktree"
        | InspectWorktreeRemovalCommand _ -> "InspectWorktreeRemoval"
        | RemoveWorktreeCommand _ -> "RemoveWorktree"
        | ListTasksCommand _ -> "ListTasks"
        | GetTaskCommand _ -> "GetTask"
        | CompareWorktreeTasksCommand _ -> "CompareWorktreeTasks"
        | GetAgentAssignmentCommand _ -> "GetAgentAssignment"
        | GetOrchestrationSnapshotCommand _ -> "GetOrchestrationSnapshot"
        | AssignTaskCommand _ -> "AssignTask"
        | AcknowledgeTaskCommand _ -> "AcknowledgeTask"
        | BeginTaskCommand _ -> "BeginTask"
        | RetireWorktreeCommand _ -> "RetireWorktree"
        | CreateTaskCommand _ -> "CreateTask"
        | UpdateTaskCommand _ -> "UpdateTask"
        | ClaimTaskCommand _ -> "ClaimTask"
        | CheckpointTaskCommand _ -> "CheckpointTask"
        | CompleteTaskCommand _ -> "CompleteTask"
        | GetTaskHistoryCommand _ -> "GetTaskHistory"
        | V2TaskReadCommand _ -> "V2TaskRead"
        | V2TaskWriteCommand _ -> "V2TaskWrite"
        | V2LedgerReadCommand _ -> "V2LedgerRead"
        | V2LedgerWriteCommand _ -> "V2LedgerWrite"
        | V2IntegrationReadCommand _ -> "V2IntegrationRead"
        | V2IntegrationWriteCommand _ -> "V2IntegrationWrite"
        | V2ReviewReadCommand _ -> "V2ReviewRead"
        | V2ReviewWriteCommand _ -> "V2ReviewWrite"
        | GetProjectDetailsCommand _ -> "GetProjectDetails"
        | ListDirectoryCommand _ -> "ListDirectory"
        | SearchFilesCommand _ -> "SearchFiles"
        | SearchTextCommand _ -> "SearchText"
        | ReadFileCommand _ -> "ReadFile"
        | ReadFilesCommand _ -> "ReadFiles"
        | ReadImageCommand _ -> "ReadImage"
        | ExportFileCommand _ -> "ExportFile"
        | ImportFileCommand _ -> "ImportFile"
        | UpdateClientCommand -> "UpdateClient"
        | WriteFileCommand _ -> "WriteFile"
        | PatchFileCommand _ -> "PatchFile"
        | RunCommandCommand _ -> "RunCommand"
        | ListProjectTasksCommand _ -> "ListProjectTasks"
        | RunProjectTaskCommand _ -> "RunTask"
        | AttachWorkTaskCommand _ -> "AttachWorkTask"
        | GetWorkTaskCommand _ -> "GetWorkTask"
        | ListWorkTasksCommand _ -> "ListWorkTasks"
        | CheckpointWorkTaskCommand _ -> "CheckpointWorkTask"
        | CompleteWorkTaskCommand _ -> "CompleteWorkTask"
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
        | CancelJobCommand _
        | UpdateClientCommand -> None
        | MessageCommand cmd -> cmd.ProjectName
        | GetClientActivityCommand cmd -> cmd.ProjectName
        | ListWorktreesCommand cmd -> Some cmd.ProjectName
        | GetWorktreeCommand cmd -> Some cmd.ProjectName
        | CreateWorktreeCommand cmd -> Some cmd.ProjectName
        | InspectWorktreeRemovalCommand cmd -> Some cmd.ProjectName
        | RemoveWorktreeCommand cmd -> Some cmd.ProjectName
        | ListTasksCommand cmd -> Some cmd.ProjectName
        | GetTaskCommand cmd -> Some cmd.ProjectName
        | CompareWorktreeTasksCommand cmd -> Some cmd.ProjectName
        | GetAgentAssignmentCommand cmd -> Some cmd.ProjectName
        | GetOrchestrationSnapshotCommand cmd -> Some cmd.ProjectName
        | AssignTaskCommand cmd -> Some cmd.ProjectName
        | AcknowledgeTaskCommand cmd -> Some cmd.ProjectName
        | BeginTaskCommand cmd -> Some cmd.ProjectName
        | RetireWorktreeCommand cmd -> Some cmd.ProjectName
        | CreateTaskCommand cmd -> Some cmd.ProjectName
        | UpdateTaskCommand cmd -> Some cmd.ProjectName
        | ClaimTaskCommand cmd -> Some cmd.ProjectName
        | CheckpointTaskCommand cmd -> Some cmd.ProjectName
        | CompleteTaskCommand cmd -> Some cmd.ProjectName
        | GetTaskHistoryCommand cmd -> Some cmd.ProjectName
        | V2TaskReadCommand cmd -> Some cmd.ProjectName
        | V2TaskWriteCommand cmd -> Some cmd.ProjectName
        | V2LedgerReadCommand cmd -> Some cmd.ProjectName
        | V2LedgerWriteCommand cmd -> Some cmd.ProjectName
        | V2IntegrationReadCommand cmd -> Some cmd.ProjectName
        | V2IntegrationWriteCommand cmd -> Some cmd.ProjectName
        | V2ReviewReadCommand cmd -> Some cmd.ProjectName
        | V2ReviewWriteCommand cmd -> Some cmd.ProjectName
        | GetProjectDetailsCommand cmd -> Some cmd.ProjectName
        | ListDirectoryCommand cmd -> Some cmd.ProjectName
        | SearchFilesCommand cmd -> Some cmd.ProjectName
        | SearchTextCommand cmd -> Some cmd.ProjectName
        | ReadFileCommand cmd -> Some cmd.ProjectName
        | ReadFilesCommand cmd -> Some cmd.ProjectName
        | ReadImageCommand cmd -> Some cmd.ProjectName
        | ExportFileCommand cmd -> Some cmd.ProjectName
        | ImportFileCommand cmd -> Some cmd.ProjectName
        | WriteFileCommand cmd -> Some cmd.ProjectName
        | PatchFileCommand cmd -> Some cmd.ProjectName
        | RunCommandCommand cmd -> Some cmd.ProjectName
        | ListProjectTasksCommand cmd -> Some cmd.ProjectName
        | RunProjectTaskCommand cmd -> Some cmd.ProjectName
        | AttachWorkTaskCommand cmd -> Some cmd.ProjectName
        | GetWorkTaskCommand cmd -> Some cmd.ProjectName
        | ListWorkTasksCommand cmd -> Some cmd.ProjectName
        | CheckpointWorkTaskCommand cmd -> Some cmd.ProjectName
        | CompleteWorkTaskCommand cmd -> Some cmd.ProjectName
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
        | ExportFileCommand cmd -> Some(compact cmd.FilePath)
        | ImportFileCommand cmd -> Some(compact cmd.FilePath)
        | UpdateClientCommand -> Some "latest published client"
        | WriteFileCommand cmd -> Some(compact cmd.FilePath)
        | PatchFileCommand cmd -> Some(compact cmd.FilePath)
        | RunCommandCommand cmd -> Some(fullCommand cmd.Executable cmd.Args)
        | RunProjectTaskCommand cmd -> Some(compact cmd.TaskName)
        | AttachWorkTaskCommand cmd -> Some(compact cmd.TaskId)
        | GetWorkTaskCommand cmd -> Some(compact cmd.TaskId)
        | ListWorkTasksCommand _ -> None
        | CheckpointWorkTaskCommand cmd -> Some(compact cmd.TaskId)
        | CompleteWorkTaskCommand cmd -> Some(compact cmd.TaskId)
        | GetSkillCommand cmd -> Some(compact cmd.SkillName)
        | CreateSkillCommand cmd -> Some(compact cmd.SkillName)
        | ListSkillsCommand _ -> None
        | GetWorktreeCommand cmd -> Some(compact cmd.WorktreePath)
        | CreateWorktreeCommand cmd -> Some(compact cmd.Name)
        | InspectWorktreeRemovalCommand cmd -> Some(compact cmd.WorktreePath)
        | RemoveWorktreeCommand cmd -> Some(compact cmd.WorktreePath)
        | ListTasksCommand _ -> None
        | GetTaskCommand cmd -> Some(compact cmd.TaskId)
        | CreateTaskCommand cmd -> Some(compact (cmd.TaskId |> Option.defaultValue "new"))
        | UpdateTaskCommand cmd -> Some(compact cmd.TaskId)
        | ClaimTaskCommand cmd -> Some(compact cmd.TaskId)
        | CheckpointTaskCommand cmd -> Some(compact cmd.TaskId)
        | CompleteTaskCommand cmd -> Some(compact cmd.TaskId)
        | GetTaskHistoryCommand cmd -> Some(compact cmd.TaskId)
        | V2TaskReadCommand cmd -> Some(compact cmd.AgentId)
        | V2TaskWriteCommand cmd -> Some(compact cmd.Action)
        | V2LedgerReadCommand cmd -> Some(compact cmd.ProjectName)
        | V2LedgerWriteCommand cmd -> Some(compact cmd.Action)
        | V2IntegrationReadCommand cmd -> Some(compact cmd.ActionId)
        | V2IntegrationWriteCommand cmd -> Some(compact cmd.Action)
        | V2ReviewReadCommand cmd -> Some(compact cmd.Action)
        | V2ReviewWriteCommand cmd -> Some(compact cmd.Action)
        | GetGitDiffCommand cmd -> cmd.Path |> Option.map compact
        | GitCommitCommand cmd -> Some(compact cmd.Message)
        | StartJobCommand cmd -> Some(fullCommand cmd.Executable cmd.Args)
        | GetJobResultCommand cmd -> Some(compact cmd.JobId)
        | CancelJobCommand cmd -> Some(compact cmd.JobId)
        | MessageCommand cmd -> Some(compact cmd.Message)
        | GetClientActivityCommand _ -> None
        | ListCommandsCommand
        | ListProjectsCommand
        | ListWorktreesCommand _
        | CompareWorktreeTasksCommand _
        | GetAgentAssignmentCommand _
        | GetOrchestrationSnapshotCommand _
        | AssignTaskCommand _
        | AcknowledgeTaskCommand _
        | BeginTaskCommand _
        | RetireWorktreeCommand _
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
        | ExportFileCommand cmd -> Some(truncate 64 cmd.FilePath)
        | ImportFileCommand cmd -> Some(truncate 64 cmd.FilePath)
        | UpdateClientCommand -> Some "self-update"
        | WriteFileCommand cmd -> Some(truncate 64 cmd.FilePath)
        | PatchFileCommand cmd -> Some(truncate 64 cmd.FilePath)
        | RunCommandCommand cmd -> Some(commandPreview cmd.Executable cmd.Args)
        | RunProjectTaskCommand cmd -> Some(truncate 48 cmd.TaskName)
        | AttachWorkTaskCommand cmd -> Some(truncate 48 cmd.TaskId)
        | GetWorkTaskCommand cmd -> Some(truncate 48 cmd.TaskId)
        | ListWorkTasksCommand _ -> None
        | CheckpointWorkTaskCommand cmd -> Some(truncate 48 cmd.TaskId)
        | CompleteWorkTaskCommand cmd -> Some(truncate 48 cmd.TaskId)
        | GetSkillCommand cmd -> Some(truncate 48 cmd.SkillName)
        | CreateSkillCommand cmd -> Some(truncate 48 cmd.SkillName)
        | ListSkillsCommand _ -> None
        | GetWorktreeCommand cmd -> Some(truncate 64 cmd.WorktreePath)
        | CreateWorktreeCommand cmd -> Some(truncate 64 cmd.Name)
        | InspectWorktreeRemovalCommand cmd -> Some(truncate 64 cmd.WorktreePath)
        | RemoveWorktreeCommand cmd -> Some(truncate 64 cmd.WorktreePath)
        | ListTasksCommand _ -> None
        | GetTaskCommand cmd -> Some(truncate 64 cmd.TaskId)
        | CreateTaskCommand cmd -> Some(truncate 64 (cmd.TaskId |> Option.defaultValue "new"))
        | UpdateTaskCommand cmd -> Some(truncate 64 cmd.TaskId)
        | ClaimTaskCommand cmd -> Some(truncate 64 cmd.TaskId)
        | CheckpointTaskCommand cmd -> Some(truncate 64 cmd.TaskId)
        | CompleteTaskCommand cmd -> Some(truncate 64 cmd.TaskId)
        | GetTaskHistoryCommand cmd -> Some(truncate 64 cmd.TaskId)
        | V2TaskReadCommand cmd -> Some(truncate 64 cmd.AgentId)
        | V2TaskWriteCommand cmd -> Some(truncate 64 cmd.Action)
        | V2LedgerReadCommand cmd -> Some(truncate 64 cmd.ProjectName)
        | V2LedgerWriteCommand cmd -> Some(truncate 64 cmd.Action)
        | V2IntegrationReadCommand cmd -> Some(truncate 64 cmd.ActionId)
        | V2IntegrationWriteCommand cmd -> Some(truncate 64 cmd.Action)
        | V2ReviewReadCommand cmd -> Some(truncate 64 cmd.Action)
        | V2ReviewWriteCommand cmd -> Some(truncate 64 cmd.Action)
        | GetGitDiffCommand cmd -> cmd.Path |> Option.map (truncate 64)
        | GitCommitCommand cmd -> Some(quoted cmd.Message)
        | StartJobCommand cmd -> Some(commandPreview cmd.Executable cmd.Args)
        | GetJobResultCommand cmd -> Some(shortId cmd.JobId)
        | CancelJobCommand cmd -> Some(shortId cmd.JobId)
        | MessageCommand cmd -> Some(truncate 64 cmd.Message)
        | GetClientActivityCommand _ -> None
        | ListCommandsCommand
        | ListProjectsCommand
        | ListWorktreesCommand _
        | CompareWorktreeTasksCommand _
        | GetAgentAssignmentCommand _
        | GetOrchestrationSnapshotCommand _
        | AssignTaskCommand _
        | AcknowledgeTaskCommand _
        | BeginTaskCommand _
        | RetireWorktreeCommand _
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
    let version = "3.37"
    let defaultPatchFuzzyContextLines = 3
    let maxResponseBytes = 900 * 1024
    let maxImageBytes = 8 * 1024 * 1024
    let maxExportBytes = 8 * 1024 * 1024
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
          definition "ListWorktrees" "Lists Git worktrees registered for a project." [ ReadOnly ] false false false
          definition "GetWorktree" "Inspects a registered Git worktree." [ ReadOnly ] false false false
          definition "CreateWorktree" "Creates an isolated Git worktree and branch." [ VersionControlWrite ] true true false
          definition "InspectWorktreeRemoval" "Checks registered worktree removal safety." [ ReadOnly ] false false false
          definition "V2TaskRead" "Reads a registered agent inbox from a single V2 ledger snapshot." [ ReadOnly; NetworkAccess ] false false false
          definition "V2TaskWrite" "Creates/assigns/starts V2 tasks and binds immutable review base." [ VersionControlWrite; NetworkAccess ] true true false
          definition "V2LedgerRead" "Inspect the verified canonical V2 Git ledger identity and trust head." [ ReadOnly; NetworkAccess ] false false false
          definition "V2LedgerWrite" "Explicitly bind, initialize or rebind the canonical V2 ledger with privacy acknowledgment." [ VersionControlWrite; NetworkAccess ] true true false
          definition "V2IntegrationRead" "Inspect planned source integration and read-only remote status." [ ReadOnly; NetworkAccess ] false false false
          definition "V2IntegrationWrite" "Plan or record verified source integration without implicit source push." [ VersionControlWrite; NetworkAccess ] true true false
          definition "V2ReviewRead" "Reads opt-in canonical V2 review ledger requests and reviewer inboxes." [ ReadOnly; NetworkAccess ] false false false
          definition "V2ReviewWrite" "Updates opt-in canonical V2 review ledger with a stable operation UUID." [ VersionControlWrite; NetworkAccess ] true true false
          definition "RemoveWorktree" "Removes a clean integrated Git worktree." [ VersionControlWrite; Destructive ] true true false
          definition "GetProjectDetails" "Reads project summary details and special files." [ ReadOnly ] false false false
          definition "ListDirectory" "Lists files and folders in a project directory." [ ReadOnly ] false false false
          definition "SearchFiles" "Searches project file names." [ ReadOnly ] false false false
          definition "SearchText" "Searches project file contents." [ ReadOnly ] false false false
          definition "ReadFile" "Reads one file." [ ReadOnly ] false false false
          definition "ReadFiles" "Reads multiple files." [ ReadOnly ] false false false
          definition "ReadImage" "Reads one PNG, JPEG, or WebP image for model vision." [ ReadOnly ] false false false
          definition "ExportFile" "Copies one bounded project file to a short-lived user download URL." [ ReadOnly ] false false false
          definition "ImportFile" "Downloads a ChatGPT-provided file into a project path." [ WorkspaceWrite; NetworkAccess ] true true false
          definition "UpdateClient" "Downloads, verifies, installs, and restarts into the latest published Jarvis client." [ ProcessExecution; NetworkAccess; Destructive ] true false false
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
              elif definition.Name = "ExportFile" then Some maxExportBytes
              else Some maxResponseBytes
          InputSchemaJson = None
          OutputSchemaJson = None }

    let capabilities = commandDefinitions |> List.map toCapability

    let tryFindDefinition name =
        commandDefinitions |> List.tryFind (fun definition -> definition.Name = name)


    let listCommandsResult =
        { ProtocolVersion = version
          Commands = capabilities }
