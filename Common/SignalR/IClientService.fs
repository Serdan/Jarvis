namespace Common.SignalR

open System.Threading.Tasks
open Common

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
