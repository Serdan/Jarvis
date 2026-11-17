namespace Common.SignalR

open System
open System.Threading.Tasks
open Common

/// Versioned client route IDs. Legacy opaque DeviceIds remain supported.
type RoutedClientIdentity =
    { MachineId: string
      InstallationId: string
      InstanceId: string }

module DeviceRouteIdentity =
    [<Literal>]
    let Prefix = "jarvis2:"

    let create machineId installationId instanceId =
        let canonical value =
            match Guid.TryParseExact(value, "D") with
            | true, guid when guid <> Guid.Empty -> guid.ToString("D")
            | _ -> invalidArg "value" "Route identity components must be nonempty canonical UUIDs."
        Prefix + canonical machineId + ":" + canonical installationId + ":" + canonical instanceId

    let tryParse (deviceId: string) =
        if String.IsNullOrWhiteSpace(deviceId) then None
        else
            match deviceId.Split(':') with
            | [| "jarvis2"; machine; installation; instance |] ->
                match Guid.TryParseExact(machine, "D"),
                      Guid.TryParseExact(installation, "D"),
                      Guid.TryParseExact(instance, "D") with
                | (true, m), (true, i), (true, s)
                    when m <> Guid.Empty && i <> Guid.Empty && s <> Guid.Empty ->
                    Some { MachineId = m.ToString("D")
                           InstallationId = i.ToString("D")
                           InstanceId = s.ToString("D") }
                | _ -> None
            | _ -> None

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