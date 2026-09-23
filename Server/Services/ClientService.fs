namespace Server.Services

open System
open System.Threading.Tasks
open System.Text.Json
open Common
open Common.SignalR
open Microsoft.AspNetCore.SignalR
open Microsoft.FSharp.Core

type ClientService(ctx: IHubContext<HubService, IClientService>, users: UserService, tracker: ClientResponseTracker) =
    member this.SendMessageToAll(message) = ctx.Clients.All.ReceiveMessage(message)

    member private this.ResponseTimeout(command: AgentCommand) =
        let permissionMargin = TimeSpan.FromMinutes(5L)
        match command with
        | RunCommandCommand cmd ->
            let executionSeconds = cmd.TimeoutSeconds |> Option.defaultValue 60 |> max 1
            TimeSpan.FromSeconds(float executionSeconds) + permissionMargin
        | _ -> TimeSpan.FromMinutes(10L)

    member this.SendCommandToUser(userId: string, command: AgentCommand) =
        task {
            match users.GetSession(userId) with
            | ValueNone ->
                return
                    { Result = None
                      Error = Some(NotFound "No Jarvis client has registered for the authenticated user.") }
            | ValueSome session ->
                match session.State, session.ConnectionId with
                | Disconnected, _ ->
                    let reason = session.LastFailure |> Option.defaultValue "Transport disconnected"
                    return
                        { Result = None
                          Error =
                            Some(
                                ExecutionFailed
                                    $"Jarvis device {session.DeviceName} is disconnected (generation {session.Generation}, protocol {session.ProtocolVersion}, last seen {session.LastSeenAt:O}). Reason: {reason}"
                            ) }
                | Registered, None ->
                    return
                        { Result = None
                          Error =
                            Some(
                                ExecutionFailed
                                    $"Jarvis device {session.DeviceName} generation {session.Generation} is registered without an active connection."
                            ) }
                | Registered, Some connectionId ->
                    let correlationId, trackingTask = tracker.Register(this.ResponseTimeout command)
                    let client = ctx.Clients.Client(connectionId)
                    let commandJson = JsonSerializer.Serialize<AgentCommand>(command)

                    try
                        do! client.ReceiveCommand(correlationId, commandJson)
                        users.RecordDispatchSuccess(userId, connectionId)

                        try
                            return! trackingTask
                        with :? TaskCanceledException ->
                            let reason =
                                $"Client response timeout for {session.DeviceName} generation {session.Generation}."
                            users.RecordDispatchFailure(userId, connectionId, reason)
                            return
                                { Result = None
                                  Error =
                                    Some(
                                        ExecutionFailed
                                            $"{reason} Protocol {session.ProtocolVersion}, client {session.ClientVersion}, last seen {session.LastSeenAt:O}."
                                    ) }
                    with ex ->
                        tracker.Cancel(correlationId)
                        let reason =
                            $"Dispatch failed for {session.DeviceName} generation {session.Generation}: {ex.Message}"
                        users.RecordDispatchFailure(userId, connectionId, reason)
                        return
                            { Result = None
                              Error = Some(ExecutionFailed reason) }
        }
