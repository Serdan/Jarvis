namespace Common.SignalR

open System.Threading.Tasks

type IHubService =
    abstract Connect: userId: string -> Task
    abstract Disconnect: unit -> Task
    abstract SendClientResponse: correlationId: string * result: string -> Task
