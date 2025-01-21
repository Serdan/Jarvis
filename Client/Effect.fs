namespace Client.Effect

open Client
open Microsoft.FSharp.Core
open Kehlet.FSharp.IO

module EffectError =
    let rec toString error =
        match error with
        | ExceptionError exn -> $"ExceptionError: {exn.Message}"
        | GenericError message -> $"GenericError: {message}"
        | ValidationError message -> $"ValidationError: {message}"
        | ContextError message -> $"ContextError: {message}"
        | NotFoundError resource -> $"NotFoundError: {resource} not found"
        | PermissionDenied resource -> $"PermissionDenied: Access denied to {resource}"
        | AggregatedErrors errors ->
            let errorList = errors |> List.map toString |> String.concat "\n"
            $"AggregatedErrors:\n{errorList}"

type IO<'runtime, 'a> = IO<'runtime, 'a, EffectError>

module Effect =
    let concat (f: IO<'rt, 'a list>) (f': IO<'rt, 'a list>) : IO<'rt, 'a list> =
        fun rt ->
            match f rt, f' rt with
            | Ok a, Ok b -> a @ b |> Ok
            | Ok _, Error e -> Error e
            | Error e, Ok _ -> Error e
            | Error(AggregatedErrors e), Error(AggregatedErrors e') -> e @ e' |> AggregatedErrors |> Error
            | Error(AggregatedErrors e), Error e'
            | Error e', Error(AggregatedErrors e) -> e' :: e |> AggregatedErrors |> Error
            | Error e, Error e' -> [ e; e' ] |> AggregatedErrors |> Error
