module Common.ValueOption

let toResult message voption =
    match voption with
    | ValueSome v -> Ok v
    | ValueNone -> message |> exn |> Error
