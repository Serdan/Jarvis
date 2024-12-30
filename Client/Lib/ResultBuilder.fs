namespace Client.Lib

type ResultBuilder() =
    member _.Bind(result, binder) =
        match result with
        | Ok value -> binder value
        | Error err -> Error err

    member _.Return(value) = Ok value
    member _.ReturnFrom(result) = result
    member _.Zero() = Ok()

    member _.Delay(f) = f
    member _.Run(f) = f ()

    member _.Combine(r1, r2) =
        match r1 with
        | Ok() -> r2
        | Error err -> Error err

    member _.TryWith(body, handler) =
        try
            body ()
        with e ->
            handler e

    member _.TryFinally(body, compensation) =
        try
            body ()
        finally
            compensation ()

    member this.Using(resource: #System.IDisposable, body) =
        this.TryFinally(
            (fun () -> body resource),
            (fun () ->
                if not (isNull (box resource)) then
                    resource.Dispose())
        )

    member this.While(guard, body) =
        if not (guard ()) then
            Ok()
        else
            body () |> ignore
            this.While(guard, body)

    member this.For(sequence: seq<_>, body) =
        let enumerator = sequence.GetEnumerator()
        this.While((fun () -> enumerator.MoveNext()), (fun () -> body enumerator.Current))
