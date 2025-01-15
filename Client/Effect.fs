namespace Client.Effect

open Client
open Microsoft.FSharp.Core

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

type IO<'rt, 'r> = 'rt -> Result<'r, EffectError>

module Effect =
    let inline liftValue x : IO<_, _> = fun _ -> Ok x

    let inline lift (f: 'a -> 'b) (x: 'a) : IO<_, _> = fun _ -> f x |> Ok

    let inline lift' (f: 'a -> Result<'b, EffectError>) (a: 'a) : IO<_, _> = fun _ -> f a

    let inline ofError (err) : IO<_, _> = fun _ -> Error err

    let inline bind f io : IO<'rt, 'a> =
        fun rt ->
            match io rt with
            | Ok result -> f result rt
            | Error err -> Error err

    let inline map f io : IO<'rt, 'a> = bind (fun x -> liftValue (f x)) io

    let inline defaultValue (value: 'a) (io: IO<'rt, 'a>) : IO<'rt, 'a> =
        fun rt ->
            match io rt with
            | Ok result -> Ok result
            | Error _ -> Ok value

    let inline defaultWith (f: EffectError -> 'a) (io: IO<'rt, 'a>) : 'rt -> 'a =
        fun rt ->
            match io rt with
            | Ok result -> result
            | Error err -> f err

    let inline ofOption err (option: 'a option) =
        fun _ ->
            match option with
            | None -> err () |> Error
            | Some value -> Ok value

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

[<AutoOpen>]
module Operators =
    let inline (>>=) io f : IO<'rt, 'a> = Effect.bind f io
    let inline (|>>) io f : IO<'rt, 'a> = io >>= fun x -> Effect.liftValue (f x)

    let inline (>=>) f g : 'a -> IO<'rt, _> =
        fun x rt ->
            match f x rt with
            | Ok y -> g y rt
            | Error err -> Error err

    type EffectBuilder() =
        member _.Return(x: 'a) : IO<'rt, 'a> = fun _ -> Ok x

        member _.Bind(m: IO<'rt, 'v>, f: 'v -> IO<'rt, 'r>) : IO<'rt, 'r> = Effect.bind f m

        member _.ReturnFrom(m: IO<'rt, 'v>) : IO<'rt, 'v> = m

        member _.Zero() : IO<'rt, unit> = fun _ -> Ok()

    let effect = EffectBuilder()
