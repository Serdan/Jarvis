module ReconnectScheduleTests

open System
open Client.SignalR
open NUnit.Framework
open FsUnitTyped

[<Test>]
let ``reconnect schedule backs off and caps at thirty seconds`` () =
    [ 0, TimeSpan.Zero
      1, TimeSpan.FromSeconds 2.0
      2, TimeSpan.FromSeconds 5.0
      3, TimeSpan.FromSeconds 10.0
      4, TimeSpan.FromSeconds 30.0
      25, TimeSpan.FromSeconds 30.0 ]
    |> List.iter (fun (failures, expected) ->
        ReconnectSchedule.delayForFailureCount failures |> shouldEqual expected)

[<Test>]
let ``negative failure count is treated as initial attempt`` () =
    ReconnectSchedule.delayForFailureCount -1 |> shouldEqual TimeSpan.Zero
