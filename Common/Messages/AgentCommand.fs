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

[<JsonConverter(typeof<TypeSafeEnumConverter<JobKind>>)>]
type JobKind =
    | Process
    | ToolOperation

[<JsonConverter(typeof<UnionConverter<JobStatus>>)>]
type JobStatus =
    | Running
    | Completed of exitCode: int
    | FailedToStart of message: string
    | Canceled
    | ToolCompleted
    | ToolFailed of message: string

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

type BrowserListTabsCommand = struct end
type BrowserListWindowsCommand = struct end
type ScheduleBrowserExecutionCommand =
    { ScheduleId: string
      RunAfterSeconds: int
      Url: string
      ProjectName: string
      ScriptPath: string
      ArgsJson: string option
      Alias: string option
      Active: bool option
      WindowRegistration: string option
      CloseTabAfterExecution: bool option
      RepeatEverySeconds: int option }
type ListScheduledBrowserExecutionsCommand = struct end
type CancelScheduledBrowserExecutionCommand = { ScheduleId: string }

type BrowserOpenTabCommand =
    { WindowRegistration: string
      Url: string
      Alias: string option
      Active: bool option }

type BrowserCloseTabCommand = { TabRegistration: string }
type BrowserRunInNewTabCommand =
    { WindowRegistration: string
      Url: string
      ProjectName: string
      ScriptPath: string
      ArgsJson: string option
      Active: bool option
      CloseTabAfterExecution: bool option
      TimeoutMs: int option }

type BrowserExecuteFileCommand =
    { ProjectName: string
      ScriptPath: string
      TabRegistration: string
      ArgsJson: string option
      TimeoutMs: int option }
type BrowserExecuteJsCommand =
    { TabRegistration: string
      Code: string
      ArgsJson: string option
      TimeoutMs: int option }
type BrowserCaptureDomCommand =
    { TabRegistration: string
      WaitJson: string option
      CaptureSelector: string option
      TimeoutMs: int option }
type BrowserExportDomCommand =
    { TabRegistration: string
      ProjectName: string
      FilePath: string
      WaitJson: string option
      CaptureSelector: string option
      TimeoutMs: int option }
type CreateProjectCommand =
    { Name: string
      InitializeGit: bool
      CreateJarvisConfig: bool
      CreateAgentInstructions: bool
      InitializeLedger: bool
      RemoteUrl: string
      AcknowledgeRemoteVisibility: bool }


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


type TaskReadCommand =
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

/// Distinct actor and subject: observation never authorizes activity for the target.
type ObserveAgentRequest =
    { ProjectName: string
      ObserverAgentId: string
      TargetAgentId: string }

type TaskWriteCommand =
    { ProjectName: string
      Action: string
      OperationId: string
      TaskId: string
      AttemptId: string
      ActorId: string
      OwnerId: string
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
      Evidence: string
      SessionId: string }

/// Challenge condition owners and descriptions are explicit typed wire fields.
type ChallengeConditionDefinition =
    { OwnerKind: string
      OwnerAgentId: string
      SuggestedAgentId: string
      Description: string }

type ChallengeReadCommand =
    { ProjectName: string
      ActorId: string
      Action: string
      ChallengeId: string
      Limit: int
      AfterChallengeId: string
      AfterEventSequence: int64
      ExpectedSnapshotCommit: string }

type ChallengeWriteCommand =
    { ProjectName: string
      ActorId: string
      OperationId: string
      Action: string
      ChallengeId: string
      ConditionId: string
      ExpectedVersion: int
      Title: string
      TaskId: string
      AttemptId: string
      DependencyChallengeIds: string list
      ExpectedTaskVersion: int
      ExpectedAttemptVersion: int
      ExpectedAgentVersion: int
      AssignmentGeneration: int
      Conditions: ChallengeConditionDefinition list
      Evidence: string
      Reason: string
      Outcome: string
      NewOwnerAgentId: string
      Subscribe: bool }

type LedgerReadCommand =
    { ProjectName: string
      Action: string
      OperationId: string
      Cursor: string
      PageSize: int }

type LedgerWriteCommand =
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

type ReviewReadCommand =
    { ProjectName: string
      Action: string
      TaskId: string
      RequestId: string
      ReviewerId: string
      DocumentPath: string }

type ReviewWriteCommand =
    { ProjectName: string
      Action: string
      OperationId: string
      TaskId: string
      RequestId: string
      ActorId: string
      ReviewerId: string
      ExpectedVersion: int
      Kind: string
      SourceRef: string
      HeadOid: string
      TargetRef: string
      Summary: string
      Verdict: string
      DocumentPath: string
      Content: string
      ExpectedBlobOid: string }

// Explicit operator-only recovery. Neither command is a routine agent write
// and neither creates a new operation UUID.
type RetryPendingIntentCommand =
    { ProjectName: string
      OperationId: string
      OperatorId: string
      ExpectedCommandSha256: string
      ExpectedVerifiedHead: string }

type SupersedePendingIntentCommand =
    { ProjectName: string
      OperationId: string
      OperatorId: string
      ExpectedCommandSha256: string
      ExpectedVerifiedHead: string
      RelatedOperationId: string
      RelatedRequestHash: string
      Reason: string }

type AbandonPendingIntentCommand =
    { ProjectName: string
      OperationId: string
      OperatorId: string
      ExpectedCommandSha256: string
      ExpectedVerifiedHead: string
      Reason: string }

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

/// Explicit search completion state; results are never silently truncated.
[<RequireQualifiedAccess>]
[<JsonConverter(typeof<UnionConverter<SearchStopReason>>)>]
type SearchStopReason =
    | Finished
    | ResultLimit
    | FileVisitLimit
    | DirectoryVisitLimit
    | EntryVisitLimit
    | TimeLimit
    | OutputByteLimit
    | TextScanLimit

