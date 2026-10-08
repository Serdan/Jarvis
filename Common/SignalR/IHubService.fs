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

type IHubService =
    abstract Connect: registration: ClientRegistration -> Task<ClientRegistrationResult>
    abstract SendClientResponse: correlationId: string * response: AgentCommandResponse -> Task
