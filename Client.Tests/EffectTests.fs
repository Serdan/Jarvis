module EffectTests

open Client
open Client.Effect
open NUnit.Framework
open FsUnitTyped

[<Test>]
let ``return should wrap a value in IO context`` () =
    let result = Effect.``return`` 42 ()
    result |> shouldEqual (Ok 42)

[<Test>]
let ``lift should apply a pure function in IO context`` () =
    let double x = x * 2
    let io = Effect.lift double 21
    io () |> shouldEqual (Ok 42)

[<Test>]
let ``bind should chain computations with IO context`` () =
    let computation =
        Effect.``return`` 10 |> Effect.bind (fun x -> Effect.``return`` (x + 5))

    computation () |> shouldEqual (Ok 15)

[<Test>]
let ``map should transform a value in IO context`` () =
    let computation = Effect.``return`` 10 |> Effect.map (fun x -> x * 2)
    computation () |> shouldEqual (Ok 20)

[<Test>]
let ``bind should propagate errors`` () =
    let failingComputation =
        fun _ _ -> "Something went wrong" |> EffectError.ValidationError |> Error

    let computation = Effect.``return`` 42 |> Effect.bind failingComputation

    computation ()
    |> shouldEqual ("Something went wrong" |> EffectError.ValidationError |> Error)

[<Test>]
let ``concat should merge two IO computations producing lists`` () =
    let computation =
        Effect.concat (Effect.``return`` [ 1; 2; 3 ]) (Effect.``return`` [ 4; 5; 6 ])

    computation () |> shouldEqual (Ok [ 1; 2; 3; 4; 5; 6 ])

[<Test>]
let ``concat should propagate errors`` () =
    let computation =
        Effect.concat (Effect.``return`` [ 1; 2 ]) (fun _ -> "Failed" |> EffectError.ContextError |> Error)

    computation () |> shouldEqual ("Failed" |> EffectError.ContextError |> Error)

[<Test>]
let ``EffectBuilder should support computation expressions`` () =
    let computation =
        effect {
            let! x = Effect.``return`` 5
            let! y = Effect.``return`` 10
            return x + y
        }

    computation () |> shouldEqual (Ok 15)

[<Test>]
let ``EffectBuilder should propagate errors`` () =
    let computation =
        effect {
            let! x = Effect.``return`` 5
            let! y = fun _ -> "Computation failed" |> EffectError.ContextError |> Error
            return x + y
        }

    computation ()
    |> shouldEqual ("Computation failed" |> EffectError.ContextError |> Error)
