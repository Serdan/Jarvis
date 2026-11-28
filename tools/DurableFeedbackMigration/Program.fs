open System
open System.IO
open Server.Services

[<EntryPoint>]
let main args =
    match args with
    | [| "--offline-confirmed"; "--legacy"; source; "--destination"; destination |] ->
        try
            // No runtime service registration; no legacy writes. The explicit
            // flag is an operator attestation, not automatic quiescence proof.
            let source, destination = Path.GetFullPath source, Path.GetFullPath destination
            if not (File.Exists source) then invalidOp "Legacy database does not exist."
            let store = DurableFeedbackStore(destination)
            store.InitializeOffline(legacyDatabasePath = source)
            printfn "Prepared destination with verified source identity, content, provenance and integrity. No activation performed."
            0
        with ex ->
            eprintfn "Offline preparation failed: %s. Preserve source, destination and sidecars for inspection; do not activate or overwrite them." ex.Message
            1
    | _ ->
        eprintfn "Usage: --offline-confirmed --legacy <quiesced-backed-up-legacy-db> --destination <new-db>"
        eprintfn "Requires explicit operator approval, a quiesced legacy feedback writer and a verified consistent backup. Does not activate the new database."
        2
