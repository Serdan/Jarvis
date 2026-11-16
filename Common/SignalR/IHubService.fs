namespace Common.SignalR

open System
open System.Threading.Tasks
open Common

type ClientRegistration =
    { DeviceId: string
      DeviceName: string
      ProtocolVersion: string
      ClientVersion: string
      SupportedCommands: string list }

type ClientRegistrationResult =
    { Generation: int64
      RegisteredAt: DateTimeOffset
      RegistrationReason: string
      ServerProtocolVersion: string
      EnabledCommands: string list }

/// A fresh authenticated server-side read; absence means the manager has not
/// sent an attributed Jarvis tool call since this activity ledger began.
type AgentActivitySnapshot =
    { LastSeenAtUtc: DateTimeOffset option
      ServerNowUtc: DateTimeOffset }

/// Disposable display projections from client-verified Git ledgers. The
/// server does not own or authorize any canonical agent/assignment state.
/// CLR-only wire DTOs avoid F# union/option JSON converter dependencies.
type AgentRosterClaim =
    { Identity: string
      ProjectName: string
      Aliases: string array }

type AgentRosterFocus =
    { TaskId: string
      Title: string
      Lifecycle: string
      Phase: string
      SourceRef: string
      BaseCommit: string
      AssignmentGeneration: int }

type AgentRosterAgent =
    { AgentId: string
      State: string
      StateEnteredAtUtc: Nullable<DateTimeOffset>
      StateReason: string
      Focus: AgentRosterFocus
      RoleIds: string array
      Activity: string
      ReviewContext: string
      PendingNotifications: int
      LatestNotification: string }

type AgentRosterLedger =
    { Identity: string
      Head: string
      ProjectName: string
      Aliases: string array
      Agents: AgentRosterAgent array
      UnclaimedReviews: int
      PendingReviews: int }

type AgentRosterRefreshRequest =
    { RequestId: string
      Identity: string
      ProjectName: string
      PreviousHead: string }

type AgentRosterRefreshResponse =
    { RequestId: string
      Identity: string
      Data: AgentRosterLedger
      Error: string }

type AgentRosterLedgerView =
    { Identity: string
      ProjectName: string
      Aliases: string array
      Status: string
      Error: string
      ProviderMachines: string array
      Data: AgentRosterLedger }

type AgentRosterActivity =
    { Identity: string
      AgentId: string
      LastSeenAtUtc: DateTimeOffset
      DeviceName: string }

type AgentRosterView =
    { Ledgers: AgentRosterLedgerView array
      Activity: AgentRosterActivity array
      ServerNowUtc: DateTimeOffset }

type IHubService =
    abstract Connect: registration: ClientRegistration -> Task<ClientRegistrationResult>
    abstract GetAgentActivity: projectName: string * agentId: string -> Task<AgentActivitySnapshot>
    abstract AnnounceAgentLedgers: claims: AgentRosterClaim array -> Task
    abstract SubmitAgentRoster: response: AgentRosterRefreshResponse -> Task
    abstract GetAgentRoster: unit -> Task<AgentRosterView>
    abstract SendClientResponse: correlationId: string * response: AgentCommandResponse -> Task