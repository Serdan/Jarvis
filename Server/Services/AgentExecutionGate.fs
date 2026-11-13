namespace Server.Services

open System
open System.Collections.Concurrent
open System.Collections.Generic

// A coordination primitive for one authenticated single-instance MCP server.
// It is deliberately NOT wired into dispatch until a durable in-flight witness
// exists; an empty in-memory table after restart must never be taken as proof
// that an earlier client command has stopped. Git remains task-state authority.
type private AgentExecutionSlot =
    { Sync: obj
      Admitted: Dictionary<Guid, bool>
      mutable Correction: Guid option
      mutable Unknown: bool }

type AgentExecutionGate() =
    // Project selectors are intentionally absent. A logical project and a
    // worktree alias may route to the same canonical project; conservative
    // cross-project serialization is safer than a false negative.
    let slots = ConcurrentDictionary<struct (string * string), AgentExecutionSlot>()

    let slot userId agentId =
        if String.IsNullOrWhiteSpace userId || String.IsNullOrWhiteSpace agentId then
            invalidArg "agentId" "The authenticated user and initiating agent must be supplied."
        slots.GetOrAdd(
            struct (userId, agentId.ToUpperInvariant()),
            fun _ ->
                { Sync = obj()
                  Admitted = Dictionary<Guid, bool>()
                  Correction = None
                  Unknown = false })

    // journalAdmission must commit a durable request witness *before* source
    // dispatch. A failed/uncertain journal result permanently poisons this
    // process's ability to certify quiescence for this agent.
    member _.TryAdmit(userId, agentId, requestId: Guid,
                      journalAdmission: unit -> Result<unit, string>) =
        let current = slot userId agentId
        lock current.Sync (fun () ->
            if requestId = Guid.Empty then Error "Missing request ID."
            elif current.Correction.IsSome then
                Error "An administrative correction currently holds this agent's dispatch gate."
            elif current.Admitted.ContainsKey(requestId) then Error "Request is already admitted."
            else
                let recorded =
                    try journalAdmission()
                    with ex -> Error ex.Message
                match recorded with
                | Error error ->
                    current.Unknown <- true
                    Error $"Cannot durably witness agent activity: {error}"
                | Ok () ->
                    current.Admitted.Add(requestId, false)
                    Ok ())

    // Only an authenticated response accepted for the original device
    // generation, or proven non-dispatch, may call this. A normal MCP timeout
    // or failed HTTP response is NOT a terminal client receipt.
    member _.ClientTerminal(userId, agentId, requestId: Guid,
                            journalTerminal: unit -> Result<unit, string>) =
        let current = slot userId agentId
        lock current.Sync (fun () ->
            match current.Admitted.TryGetValue(requestId) with
            | false, _ -> Error "Request was not admitted."
            | true, _ ->
                let recorded =
                    try journalTerminal()
                    with ex -> Error ex.Message
                match recorded with
                | Ok () ->
                    current.Admitted.Remove(requestId) |> ignore
                    Ok ()
                | Error error ->
                    current.Admitted[requestId] <- true
                    current.Unknown <- true
                    Error $"Terminal operation evidence could not be persisted: {error}")

    member _.MarkIndeterminate(userId, agentId, requestId: Guid) =
        let current = slot userId agentId
        lock current.Sync (fun () ->
            match current.Admitted.TryGetValue(requestId) with
            | true, _ ->
                current.Admitted[requestId] <- true
                true
            | false, _ -> false)

    // Reserve the exclusive barrier BEFORE looking at durable history. During
    // verifyDurableQuiescence, new agent commands cannot be admitted. The
    // callback must inspect server-restart/connection-generation unknowns and
    // confirm there are no unresolved client operations or child jobs.
    member _.TryBeginCorrection(userId, targetAgentId,
                                verifyDurableQuiescence: unit -> Result<unit, string>) =
        let current = slot userId targetAgentId
        let permitId = Guid.NewGuid()
        let admitted =
            lock current.Sync (fun () ->
                if current.Correction.IsSome then Error "Correction already in progress."
                elif current.Unknown || current.Admitted.Count > 0 then
                    Error "Agent has in-flight or indeterminate operations."
                else
                    current.Correction <- Some permitId
                    Ok ())
        match admitted with
        | Error message -> Error message
        | Ok () ->
            let verified =
                try verifyDurableQuiescence()
                with ex -> Error ex.Message
            match verified with
            | Ok () -> Ok permitId
            | Error message ->
                lock current.Sync (fun () ->
                    if current.Correction = Some permitId then
                        current.Correction <- None)
                Error $"Cannot prove target agent is inactive: {message}"

    // The caller must resolve the exact canonical Git operation receipt first.
    // Unknown correction outcome retains the exclusive barrier.
    member _.EndCorrection(userId, targetAgentId, permitId, terminal: bool) =
        let current = slot userId targetAgentId
        lock current.Sync (fun () ->
            if current.Correction <> Some permitId then Error "Mismatched correction permit."
            elif not terminal then Error "Correction outcome is indeterminate; gate remains exclusive."
            else
                current.Correction <- None
                Ok ())

    member _.Snapshot(userId, agentId) =
        let current = slot userId agentId
        lock current.Sync (fun () ->
            current.Admitted.Count,
            (current.Admitted.Values |> Seq.filter id |> Seq.length),
            current.Correction.IsSome,
            current.Unknown)
