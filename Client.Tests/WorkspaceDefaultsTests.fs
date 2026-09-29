module WorkspaceDefaultsTests

open Client
open FsUnitTyped
open NUnit.Framework

[<Test>]
let ``linux defaults to Projects when it exists`` () =
    WorkspaceDefaults.choose true "/home/alice" (fun path -> path = "/home/alice/Projects")
    |> shouldEqual "/home/alice/Projects"

[<Test>]
let ``linux falls back to home when Projects does not exist`` () =
    WorkspaceDefaults.choose true "/home/alice" (fun _ -> false)
    |> shouldEqual "/home/alice"

[<Test>]
let ``non-linux keeps interactive workspace selection`` () =
    WorkspaceDefaults.choose false "/Users/alice" (fun _ -> true)
    |> shouldEqual ""

[<Test>]
let ``missing home keeps interactive workspace selection`` () =
    WorkspaceDefaults.choose true "" (fun _ -> true)
    |> shouldEqual ""
