module Client.Result

let ofOption mapError option =
    match option with
    | Some value -> Ok value
    | None -> Error(mapError ())


let printError result =
    match result with
    | Error e -> printfn $"{e}"
    | _ -> ()
