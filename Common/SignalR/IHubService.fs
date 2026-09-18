namespace Common.SignalR

open System.Threading.Tasks
open Common

type IHubService =
    abstract Connect: userId: string -> Task
    abstract SendClientResponse: correlationId: string * response: AgentCommandResponse -> Task
