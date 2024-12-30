namespace Common.SignalR

open System.Threading.Tasks
open Common

type IClientService =
    abstract ReceiveMessage: message: string -> unit
    abstract ReceiveCommand: correlationId: string * command: AgentCommand -> Task
