module Program

open System
open System.IO
open Client
open Client.Effect

[<EntryPoint>]
let main args =
    match args with
    | [| "--self-update-test-host"; livePath; stagedPath; backupPath; markerPath; errorPath; forwardedArgument |] ->
        let update: SelfUpdate.PreparedUpdate =
            { LivePath = livePath
              StagedPath = stagedPath
              BackupPath = backupPath
              Arguments = [| markerPath; forwardedArgument |] }

        match SelfUpdate.activate update with
        | Ok _ -> 0
        | Error error ->
            File.WriteAllText(errorPath, EffectError.toString error)
            1
    | _ -> 0
