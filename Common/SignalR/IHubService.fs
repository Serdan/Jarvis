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

/// A nonauthoritative hint for the exact approved project ledger identity.
/// It contains no job result, terminal evidence or execution credential.
type JobWakeInvalidation =
    { ProjectName: string
      ProjectId: Guid
      LedgerId: Guid
      Epoch: int
      AgentIds: string array }

module JobWakeInvalidation =
    let private boundedLabel (value: string) =
        not (String.IsNullOrWhiteSpace value) && value.Length <= 256
        && not (value |> Seq.exists Char.IsControl)

    let valid (hint: JobWakeInvalidation) =
        not (isNull (box hint))
        && boundedLabel hint.ProjectName
        && ProjectSelector.parse hint.ProjectName = Some(hint.ProjectName, None)
        && hint.ProjectId <> Guid.Empty && hint.LedgerId <> Guid.Empty && hint.Epoch > 0
        && not (isNull hint.AgentIds) && hint.AgentIds.Length > 0 && hint.AgentIds.Length <= 256
        && Array.forall boundedLabel hint.AgentIds

type IHubService =
    abstract Connect: registration: ClientRegistration -> Task<ClientRegistrationResult>
    abstract GetAgentActivity: projectName: string * agentId: string -> Task<AgentActivitySnapshot>
    abstract PublishJobWakeInvalidation: hint: JobWakeInvalidation -> Task
    abstract SendClientResponse: correlationId: string * response: AgentCommandResponse -> Task
