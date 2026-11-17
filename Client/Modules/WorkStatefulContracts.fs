module Client.WorkStatefulContracts

open System

// H0 contract vocabulary only. There are no live endpoints, credentials,
// mutations, or authority adapters in this module. H1 must explicitly bind
// authenticated server responses after the migration/recovery gates.
[<Literal>]
let ApiVersion = 1

[<RequireQualifiedAccess>]
type Evidence<'a> =
    | Verified of atRevision: int64 * value: 'a
    | Unknown of reason: string

[<RequireQualifiedAccess>]
type SnapshotOrigin =
    | ExplicitFixture of description: string
    | AuthenticatedServer of authorityId: string

[<RequireQualifiedAccess>]
type NodeKind = Project | System | Feature

[<RequireQualifiedAccess>]
type NodeState = Draft | Proposed | Open | Active | Completed | Closed | Cancelled

[<RequireQualifiedAccess>]
type ProposalState =
    | NotSubmitted
    | AwaitingLead
    | Returned of reason: string
    | Approved of decisionId: string
    | Unknown of reason: string

[<RequireQualifiedAccess>]
type AcceptanceState =
    | AwaitingIndependentAcceptance
    | Accepted of evidenceRef: string
    | NotRequired
    | Unknown of reason: string

type WorkNode =
    { Id: string
      ProjectId: string
      ParentId: string option
      Kind: NodeKind
      Title: string
      Version: int
      State: NodeState
      Proposal: ProposalState
      RequiredForParent: bool
      Acceptance: AcceptanceState }

[<RequireQualifiedAccess>]
type StageKind =
    | Planning
    | Approval
    | Implementation
    | FrozenReview
    | Integration
    | Recovery
    | RoleMessage

[<RequireQualifiedAccess>]
type ExecutionObservation = Running | Stopped | Unknown of reason: string

type Claim =
    { LaunchId: string
      Generation: int
      Execution: Evidence<ExecutionObservation> }

[<RequireQualifiedAccess>]
type StageState =
    | Pending
    | Available
    | Blocked of reasons: string list
    | Claimed of Claim
    | Finished
    | Cancelled

[<RequireQualifiedAccess>]
type ReviewStage =
    | NotRequired
    | AwaitingFrozenSource
    | Frozen of commit: string
    | Accepted of commit: string
    | ChangesRequested of commit: string
    | Unknown of reason: string

[<RequireQualifiedAccess>]
type IntegrationStage =
    | NotRequired
    | AwaitingAcceptedReview
    | Submitted of sourceCommit: string
    | Integrated of integrationCommit: string
    | Unknown of reason: string

type WorkStage =
    { Id: string
      // Canonical project instance; node/stage IDs are only project-local.
      // Never join or authorize by an unqualified ID.
      ProjectId: string
      NodeId: string
      QueueId: string
      Kind: StageKind
      Generation: int
      State: StageState
      LeadPriority: Evidence<int>
      IsBlocking: Evidence<bool>
      Eligible: Evidence<bool>
      AvailableSequence: int64
      UnmetHardDependencies: Evidence<string list>
      Review: ReviewStage
      Integration: IntegrationStage }

type ReadScope =
    { AllowedProjectIds: Set<string>
      Authenticated: bool
      CanClaim: bool }

type Capacity =
    { Limit: int
      Occupied: int }

type BackupStatus =
    { ExportSchemaVersion: int
      ExportedRevision: int64
      SnapshotAt: DateTimeOffset
      PublishedAt: DateTimeOffset option
      VerifiedExport: bool }

[<RequireQualifiedAccess>]
type RecoveryMode =
    | Normal
    | Fenced of reason: string
    | Investigating of reason: string

type Snapshot =
    { Version: int
      Revision: int64
      CapturedAt: DateTimeOffset
      Origin: SnapshotOrigin
      Scope: Evidence<ReadScope>
      RoleQueues: Evidence<Map<string, Set<string>>>
      Capacity: Evidence<Capacity>
      Backup: Evidence<BackupStatus>
      Recovery: Evidence<RecoveryMode>
      Nodes: WorkNode list
      Stages: WorkStage list }

// Versioned H1 transport contracts. Authentication is provided by the future
// authenticated transport and server reservation policy, never by a caller
// supplied role/agent label. The client H0 layer does not execute commands.
type SnapshotRequest =
    { ApiVersion: int
      ProjectId: string option
      RoleId: string option
      QueueId: string option
      IncludeHistoricalAgentAudits: bool }

[<RequireQualifiedAccess>]
type WorkCommand =
    | SubmitProposal of nodeId: string
    | ReturnProposal of nodeId: string * reason: string
    | ApproveProposal of nodeId: string
    | SetLeadPriority of stageId: string * priority: int
    | Claim of stageId: string
    | SubmitFrozenReview of stageId: string * commit: string
    | RecordIntegration of stageId: string * commit: string
    | ReleaseRecoveryFence of evidenceRef: string

type CommandRequest =
    { ApiVersion: int
      // Scope all project-local subject IDs to this canonical project.
      // The server must verify the ID against authenticated authority.
      ProjectId: string
      OperationId: Guid
      ExpectedSnapshotRevision: int64
      ExpectedSubjectVersion: int
      // H1 stage commands require an exact generation to fence stale effects;
      // None is reserved for project/node commands.
      ExpectedStageGeneration: int option
      Command: WorkCommand }

[<RequireQualifiedAccess>]
type CommandOutcome =
    | Applied of newRevision: int64
    | Rejected of reason: string * observedRevision: int64 option
    | UnknownOutcome of operationId: Guid
