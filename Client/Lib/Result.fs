module Client.Result

let ofOption mapError option =
    match option with
    | Some value -> Ok value
    | None -> Error(mapError ())
