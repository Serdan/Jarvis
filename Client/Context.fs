module Client.Context

open System
open Microsoft.FSharp.Core

let map (f: 'a -> 'b) (v: 'Context -> Result<'a>) (ctx: 'Context) : Result<'b> = v ctx |> Result.map f

let map' (f: 'a -> 'Context -> 'b) (v: 'Context -> Result<'a>) (ctx: 'Context) : Result<'b> =
    v ctx |> Result.map (fun a -> f a ctx)

let map'' (f: 'Context -> 'a -> 'b) (v: 'Context -> Result<'a>) (ctx: 'Context) : Result<'b> =
    v ctx |> Result.map (f ctx)


let bind (f: 'a -> Result<'b>) (v: 'Context -> Result<'a>) (ctx: 'Context) : Result<'b> = v ctx |> Result.bind f

let bind' (f: 'a -> 'Context -> Result<'b>) (v: 'Context -> Result<'a>) (ctx: 'Context) : Result<'b> =
    v ctx |> Result.bind (fun a -> f a ctx)

let bind'' (f: 'Context -> 'a -> Result<'b>) (v: 'Context -> Result<'a>) (ctx: 'Context) : Result<'b> =
    v ctx |> Result.bind (f ctx)

let defaultWith (e: unit -> exn) (f: 'Context -> Result<Option<'a>>) (ctx: 'Context) : Result<'a> =
    f ctx
    |> Result.bind (fun x ->
        match x with
        | Some v -> Ok v
        | None -> Error(e ()))

let tuple2 (f: 'Context -> Result<'a>) (f': 'Context -> Result<'b>) (ctx: 'Context) =
    match f ctx, f' ctx with
    | Ok a, Ok b -> Ok(a, b)
    | Ok _, Error e -> Error e
    | Error e, Ok _ -> Error e
    | Error e, Error e' -> [| e; e' |] |> AggregateException :> exn |> Error

let concat (f: 'Context -> Result<'a list>) (f': 'Context -> Result<'a list>) (ctx: 'Context) =
    match f ctx, f' ctx with
    | Ok a, Ok b -> a @ b |> Ok
    | Ok _, Error e -> Error e
    | Error e, Ok _ -> Error e
    | Error e, Error e' -> [| e; e' |] |> AggregateException :> exn |> Error
