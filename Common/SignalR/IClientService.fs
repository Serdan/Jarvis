namespace Common.SignalR

open System.Threading.Tasks
open Common

type IClientService =
    abstract ReceiveMessage: message: string -> Task
    abstract ReceiveCommand: correlationId: string * commandJson: string * agentId: string -> Task
    /// Best-effort cross-device hint: the project ledger may have changed agent assignments.
    abstract InvalidateAgentStates: projectName: string * agentIds: string array -> Task