type SearchResult<'T> =
    { Matches: 'T list
      StopReason: SearchStopReason
      FilesVisited: int
      DirectoriesVisited: int }

type ReadFileCommand =
    { ProjectName: string
      FilePath: string
      StartLine: int option
      EndLine: int option
      IncludeLineNumbers: bool option }

/// Bounded UTF-8 content page with a file-version-aware continuation.
type ReadFilePageCommand =
    { ProjectName: string
      FilePath: string
      Cursor: string option
      PageBytes: int option }

type ReadFilePageResult =
    { FilePath: string
      Content: string
      StartByte: int64
      NextByte: int64
      TotalBytes: int64
      FirstLine: int64
      NextLine: int64
      NextCursor: string option }

type ReadFilesCommand =
    { ProjectName: string
      FilePaths: string list }

/// Explicit, uncached hierarchy retrieval. The cursor continues large responses
/// without imposing a file-length or hierarchy-depth limit.
type GetAgentInstructionsCommand =
    { ProjectName: string
      AgentId: string
      Directory: string
      Cursor: string option }

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

type ListConfiguredTasksCommand =
    { ProjectName: string }

type ListConfiguredTasksResult =
    { Tasks: ProjectTaskDefinition list }

type RunProjectTaskCommand =
    { ProjectName: string
      TaskName: string }

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
      /// Process and in-process tool operation jobs are separate kinds.
      Kind: JobKind
      CommandName: string option
      OperationId: string option
      AgentId: string option
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
    { ProjectName: string
      JobId: string
      AfterSequence: int64 option }

/// Read-only, device-local forensic evidence for a single native StartJob.
/// An ordinary job-result lookup cannot recover process identity on restart.
type GetProcessOwnershipEvidenceCommand =
    { ProjectName: string
      JobId: string }

/// Never treat a launch observation or a parent PID as OS reaping proof.
/// Stage is IntentOnly or Launched; ProcessTreeStatus is always Unverified.
type GetProcessOwnershipEvidenceResult =
    { JobId: string
      ProjectId: string
      DeviceId: string
      ClientIncarnationId: string
      IntentCreatedUtc: string
      Stage: string
      ProcessId: int option
      ProcessStartUtc: string option
      LinuxBootId: string option
      LinuxStartTicks: string option
      IntentSha256: string option
      LaunchRecordedUtc: string option
      ProcessTreeStatus: string }

type CancelJobCommand = { ProjectName: string; JobId: string }

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

type LedgerFailureInfo =
    { Code: string
      Message: string
      OperationId: string option
      VerifiedLedgerHead: string option }

/// A command may have committed before its response was lost. OperationId
/// refers to the ORIGINAL caller operation; never generate a replacement ID.
type UncertainOperationInfo =
    { Message: string
      OperationId: string option }

[<JsonConverter(typeof<UnionConverter<AgentError>>)>]
type AgentError =
    | NotFound of string
    | PermissionDenied of string
    | ConfirmationRequired of ConfirmationRequest
    | ValidationFailed of string
    | Conflict of string
    | ExecutionFailed of string
    | UncertainOutcome of UncertainOperationInfo
    | OutputTruncated of string
    | LedgerFailure of LedgerFailureInfo

type InstructionDocument =
    { Scope: string
      Path: string
      Sha256: string
      Content: string }

/// A canonical ledger mutation changed the assignment or Working-turn state
/// of this agent. Advisory to other clients; never an authorization claim.
type AgentStateChange =
    { ProjectName: string
      AgentId: string }

/// Caller-visible outcome of a self-AgentInbox's automatic activation.
/// CommittedHead/OperationId are present only after a confirmed ledger write.
/// A non-activating read is explicitly reported without inventing a transition.
type AgentInboxActivation =
    { ProjectName: string
      AgentId: string
      ActivatedByThisCall: bool
      BeforeState: string option
      AfterState: string option
      CommittedHead: string option
      OperationId: string option }

/// A pending tool call is still executing in this exact client process.
/// An agent must poll by ID; neither state changes nor instructions are final yet.
type ToolJobHandle =
    { JobId: string
      Kind: JobKind
      Status: string
      ProjectName: string option
      /// Destination routing provenance is attached by the server, never inferred from a default device.
      DeviceId: string option
      AgentId: string
      OperationId: string option
      CommandName: string
      StartedAt: DateTimeOffset }

type AgentCommandResponse =
    { Result: string option
      Error: AgentError option
      Instructions: InstructionDocument list
      AgentStateChanges: AgentStateChange list
      InboxActivation: AgentInboxActivation option
      PendingJob: ToolJobHandle option }

/// Internal server-to-client envelope for verified source-side mutation
/// evidence of a cross-project notification. The destination client owns the
/// full combined terminal response and its resumable job; no server task store.
/// This command is not exposed through public MCP actions.
type CrossProjectNotificationCommand =
    { Notification: TaskWriteCommand
      SourceStateChanges: AgentStateChange list
      SourceInboxActivation: AgentInboxActivation option }

/// Process output and complete, immutable tool response share a lookup API.
/// ToolResponse contains the ORIGINAL response, not reconstructed polling metadata.
type JobResult =
    { JobId: string
      Status: JobStatus
      Events: JobOutputEvent list
      NextSequence: int64
      Truncated: bool
      ToolResponse: AgentCommandResponse option
      OperationId: string option }

