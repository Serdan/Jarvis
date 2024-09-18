namespace Client.Lib

type OptionBuilder() =
    member _.Bind(x: 'a option, f: 'a -> 'b option) : 'b option =
        match x with
        | Some value -> f value
        | None -> None

    member _.Return(x: 'a) : 'a option = Some x

    member _.ReturnFrom(x: 'a option) : 'a option = x

    member _.Zero() : unit option = Some()

    member _.Combine(x: unit option, f: unit -> 'b option) : 'b option =
        match x with
        | Some() -> f ()
        | None -> None

    member _.Delay(f: unit -> 'a option) : unit -> 'a option = f

    member _.Run(f: unit -> 'a option) : 'a option = f ()

    member _.TryWith(f: unit -> 'a option, handler: exn -> 'a option) : 'a option =
        try
            f ()
        with ex ->
            handler ex

    member _.TryFinally(f: unit -> 'a option, compensation: unit -> unit) : 'a option =
        try
            f ()
        finally
            compensation ()

    member _.Using(resource: 'a :> System.IDisposable, body: 'a -> 'b option) : 'b option =
        try
            body resource
        finally
            if not (isNull resource) then
                resource.Dispose()

    member _.While(guard: unit -> bool, body: unit -> unit option) : unit option =
        let rec loop () =
            if guard () then
                match body () with
                | Some() -> loop ()
                | None -> None
            else
                Some()

        loop ()

    member _.For(sequence: seq<'a>, body: 'a -> unit option) : unit option =
        use enumerator = sequence.GetEnumerator()

        let rec loop () =
            if enumerator.MoveNext() then
                match body enumerator.Current with
                | Some() -> loop ()
                | None -> None
            else
                Some()

        loop ()
