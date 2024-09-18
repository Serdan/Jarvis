module Client.UserClient

open System.Threading.Tasks

let receiveMessage message = printfn $"%s{message}"

let receiveCommand hub browser (correlationId: string) (command: AgentCommand) = task { return () } :> Task
