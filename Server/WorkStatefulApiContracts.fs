namespace Jarvis.WorkStatefulApi

open System

// H1a transport vocabulary only. No endpoint, mutation, bearer, or fixture
// constructor is registered here. Authentication must precede this response.
[<RequireQualifiedAccess>]
module WorkReadVersion =
    [<Literal>]
    let Current = 1

type WorkReadRequest =
    { ApiVersion: int
      ProjectId: string
      RoleId: string option }

type WorkReadNode =
    { ProjectId: string
      Id: string
      RowVersion: int64
      GraphVersion: int64
      Kind: string option
      Title: string option
      ParentId: string option
      State: string option
      ProposalState: string option
      ApprovalReceipt: string option
      RequiredForParent: bool option
      AcceptanceEvidence: string option }

type WorkReadStage =
    { ProjectId: string
      Id: string
      RowVersion: int64
      GraphVersion: int64
      NodeId: string option
      QueueId: string option
      Kind: string option
      State: string option
      ProposalState: string option
      Gate: string option
      ClaimLaunchId: string option
      StageGeneration: int64
      ClaimGeneration: int64
      StopGeneration: int64
      SourceCommit: string option
      ReviewCommit: string option
      IntegrationCommit: string option
      AvailableSequence: int64 option
      LeadPriority: int option }

type WorkReadRole =
    { ProjectId: string
      Id: string
      RoleVersion: int64
      Queues: string list option }

type WorkReadCapacity =
    { Limit: int
      Occupied: int
      RowVersion: int64 }

type WorkReadSnapshot =
    { ApiVersion: int
      AuthorityId: string
      ProjectId: string
      Revision: int64
      CapturedAt: DateTimeOffset
      ProjectRowVersion: int64
      GraphVersion: int64
      StopGeneration: int64
      VerifiedRoleId: string option
      Nodes: WorkReadNode list
      Stages: WorkReadStage list
      Roles: WorkReadRole list
      Capacity: WorkReadCapacity option
      // F/G evidence is not authoritative until their reviewed source
      // is integrated. Never infer Recovery=Normal or "backed up".
      BackupUnknownReason: string
      RecoveryUnknownReason: string
      Diagnostics: string list }
