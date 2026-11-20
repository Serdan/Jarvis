namespace Common.SignalR

open System
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks
open Common

/// Server-authenticated provenance for one project-scoped agent MCP dispatch.
/// The client validates every field against its live SignalR session before
/// publishing the watermark. ConnectionId is transport metadata, not a bearer.
[<CLIMutable>]
type AgentActivityDispatch =
    { SchemaVersion: int
      ProjectName: string
      AgentId: string
      AccountScope: string
      DeviceId: string
      ConnectionId: string
      ConnectionGeneration: int64
      CorrelationId: string
      EventId: string
      CommandName: string
      ObservedAtUtc: DateTimeOffset }

/// Backwards-compatible envelope over the existing command JSON argument.
/// Older clients do not advertise the capability and therefore never receive it.
[<CLIMutable>]
type AgentActivityCommandEnvelope =
    { SchemaVersion: int
      CommandJson: string
      Activity: AgentActivityDispatch }

module AgentActivityProtocol =
    /// Negotiated through SupportedCommands without changing the hub contract.
    let capability = "AgentActivityIngressV1"
    let schemaVersion = 1
    let recordKind = "AgentActivityWatermark"
    let sourceKind = "AuthenticatedMcpToolIngress"

    /// Stable account partition shared by the authenticated server and client.
    /// The raw OAuth subject is never written to the project Git ledger.
    let accountScopeFromSubject (subject: string) =
        if String.IsNullOrWhiteSpace subject then None
        else
            let digest = SHA256.HashData(Encoding.UTF8.GetBytes subject)
            Some("operator:" + Convert.ToHexString(digest).ToLowerInvariant())

type IClientService =
    abstract ReceiveMessage: message: string -> Task
    abstract ReceiveCommand: correlationId: string * commandJson: string * agentId: string -> Task
    /// Caller-retained UUID transport for a separate, opt-in, nonproduction
    /// source-file diagnostic route. Legacy ReceiveCommand never acquires it.
    abstract ReceiveSourceCommand: correlationId: string * commandJson: string * agentId: string * originalOperationId: string -> Task
    // Server-only routed manual grant authority: no bearer crosses this boundary.
    abstract ReceiveManualGrantedCommand: correlationId: string * commandJson: string * agentId: string -> Task
    abstract ReceiveManualGrantApproval: correlationId: string * requestJson: string * agentId: string -> Task
    /// Best-effort cross-device hint: the project ledger may have changed agent assignments.
    abstract InvalidateAgentStates: projectName: string * agentIds: string array -> Task
    /// The mutation scope is unknown: invalidate the complete local agent-state cache.
    abstract InvalidateAllAgentStates: unit -> Task
    /// Authenticated cross-device activity hint; never a canonical agent-state mutation.
    abstract RecordAgentActivity: projectName: string * agentId: string * observedAtUtc: System.DateTimeOffset -> Task