[<JsonConverter(typeof<UnionConverter<AgentCommand>>)>]
type AgentCommand =
    | ListCommandsCommand
    | ListProjectsCommand
    | BrowserListTabsCommand
    | BrowserListWindowsCommand
    | BrowserCloseTabCommand of BrowserCloseTabCommand
    | BrowserRunInNewTabCommand of BrowserRunInNewTabCommand
    | ScheduleBrowserExecutionCommand of ScheduleBrowserExecutionCommand
    | ListScheduledBrowserExecutionsCommand
    | CancelScheduledBrowserExecutionCommand of CancelScheduledBrowserExecutionCommand
    | BrowserOpenTabCommand of BrowserOpenTabCommand
    | BrowserExecuteFileCommand of BrowserExecuteFileCommand
    | BrowserExecuteJsCommand of BrowserExecuteJsCommand
    | BrowserCaptureDomCommand of BrowserCaptureDomCommand
    | BrowserExportDomCommand of BrowserExportDomCommand
    | CreateProjectCommand of CreateProjectCommand
    | ListWorktreesCommand of ListWorktreesCommand
    | GetWorktreeCommand of GetWorktreeCommand
    | CreateWorktreeCommand of CreateWorktreeCommand
    | InspectWorktreeRemovalCommand of InspectWorktreeRemovalCommand
    | RemoveWorktreeCommand of RemoveWorktreeCommand
    | TaskReadCommand of TaskReadCommand
    | ObserveAgentCommand of ObserveAgentRequest
    | TaskWriteCommand of TaskWriteCommand
    | CrossProjectNotificationCommand of CrossProjectNotificationCommand
    | ChallengeReadCommand of ChallengeReadCommand
    | ChallengeWriteCommand of ChallengeWriteCommand
    | LedgerReadCommand of LedgerReadCommand
    | LedgerWriteCommand of LedgerWriteCommand
    | ReviewReadCommand of ReviewReadCommand
    | ReviewWriteCommand of ReviewWriteCommand
    | RetryPendingIntentCommand of RetryPendingIntentCommand
    | SupersedePendingIntentCommand of SupersedePendingIntentCommand
    | AbandonPendingIntentCommand of AbandonPendingIntentCommand
    | GetProjectDetailsCommand of GetProjectDetailsCommand
    | ListDirectoryCommand of ListDirectoryCommand
    | SearchFilesCommand of SearchFilesCommand
    | SearchTextCommand of SearchTextCommand
    | ReadFileCommand of ReadFileCommand
    | ReadFilePageCommand of ReadFilePageCommand
    | ReadFilesCommand of ReadFilesCommand
    | GetAgentInstructionsCommand of GetAgentInstructionsCommand
    | ReadImageCommand of ReadImageCommand
    | ExportFileCommand of ExportFileCommand
    | ImportFileCommand of ImportFileCommand
    | UpdateClientCommand
    | WriteFileCommand of WriteFileCommand
    | PatchFileCommand of PatchFileCommand
    | RunCommandCommand of RunCommandCommand
    | ListConfiguredTasksCommand of ListConfiguredTasksCommand
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
    | GetProcessOwnershipEvidenceCommand of GetProcessOwnershipEvidenceCommand
    | CancelJobCommand of CancelJobCommand
    | MessageCommand of MessageCommand
    | GetClientActivityCommand of GetClientActivityCommand

/// An empty list is definitive only for a confirmed response. After a
/// transport/dispatch exception, the list can at most contain known changes.
module AgentStateEvidence =
    let isUnknown (response: AgentCommandResponse) =
        match response.Error with
        | Some(UncertainOutcome _) -> true
        | _ -> false

