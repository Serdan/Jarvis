module ActivityExportTests

open Client
open FsUnitTyped
open NUnit.Framework

[<Test>]
let ``recent command export is concise and chronological`` () =
    let first: ActivityExport.Entry =
        { Timestamp = "18:31:47"
          ProjectName = Some "Wayfold"
          CommandName = "SearchText"
          Reason = None
          Detail = Some "\"CreatureRenderer\""
          Status = "Completed"
          DurationMs = Some 7L
          Result = Some "2 matches" }

    let second: ActivityExport.Entry =
        { Timestamp = "18:32:01"
          ProjectName = Some "Jarvis"
          CommandName = "RunCommand"
          Reason = Some "Verify tests"
          Detail = Some "dotnet test"
          Status = "Completed"
          DurationMs = Some 812L
          Result = Some "exit 0" }

    let entries =
        [ first; second ]

    ActivityExport.formatRecent entries
    |> shouldEqual
        "Jarvis recent commands (2, oldest to newest)\n[18:31:47] @Wayfold SearchText\n  Detail: \"CreatureRenderer\"\n  Status: Completed | 7 ms | 2 matches\n\n[18:32:01] @Jarvis RunCommand\n  Reason: Verify tests\n  Detail: dotnet test\n  Status: Completed | 812 ms | exit 0"
