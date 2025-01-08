namespace Client

open System
open System.IO
open FsToolkit.ErrorHandling
open Microsoft.FSharp.Core

[<AutoOpen>]
module Misc =
    let tuple2 a b = (a, b)

    let ignoreAll = IgnoreBuilder()

module Seq =
    let filterAll filters source =
        source
        |> Seq.filter (fun item -> filters |> Seq.forall (fun filter -> filter item))

module String =
    let startsWith value (s: string) =
        s.StartsWith(value, StringComparison.InvariantCulture)

    let tryIndexOf' (value: string) (startIndex: int) (s: string) =
        let index = s.IndexOf(value, startIndex, StringComparison.Ordinal)
        if index >= 0 then Some index else None

    let tryIndexOf (value: string) (s: string) = tryIndexOf' value 0 s

module Path =
    let combine a b = Path.Combine(a, b)
    let combineAll (segments: string seq) = segments |> Seq.toArray |> Path.Combine

module Result =
    let ofOption mapError option =
        match option with
        | Some value -> Ok value
        | None -> Error(mapError ())

    let printError result =
        match result with
        | Error e -> printfn $"{e}"
        | _ -> ()

    let printExceptionError (result: Result<'a, exn>) =
        match result with
        | Error e -> printfn $"{e.Message}"
        | _ -> ()

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ProjectDirectory =
    let toFolderPath (ProjectDirectory projectDirectory) = FolderPath projectDirectory
