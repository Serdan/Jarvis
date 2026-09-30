module ProjectColorAssignmentsTests

open Client
open FsUnitTyped
open NUnit.Framework

[<Test>]
let ``projects receive unique colors until palette is exhausted`` () =
    let assignments = ProjectColorAssignments.ProjectColorAssignments([| 1; 2; 3 |])

    assignments.Get("A") |> shouldEqual 1
    assignments.Get("B") |> shouldEqual 2
    assignments.Get("C") |> shouldEqual 3

[<Test>]
let ``project keeps the same color for the client lifetime`` () =
    let assignments = ProjectColorAssignments.ProjectColorAssignments([| 1; 2; 3 |])

    assignments.Get("A") |> shouldEqual 1
    assignments.Get("B") |> shouldEqual 2
    assignments.Get("A") |> shouldEqual 1

[<Test>]
let ``colors repeat only after palette is exhausted`` () =
    let assignments = ProjectColorAssignments.ProjectColorAssignments([| 1; 2 |])

    assignments.Get("A") |> shouldEqual 1
    assignments.Get("B") |> shouldEqual 2
    assignments.Get("C") |> shouldEqual 1
