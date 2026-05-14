namespace Common.SignalR

open System.Threading.Tasks
open Common

type IClientService =
    abstract ReceiveMessage: message: string -> Task
    abstract ReceiveCommand: correlationId: string * commandJson: string -> Task
