namespace Client

open System
open System.Text.Json
open Client.AgentActivityLedger
open Client.LedgerStore
open Common
open Common.SignalR

/// Pure transport parsing plus the minimum authenticated Git publication gate.
/// The caller may invoke the existing command executor ONLY on Ok(commandJson).
/// No other receive path, including automatic promoted jobs, can skip this gate.
module AgentActivityIngress =
    let private unavailable reason = Error("ActivityProofUnavailable: " + reason)

    let private attributedProjectCommand assertedAgentId (command: AgentCommand) =
        AgentActivityLedger.requiresProof assertedAgentId command

    let private decodeCommand (commandJson: string) =
        try
            let command = JsonSerializer.Deserialize<AgentCommand>(commandJson)
            if isNull (box command) then None else Some command
        with _ -> None

    /// loadVerified must resolve an operator-trusted project binding and fetch
    /// the authoritative remote Git head. Never substitute a local checkout,
    /// cache, TUI hint, SQLite activity, or a client-provided project identity.
    let admitWith
        (loadVerified: string -> Result<LedgerBinding * LedgerSnapshot, string>)
        (session: AgentActivitySessionContext option)
        (correlationId: string)
        (assertedAgentId: string)
        (commandJson: string) : Result<string, string> =

        match AgentActivityLedger.tryDecodeEnvelope commandJson with
        | Error reason -> Error reason
        | Ok None ->
            match decodeCommand commandJson with
            | Some command when attributedProjectCommand assertedAgentId command ->
                unavailable "this project-scoped agent command is missing its server-authenticated activity envelope"
            | _ -> Ok commandJson
        | Ok(Some envelope) ->
            match session, decodeCommand envelope.CommandJson with
            | None, _ ->
                unavailable "the authenticated live client registration and connection are unavailable"
            | _, None ->
                unavailable "the enveloped agent command cannot be decoded"
            | Some expected, Some command ->
                if String.IsNullOrWhiteSpace correlationId
                   || correlationId <> envelope.Activity.CorrelationId then
                    unavailable "the activity event does not belong to this command correlation"
                else
                    match AgentActivityLedger.validateDispatch expected assertedAgentId command envelope.Activity with
                    | Error reason -> Error reason
                    | Ok () ->
                        match loadVerified envelope.Activity.ProjectName with
                        | Error _ ->
                            unavailable "the trusted project ledger and verified remote snapshot cannot be loaded"
                        | Ok(binding, snapshot) ->
                            match AgentActivityLedger.record binding snapshot expected assertedAgentId envelope.Activity command with
                            | Error reason -> Error reason
                            | Ok published when published.AlreadyApplied ->
                                unavailable "this activity event was already committed; the tool effect cannot be replayed"
                            | Ok _ -> Ok envelope.CommandJson
