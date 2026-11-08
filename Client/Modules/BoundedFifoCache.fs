namespace Client

open System
open System.Collections.Generic

/// Small process-wide FIFO of immutable content-addressed read models.
/// Bounds count and estimated retained bytes; an entry too large for the
/// budget is served to the current request but is never retained.
type internal BoundedFifoCache<'value>(maxEntries: int, maxWeightBytes: int64) =
    do
        if maxEntries < 1 || maxWeightBytes < 1L then
            invalidArg "maxEntries" "Cache entry and byte limits must be positive."

    let gate = obj()
    let entries = Dictionary<string, struct ('value * int64)>(StringComparer.Ordinal)
    let insertionOrder = Queue<string>()
    let mutable retainedBytes = 0L

    member _.TryGet(key: string) =
        lock gate (fun () ->
            match entries.TryGetValue(key) with
            | true, struct (value, _) -> ValueSome value
            | false, _ -> ValueNone)

    member _.Add(key: string, value: 'value, estimatedBytes: int64) =
        if String.IsNullOrWhiteSpace key then invalidArg "key" "A content-addressed key is required."
        if estimatedBytes < 0L then invalidArg "estimatedBytes" "An estimated retained size cannot be negative."
        if estimatedBytes <= maxWeightBytes then
            lock gate (fun () ->
                if not (entries.ContainsKey key) then
                    // Evict before inserting. A request must keep its own
                    // resolved objects, not depend on these cache entries
                    // surviving another concurrent project refresh.
                    while entries.Count >= maxEntries || retainedBytes > maxWeightBytes - estimatedBytes do
                        let oldest = insertionOrder.Dequeue()
                        match entries.TryGetValue oldest with
                        | true, struct (_, size) ->
                            entries.Remove(oldest) |> ignore
                            retainedBytes <- retainedBytes - size
                        | false, _ -> invalidOp "Bounded cache insertion order is inconsistent."
                    entries.Add(key, struct (value, estimatedBytes))
                    insertionOrder.Enqueue key
                    retainedBytes <- retainedBytes + estimatedBytes)

    member _.Counts =
        lock gate (fun () -> struct (entries.Count, retainedBytes))