module AgentCommandInfo =
    /// A forwarded notification has already validated and possibly activated
    /// its source agent. These are confirmed facts even when the destination
    /// write later has an uncertain outcome.
    let confirmedSourceEvidence = function
        | CrossProjectNotificationCommand envelope ->
            (if isNull (box envelope.SourceStateChanges) then [] else envelope.SourceStateChanges),
            (envelope.SourceInboxActivation
             |> Option.filter (fun receipt -> receipt.ActivatedByThisCall))
        | _ -> [], None

    let recoveryOperationId command =
        let raw =
            match command with
            | TaskWriteCommand cmd -> cmd.OperationId
            | CrossProjectNotificationCommand envelope -> envelope.Notification.OperationId
            | ChallengeWriteCommand cmd -> cmd.OperationId
            | LedgerWriteCommand cmd -> cmd.OperationId
            | ReviewWriteCommand cmd -> cmd.OperationId
            | RetryPendingIntentCommand cmd -> cmd.OperationId
            | SupersedePendingIntentCommand cmd -> cmd.OperationId
            | AbandonPendingIntentCommand cmd -> cmd.OperationId
            | _ -> ""
        match Guid.TryParse raw with
        | true, id -> Some(id.ToString("D"))
        | _ -> None

    let name = function
        | ListCommandsCommand -> "ListCommandsCommand"
        | ListProjectsCommand -> "ListProjectsCommand"
        | BrowserListTabsCommand -> "BrowserListTabsCommand"
        | BrowserListWindowsCommand -> "BrowserListWindowsCommand"
        | BrowserCloseTabCommand _ -> "BrowserCloseTabCommand"
        | BrowserRunInNewTabCommand _ -> "BrowserRunInNewTabCommand"
        | ScheduleBrowserExecutionCommand _ -> "ScheduleBrowserExecutionCommand"
        | ListScheduledBrowserExecutionsCommand -> "ListScheduledBrowserExecutionsCommand"
        | CancelScheduledBrowserExecutionCommand _ -> "CancelScheduledBrowserExecutionCommand"
        | BrowserOpenTabCommand _ -> "BrowserOpenTabCommand"
        | BrowserExecuteFileCommand _ -> "BrowserExecuteFileCommand"
        | BrowserExecuteJsCommand _ -> "BrowserExecuteJsCommand"
        | BrowserCaptureDomCommand _ -> "BrowserCaptureDomCommand"
        | BrowserExportDomCommand _ -> "BrowserExportDomCommand"
        | CreateProjectCommand _ -> "CreateProjectCommand"
        | ListWorktreesCommand _ -> "ListWorktreesCommand"
        | GetWorktreeCommand _ -> "GetWorktreeCommand"
        | CreateWorktreeCommand _ -> "CreateWorktreeCommand"
        | InspectWorktreeRemovalCommand _ -> "InspectWorktreeRemovalCommand"
        | RemoveWorktreeCommand _ -> "RemoveWorktreeCommand"
        | TaskReadCommand _ -> "TaskReadCommand"
        | ObserveAgentCommand _ -> "ObserveAgentCommand"
        | TaskWriteCommand _
        | CrossProjectNotificationCommand _ -> "TaskWriteCommand"
        | ChallengeReadCommand _ -> "ChallengeReadCommand"
        | ChallengeWriteCommand _ -> "ChallengeWriteCommand"
        | LedgerReadCommand _ -> "LedgerReadCommand"
        | LedgerWriteCommand _ -> "LedgerWriteCommand"
        | ReviewReadCommand _ -> "ReviewReadCommand"
        | ReviewWriteCommand _ -> "ReviewWriteCommand"
        | RetryPendingIntentCommand _ -> "RetryPendingIntentCommand"
        | SupersedePendingIntentCommand _ -> "SupersedePendingIntentCommand"
        | AbandonPendingIntentCommand _ -> "AbandonPendingIntentCommand"
        | GetProjectDetailsCommand _ -> "GetProjectDetailsCommand"
        | ListDirectoryCommand _ -> "ListDirectoryCommand"
        | SearchFilesCommand _ -> "SearchFilesCommand"
        | SearchTextCommand _ -> "SearchTextCommand"
        | ReadFileCommand _ -> "ReadFileCommand"
        | ReadFilePageCommand _ -> "ReadFilePageCommand"
        | ReadFilesCommand _ -> "ReadFilesCommand"
        | GetAgentInstructionsCommand _ -> "GetAgentInstructionsCommand"
        | ReadImageCommand _ -> "ReadImageCommand"
        | ExportFileCommand _ -> "ExportFileCommand"
        | ImportFileCommand _ -> "ImportFileCommand"
        | UpdateClientCommand -> "UpdateClientCommand"
        | WriteFileCommand _ -> "WriteFileCommand"
        | PatchFileCommand _ -> "PatchFileCommand"
        | RunCommandCommand _ -> "RunCommandCommand"
        | ListConfiguredTasksCommand _ -> "ListConfiguredTasksCommand"
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
        | GetProcessOwnershipEvidenceCommand _ -> "GetProcessOwnershipEvidenceCommand"
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
        | BrowserListTabsCommand -> "BrowserListTabs"
        | BrowserListWindowsCommand -> "BrowserListWindows"
        | BrowserCloseTabCommand _ -> "BrowserCloseTab"
        | BrowserRunInNewTabCommand _ -> "BrowserRunInNewTab"
        | ScheduleBrowserExecutionCommand _ -> "ScheduleBrowserExecution"
        | ListScheduledBrowserExecutionsCommand -> "ListScheduledBrowserExecutions"
        | CancelScheduledBrowserExecutionCommand _ -> "CancelScheduledBrowserExecution"
        | BrowserOpenTabCommand _ -> "BrowserOpenTab"
        | BrowserExecuteFileCommand _ -> "BrowserExecuteFile"
        | BrowserExecuteJsCommand _ -> "BrowserExecuteJs"
        | BrowserCaptureDomCommand _ -> "BrowserCaptureDom"
        | BrowserExportDomCommand _ -> "BrowserExportDom"
        | CreateProjectCommand _ -> "CreateProject"
        | ListWorktreesCommand _ -> "ListWorktrees"
        | GetWorktreeCommand _ -> "GetWorktree"
        | CreateWorktreeCommand _ -> "CreateWorktree"
        | InspectWorktreeRemovalCommand _ -> "InspectWorktreeRemoval"
        | RemoveWorktreeCommand _ -> "RemoveWorktree"
        | TaskReadCommand _ -> "TaskRead"
        | ObserveAgentCommand _ -> "ObserveAgent"
        | TaskWriteCommand _
        | CrossProjectNotificationCommand _ -> "TaskWrite"
        | ChallengeReadCommand _ -> "ChallengeRead"
        | ChallengeWriteCommand _ -> "ChallengeWrite"
        | LedgerReadCommand _ -> "LedgerRead"
        | LedgerWriteCommand _ -> "LedgerWrite"
        | ReviewReadCommand _ -> "ReviewRead"
        | ReviewWriteCommand _ -> "ReviewWrite"
        | RetryPendingIntentCommand _ -> "RetryPendingIntent"
        | SupersedePendingIntentCommand _ -> "SupersedePendingIntent"
        | AbandonPendingIntentCommand _ -> "AbandonPendingIntent"
        | GetProjectDetailsCommand _ -> "GetProjectDetails"
        | ListDirectoryCommand _ -> "ListDirectory"
        | SearchFilesCommand _ -> "SearchFiles"
        | SearchTextCommand _ -> "SearchText"
        | ReadFileCommand _ -> "ReadFile"
        | ReadFilePageCommand _ -> "ReadFilePage"
        | ReadFilesCommand _ -> "ReadFiles"
        | GetAgentInstructionsCommand _ -> "GetAgentInstructions"
        | ReadImageCommand _ -> "ReadImage"
        | ExportFileCommand _ -> "ExportFile"
        | ImportFileCommand _ -> "ImportFile"
        | UpdateClientCommand -> "UpdateClient"
        | WriteFileCommand _ -> "WriteFile"
        | PatchFileCommand _ -> "PatchFile"
        | RunCommandCommand _ -> "RunCommand"
        | ListConfiguredTasksCommand _ -> "ListConfiguredTasks"
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
        | GetProcessOwnershipEvidenceCommand _ -> "GetProcessOwnershipEvidence"
        | CancelJobCommand _ -> "CancelJob"
        | MessageCommand _ -> "Message"
        | GetClientActivityCommand _ -> "GetClientActivity"

    /// Stable identity of the represented ledger write/recovery operation.
    /// Do not infer it by scanning arbitrary JSON fields in other commands.
    let operationId = function
        | TaskWriteCommand cmd -> Some cmd.OperationId
        | CrossProjectNotificationCommand envelope -> Some envelope.Notification.OperationId
        | ChallengeWriteCommand cmd -> Some cmd.OperationId
        | ReviewWriteCommand cmd -> Some cmd.OperationId
        | LedgerWriteCommand cmd when cmd.Action <> "AcknowledgePendingOperation" -> Some cmd.OperationId
        | RetryPendingIntentCommand cmd -> Some cmd.OperationId
        | SupersedePendingIntentCommand cmd -> Some cmd.OperationId
        | AbandonPendingIntentCommand cmd -> Some cmd.OperationId
        | _ -> None

    let projectName = function
        | ListCommandsCommand
        | ListProjectsCommand
        | BrowserListTabsCommand
        | BrowserListWindowsCommand
        | BrowserCloseTabCommand _
        | ListScheduledBrowserExecutionsCommand
        | CancelScheduledBrowserExecutionCommand _
        | BrowserOpenTabCommand _
        | BrowserExecuteJsCommand _
        | BrowserCaptureDomCommand _
        | CreateProjectCommand _
        | UpdateClientCommand -> None
        | GetJobResultCommand cmd -> Some cmd.ProjectName
        | GetProcessOwnershipEvidenceCommand cmd -> Some cmd.ProjectName
        | CancelJobCommand cmd -> Some cmd.ProjectName
        | MessageCommand cmd -> cmd.ProjectName
        | GetClientActivityCommand cmd -> cmd.ProjectName
        | ScheduleBrowserExecutionCommand cmd -> Some cmd.ProjectName
        | BrowserRunInNewTabCommand cmd -> Some cmd.ProjectName
        | BrowserExecuteFileCommand cmd -> Some cmd.ProjectName
        | BrowserExportDomCommand cmd -> Some cmd.ProjectName
        | ListWorktreesCommand cmd -> Some cmd.ProjectName
        | GetWorktreeCommand cmd -> Some cmd.ProjectName
        | CreateWorktreeCommand cmd -> Some cmd.ProjectName
        | InspectWorktreeRemovalCommand cmd -> Some cmd.ProjectName
        | RemoveWorktreeCommand cmd -> Some cmd.ProjectName
        | TaskReadCommand cmd -> Some cmd.ProjectName
        | ObserveAgentCommand cmd -> Some cmd.ProjectName
        | TaskWriteCommand cmd -> Some cmd.ProjectName
        | CrossProjectNotificationCommand envelope -> Some envelope.Notification.ProjectName
        | ChallengeReadCommand cmd -> Some cmd.ProjectName
        | ChallengeWriteCommand cmd -> Some cmd.ProjectName
        | LedgerReadCommand cmd -> Some cmd.ProjectName
        | LedgerWriteCommand cmd -> Some cmd.ProjectName
        | ReviewReadCommand cmd -> Some cmd.ProjectName
        | ReviewWriteCommand cmd -> Some cmd.ProjectName
        | RetryPendingIntentCommand cmd -> Some cmd.ProjectName
        | SupersedePendingIntentCommand cmd -> Some cmd.ProjectName
        | AbandonPendingIntentCommand cmd -> Some cmd.ProjectName
        | GetProjectDetailsCommand cmd -> Some cmd.ProjectName
        | ListDirectoryCommand cmd -> Some cmd.ProjectName
        | SearchFilesCommand cmd -> Some cmd.ProjectName
        | SearchTextCommand cmd -> Some cmd.ProjectName
        | ReadFileCommand cmd -> Some cmd.ProjectName
        | ReadFilePageCommand cmd -> Some cmd.ProjectName
        | ReadFilesCommand cmd -> Some cmd.ProjectName
        | GetAgentInstructionsCommand cmd -> Some cmd.ProjectName
        | ReadImageCommand cmd -> Some cmd.ProjectName
        | ExportFileCommand cmd -> Some cmd.ProjectName
        | ImportFileCommand cmd -> Some cmd.ProjectName
        | WriteFileCommand cmd -> Some cmd.ProjectName
        | PatchFileCommand cmd -> Some cmd.ProjectName
        | RunCommandCommand cmd -> Some cmd.ProjectName
        | ListConfiguredTasksCommand cmd -> Some cmd.ProjectName
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
        | ScheduleBrowserExecutionCommand cmd -> Some($"schedule {cmd.ScheduleId}: {cmd.ProjectName}/{cmd.ScriptPath} in {cmd.RunAfterSeconds}s")
        | ListScheduledBrowserExecutionsCommand -> None
        | CancelScheduledBrowserExecutionCommand cmd -> Some cmd.ScheduleId
        | BrowserListTabsCommand -> None
        | BrowserListWindowsCommand -> None
        | BrowserCloseTabCommand cmd -> Some("agent-created tab " + cmd.TabRegistration)
        | BrowserRunInNewTabCommand cmd -> Some(cmd.ProjectName + "/" + cmd.ScriptPath + " -> " + cmd.WindowRegistration)
        | BrowserOpenTabCommand cmd ->
            match Uri.TryCreate(cmd.Url, UriKind.Absolute) with
            | true, uri when uri.Scheme = "https" || uri.Scheme = "http" -> Some(uri.GetLeftPart(UriPartial.Authority) + " (new tab)")
            | _ -> Some "(new browser tab)"
        | BrowserExecuteFileCommand cmd -> Some(cmd.ScriptPath + " -> " + cmd.TabRegistration)
        | BrowserExecuteJsCommand cmd -> Some("registered tab " + cmd.TabRegistration)
        | BrowserCaptureDomCommand cmd -> Some("DOM snapshot from " + cmd.TabRegistration)
        | BrowserExportDomCommand cmd -> Some("DOM snapshot to " + cmd.ProjectName + "/" + cmd.FilePath)
        | ListDirectoryCommand cmd -> Some(compact cmd.FolderPath)
        | SearchFilesCommand cmd -> Some(sprintf "\"%s\"" (compact cmd.Query))
        | SearchTextCommand cmd -> Some(sprintf "\"%s\"" (compact cmd.Query))
        | ReadFileCommand cmd -> Some(compact cmd.FilePath)
        | ReadFilePageCommand cmd -> Some(compact cmd.FilePath)
        | ReadFilesCommand cmd -> Some(String.concat ", " (cmd.FilePaths |> List.map compact))
        | GetAgentInstructionsCommand cmd -> Some(compact cmd.Directory)
        | ReadImageCommand cmd -> Some(compact cmd.FilePath)
        | ExportFileCommand cmd -> Some(compact cmd.FilePath)
        | ImportFileCommand cmd -> Some(compact cmd.FilePath)
        | UpdateClientCommand -> Some "latest published client"
        | WriteFileCommand cmd -> Some(compact cmd.FilePath)
        | PatchFileCommand cmd -> Some(compact cmd.FilePath)
        | RunCommandCommand cmd -> Some(fullCommand cmd.Executable cmd.Args)
        | RunProjectTaskCommand cmd -> Some(compact cmd.TaskName)
        | GetSkillCommand cmd -> Some(compact cmd.SkillName)
        | CreateSkillCommand cmd -> Some(compact cmd.SkillName)
        | ListSkillsCommand _ -> None
        | GetWorktreeCommand cmd -> Some(compact cmd.WorktreePath)
        | CreateProjectCommand cmd -> Some(compact cmd.Name)
        | CreateWorktreeCommand cmd -> Some(compact cmd.Name)
        | InspectWorktreeRemovalCommand cmd -> Some(compact cmd.WorktreePath)
        | RemoveWorktreeCommand cmd -> Some(compact cmd.WorktreePath)
        | TaskReadCommand cmd -> Some(compact cmd.AgentId)
        | ObserveAgentCommand cmd -> Some(compact cmd.TargetAgentId)
        | TaskWriteCommand cmd
        | CrossProjectNotificationCommand { Notification = cmd } -> Some(compact cmd.Action)
        | ChallengeReadCommand cmd -> Some(compact cmd.Action)
        | ChallengeWriteCommand cmd -> Some(compact cmd.Action)
        | LedgerReadCommand cmd -> Some(compact cmd.ProjectName)
        | LedgerWriteCommand cmd -> Some(compact cmd.Action)
        | ReviewReadCommand cmd -> Some(compact cmd.Action)
        | ReviewWriteCommand cmd -> Some(compact cmd.Action)
        | RetryPendingIntentCommand cmd -> Some(compact cmd.OperationId)
        | SupersedePendingIntentCommand cmd -> Some(compact cmd.OperationId)
        | AbandonPendingIntentCommand cmd -> Some(compact cmd.OperationId)
        | GetGitDiffCommand cmd -> cmd.Path |> Option.map compact
        | GitCommitCommand cmd -> Some(compact cmd.Message)
        | StartJobCommand cmd -> Some(fullCommand cmd.Executable cmd.Args)
        | GetJobResultCommand cmd -> Some(compact cmd.JobId)
        | GetProcessOwnershipEvidenceCommand cmd -> Some(compact cmd.JobId)
        | CancelJobCommand cmd -> Some(compact cmd.JobId)
        | MessageCommand cmd -> Some(compact cmd.Message)
        | GetClientActivityCommand _ -> None
        | ListCommandsCommand
        | ListProjectsCommand
        | ListWorktreesCommand _
        | GetProjectDetailsCommand _
        | ListConfiguredTasksCommand _
        | GetGitStatusCommand _
        | ListJobsCommand _ -> None

    let detail = function
        | ScheduleBrowserExecutionCommand cmd -> Some($"schedule {cmd.ScheduleId} in {cmd.RunAfterSeconds}s")
        | ListScheduledBrowserExecutionsCommand -> None
        | CancelScheduledBrowserExecutionCommand cmd -> Some cmd.ScheduleId
        | BrowserListTabsCommand
        | BrowserListWindowsCommand -> None
        | BrowserCloseTabCommand cmd -> Some("agent-created tab " + cmd.TabRegistration)
        | BrowserRunInNewTabCommand cmd -> Some(cmd.ScriptPath + " -> window " + cmd.WindowRegistration)
        | BrowserOpenTabCommand cmd ->
            match Uri.TryCreate(cmd.Url, UriKind.Absolute) with
            | true, uri when uri.Scheme = "https" || uri.Scheme = "http" -> Some(uri.GetLeftPart(UriPartial.Authority) + " (new tab)")
            | _ -> Some "(new browser tab)"
        | BrowserExecuteFileCommand cmd -> Some(cmd.ScriptPath + " -> " + cmd.TabRegistration)
        | BrowserExecuteJsCommand cmd -> Some("registered tab " + cmd.TabRegistration)
        | BrowserCaptureDomCommand cmd -> Some("DOM snapshot from " + cmd.TabRegistration)
        | BrowserExportDomCommand cmd -> Some("DOM snapshot to " + cmd.ProjectName + "/" + cmd.FilePath)
        | ListDirectoryCommand cmd -> Some(truncate 64 cmd.FolderPath)
        | SearchFilesCommand cmd -> Some(quoted cmd.Query)
        | SearchTextCommand cmd -> Some(quoted cmd.Query)
        | ReadFileCommand cmd -> Some(truncate 64 cmd.FilePath)
        | ReadFilePageCommand cmd -> Some(truncate 64 cmd.FilePath)
        | ReadFilesCommand cmd -> Some $"{cmd.FilePaths.Length} files"
        | GetAgentInstructionsCommand cmd -> Some(truncate 64 cmd.Directory)
        | ReadImageCommand cmd -> Some(truncate 64 cmd.FilePath)
        | ExportFileCommand cmd -> Some(truncate 64 cmd.FilePath)
        | ImportFileCommand cmd -> Some(truncate 64 cmd.FilePath)
        | UpdateClientCommand -> Some "self-update"
        | WriteFileCommand cmd -> Some(truncate 64 cmd.FilePath)
        | PatchFileCommand cmd -> Some(truncate 64 cmd.FilePath)
        | RunCommandCommand cmd -> Some(commandPreview cmd.Executable cmd.Args)
        | RunProjectTaskCommand cmd -> Some(truncate 48 cmd.TaskName)
        | GetSkillCommand cmd -> Some(truncate 48 cmd.SkillName)
        | CreateSkillCommand cmd -> Some(truncate 48 cmd.SkillName)
        | ListSkillsCommand _ -> None
        | GetWorktreeCommand cmd -> Some(truncate 64 cmd.WorktreePath)
        | CreateProjectCommand cmd -> Some(truncate 64 cmd.Name)
        | CreateWorktreeCommand cmd -> Some(truncate 64 cmd.Name)
        | InspectWorktreeRemovalCommand cmd -> Some(truncate 64 cmd.WorktreePath)
        | RemoveWorktreeCommand cmd -> Some(truncate 64 cmd.WorktreePath)
        | TaskReadCommand cmd -> Some(truncate 64 cmd.AgentId)
        | ObserveAgentCommand cmd -> Some(truncate 64 cmd.TargetAgentId)
        | TaskWriteCommand cmd
        | CrossProjectNotificationCommand { Notification = cmd } -> Some(truncate 64 cmd.Action)
        | ChallengeReadCommand cmd -> Some(truncate 64 cmd.Action)
        | ChallengeWriteCommand cmd -> Some(truncate 64 cmd.Action)
        | LedgerReadCommand cmd -> Some(truncate 64 cmd.ProjectName)
        | LedgerWriteCommand cmd -> Some(truncate 64 cmd.Action)
        | ReviewReadCommand cmd -> Some(truncate 64 cmd.Action)
        | ReviewWriteCommand cmd -> Some(truncate 64 cmd.Action)
        | RetryPendingIntentCommand cmd -> Some(truncate 64 cmd.OperationId)
        | SupersedePendingIntentCommand cmd -> Some(truncate 64 cmd.OperationId)
        | AbandonPendingIntentCommand cmd -> Some(truncate 64 cmd.OperationId)
        | GetGitDiffCommand cmd -> cmd.Path |> Option.map (truncate 64)
        | GitCommitCommand cmd -> Some(quoted cmd.Message)
        | StartJobCommand cmd -> Some(commandPreview cmd.Executable cmd.Args)
        | GetJobResultCommand cmd -> Some(shortId cmd.JobId)
        | GetProcessOwnershipEvidenceCommand cmd -> Some(shortId cmd.JobId)
        | CancelJobCommand cmd -> Some(shortId cmd.JobId)
        | MessageCommand cmd -> Some(truncate 64 cmd.Message)
        | GetClientActivityCommand _ -> None
        | ListCommandsCommand
        | ListProjectsCommand
        | ListWorktreesCommand _
        | GetProjectDetailsCommand _
        | ListConfiguredTasksCommand _
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
    // Witness-capable admission and deferred ToolOperation receipts retain
    // process-return identity separately from proven process-tree quiescence.
    // 3.67 adds a read-only historical StartJob ownership query, NEVER OS
    // termination proof. Prior 3.65/3.66 legacy approvals cannot carry over.
    let version = "3.67"
    let defaultPatchFuzzyContextLines = 3
    let maxResponseBytes = 900 * 1024
    let maxImageBytes = 8 * 1024 * 1024
    let maxExportBytes = 8 * 1024 * 1024
    // Bound source work before allocations and before the transport's final
    // response serialization guard. Large sources use ReadFilePage instead.
    let maxLegacyTextReadBytes = 512 * 1024
    let maxRangeScanBytes = 8 * 1024 * 1024
    let maxRangeReturnBytes = 256 * 1024
    let maxRangeLines = 2048
    let maxSearchFileBytes = 8 * 1024 * 1024
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
          definition "BrowserListWindows" "Lists user-registered browser windows, without exposing unrelated windows or tabs." [ ReadOnly ] false false false
          definition "BrowserCloseTab" "Closes only a Jarvis-created tab with verified identity." [ ProcessExecution ] true true false
          definition "BrowserRunInNewTab" "Creates a tab in a registered window, runs a project script, and optionally closes the tab." [ ProcessExecution; NetworkAccess ] true true false
          definition "BrowserListTabs" "Lists tabs registered through the extension, including opted-in agent-created tabs." [ ReadOnly ] false false false
          definition "ScheduleBrowserExecution" "Persist a one-shot or recurring action that runs a project JS file in a new tab in a registered window." [ ProcessExecution; NetworkAccess ] true true false
          definition "ListScheduledBrowserExecutions" "List queued, completed and failed local browser actions without disclosing scripts or arguments." [ ReadOnly ] false false false
          definition "CancelScheduledBrowserExecution" "Cancel a pending one-shot or recurring browser action." [ ProcessExecution ] true true false
          definition "BrowserOpenTab" "Opens a new background tab in a user-registered window on an explicitly permitted site." [ ProcessExecution; NetworkAccess ] true true false
          definition "BrowserExecuteFile" "Executes JavaScript from a bounded project file in a registered tab." [ ProcessExecution; NetworkAccess ] true true false
          definition "BrowserExecuteJs" "Executes supplied JavaScript in a registered browser tab." [ ProcessExecution; NetworkAccess ] true true false
          definition "BrowserCaptureDom" "Capture live DOM markup after an explicit wait condition." [ ProcessExecution ] false true false
          definition "BrowserExportDom" "Write an explicitly requested live DOM snapshot to a project file." [ ProcessExecution; WorkspaceWrite ] true true false
          definition "CreateProject" "Create a new workspace folder with optional Git, starter files and remote-backed ledger." [ WorkspaceWrite; VersionControlWrite; NetworkAccess ] true true false
          definition "ListWorktrees" "Lists Git worktrees registered for a project." [ ReadOnly ] false false false
          definition "GetWorktree" "Inspects a registered Git worktree." [ ReadOnly ] false false false
          definition "CreateWorktree" "Creates an isolated Git worktree and branch." [ VersionControlWrite ] true true false
          definition "InspectWorktreeRemoval" "Checks registered worktree removal safety." [ ReadOnly ] false false false
          definition "TaskRead" "Reads a registered agent inbox from a single ledger snapshot." [ ReadOnly; NetworkAccess ] false false false
          definition "ObserveAgent" "Read an explicitly named target agent without attributing tool activity or beginning a Ready turn. Observer identity is independently verified." [ ReadOnly; NetworkAccess ] false false false
          definition "TaskWrite" "Creates/assigns/starts tasks and binds immutable review base." [ VersionControlWrite; NetworkAccess ] true true false
          definition "ChallengeRead" "Read issued or owned durable challenge conditions from the verified ledger." [ ReadOnly; NetworkAccess ] false false false
          definition "ChallengeWrite" "Atomically create, satisfy, block, reopen, or cancel durable agent challenges." [ VersionControlWrite; NetworkAccess ] true true false
          definition "LedgerRead" "Inspect the verified canonical Git ledger identity and trust head." [ ReadOnly; NetworkAccess ] false false false
          definition "LedgerWrite" "Explicitly bind, initialize or rebind the canonical ledger with privacy acknowledgment." [ VersionControlWrite; NetworkAccess ] true true false
          definition "ReviewRead" "Reads opt-in canonical review ledger requests and reviewer inboxes." [ ReadOnly; NetworkAccess ] false false false
          definition "ReviewWrite" "Updates opt-in canonical review ledger with a stable operation UUID." [ VersionControlWrite; NetworkAccess ] true true false
          definition "RetryPendingIntent" "Operator-authorized retry of an exact saved intent through current typed CAS writers, preserving UUID." [ VersionControlWrite; NetworkAccess ] true true false
          definition "SupersedePendingIntent" "Operator-authorized immutable local archive of an uncertain intent with verified related Applied receipt; no remote ledger mutation." [ WorkspaceWrite; VersionControlWrite; NetworkAccess ] true true false
          definition "AbandonPendingIntent" "Operator-authorized archival of an obsolete saved intent using typed canonical task/review state at a pinned verified head; no remote mutation." [ WorkspaceWrite; VersionControlWrite; NetworkAccess ] true true false
          definition "RemoveWorktree" "Removes a clean integrated Git worktree." [ VersionControlWrite; Destructive ] true true false
          definition "GetProjectDetails" "Reads project summary details and special files." [ ReadOnly ] false false false
          definition "ListDirectory" "Lists a bounded project directory; oversized listings fail explicitly." [ ReadOnly ] false false false
          definition "SearchFiles" "Finds project file/folder names with lazy traversal. Returns Matches, StopReason and visit counts; non-Finished results are partial." [ ReadOnly ] false false false
          definition "SearchText" "Finds text matches with bounded scans. Returns Matches, StopReason and visit counts; non-Finished results are partial." [ ReadOnly ] false false false
          definition "ReadFile" "Reads a small project text file or line range; use ReadFilePage for large UTF-8 sources." [ ReadOnly ] false false false
          definition "ReadFilePage" "Reads a bounded UTF-8 page from a file, with snapshot-aware continuation." [ ReadOnly ] false false false
          definition "ReadFiles" "Reads at most 32 files with an aggregate serialized-byte budget and per-file errors." [ ReadOnly ] false false false
          definition "GetAgentInstructions" "Reads all applicable AGENTS.md files for a checkout directory, with continuation for transport-size limits." [ ReadOnly ] false false false
          definition "ReadImage" "Reads one PNG, JPEG, or WebP image for model vision." [ ReadOnly ] false false false
          definition "ExportFile" "Copies one bounded project file to a short-lived user download URL." [ ReadOnly ] false false false
          definition "ImportFile" "Downloads a ChatGPT-provided file into a project path." [ WorkspaceWrite; NetworkAccess ] true true false
          definition "UpdateClient" "Downloads, verifies, installs, and restarts into the latest published Jarvis client." [ ProcessExecution; NetworkAccess; Destructive ] true false false
          definition "WriteFile" "Writes or appends one file." [ WorkspaceWrite ] true true false
          definition "PatchFile" "Applies an atomic unified diff to one file." [ WorkspaceWrite ] true true true
          definition "RunCommand" "Runs a bounded local process." [ ProcessExecution ] true true false
          definition "ListConfiguredTasks" "Lists locally configured project tasks." [ ReadOnly ] false false false
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
          definition "GetProcessOwnershipEvidence" "Reads device-local StartJob launch identity; never certifies descendant termination." [ ReadOnly ] false false false
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
