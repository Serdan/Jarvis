open System
open Fixture

let red () =
    withProducer (fun json ->
        let actual = ids json
        printfn "Actual Service.List rows: %d; expected scoped current browser rows: %d" actual.Count expected.Count
        printfn "Unexpected IDs: %A" (Set.difference actual expected)
        if actual <> expected then failwith "RED: existing producer is unfiltered")

[<EntryPoint>]
let main args =
    match args with
    | [| "--red" |] -> red ()
    | [||] -> ContractProgram.contract ()
    | _ -> invalidArg "args" "Use --red for the intentional baseline regression."
    0
