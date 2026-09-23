namespace Common.SignalR

open System
open System.Threading.Tasks
open Common

type ClientRegistration =
    { DeviceId: string
      DeviceName: string
      ProtocolVersion: string
      ClientVersion: string }

type ClientRegistrationResult =
    { Generation: int64
      RegisteredAt: DateTimeOffset
      RegistrationReason: string }

type IHubService =
    abstract Connect: registration: ClientRegistration -> Task<ClientRegistrationResult>
    abstract SendClientResponse: correlationId: string * response: AgentCommandResponse -> Task
