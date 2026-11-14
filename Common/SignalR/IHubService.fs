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

type IHubService =
    abstract Connect: registration: ClientRegistration -> Task<ClientRegistrationResult>
    abstract GetAgentActivity: projectName: string * agentId: string -> Task<AgentActivitySnapshot>
    abstract SendClientResponse: correlationId: string * response: AgentCommandResponse -> Task
