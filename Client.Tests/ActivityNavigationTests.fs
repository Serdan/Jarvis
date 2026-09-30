module ActivityNavigationTests

open Client
open FsUnitTyped
open NUnit.Framework

[<Test>]
let ``selection defaults to latest activity`` () =
    ActivityNavigation.normalizeSelection 5 None
    |> shouldEqual (Some 4)

[<Test>]
let ``selection movement is clamped to history bounds`` () =
    ActivityNavigation.move 5 -1 (Some 0)
    |> shouldEqual (Some 0)

    ActivityNavigation.move 5 1 (Some 4)
    |> shouldEqual (Some 4)

[<Test>]
let ``moving selection reveals older activity at top of viewport`` () =
    ActivityNavigation.scrollOffsetForSelection 20 5 0 10
    |> shouldEqual 5

[<Test>]
let ``moving selection reveals newer activity at bottom of viewport`` () =
    ActivityNavigation.scrollOffsetForSelection 20 5 10 15
    |> shouldEqual 4

[<Test>]
let ``visible selection preserves scroll position`` () =
    ActivityNavigation.scrollOffsetForSelection 20 5 5 12
    |> shouldEqual 5

[<Test>]
let ``empty history has no selection and no scroll offset`` () =
    ActivityNavigation.normalizeSelection 0 None
    |> shouldEqual None

    ActivityNavigation.scrollOffsetForSelection 0 5 9 0
    |> shouldEqual 0
