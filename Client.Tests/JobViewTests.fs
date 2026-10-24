module JobViewTests

open System
open Client.JobView
open Common
open NUnit.Framework
open FsUnitTyped

let summary id project status started =
    { JobId = id
      ProjectName = project
      Executable = "dotnet"
      Args = []
      Reason = None
      WorkingDirectory = None
      Status = status
      StartedAt = started
      CompletedAt = None }

[<Test>]
let ``jobs sort active first then newest and filter projects`` () =
    let start = DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero)
    let items = [
        summary "old" "Wayfold" JobStatus.Running start
        summary "done" "Jarvis" (JobStatus.Completed 0) (start.AddMinutes 10)
        summary "new" "Wayfold" JobStatus.Running (start.AddMinutes 5) ]
    (visibleJobs true "" items |> List.map _.JobId) |> shouldEqual [ "new"; "old"; "done" ]
    (visibleJobs false "" items |> List.map _.JobId) |> shouldEqual [ "new"; "old" ]
    (visibleJobs true "jar" items |> List.map _.JobId) |> shouldEqual [ "done" ]

[<Test>]
let ``output lines are ordered bounded and sanitized`` () =
    let events = [
        { Sequence = 1L; Stream = JobOutputStream.StdOut; Text = "hello\nworld\n" }
        { Sequence = 2L; Stream = JobOutputStream.StdErr; Text = "\u001b[31merror\u0007\n" } ]
    let first = appendOutput 2 [] events
    (first |> List.map _.Text) |> shouldEqual [ "world"; " [31merror " ] 
    (first |> List.map _.Sequence) |> shouldEqual [ 1L; 2L ]
    let next = appendOutput 2 first [ { Sequence = 3L; Stream = JobOutputStream.StdOut; Text = "later\n" } ]
    (next |> List.map _.Text) |> shouldEqual [ " [31merror "; "later" ]
    (safeLine "x\r\n\u001b") |> shouldEqual "x   "

[<Test>]
let ``duration uses completion time rather than increasing forever`` () =
    let start = DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero)
    let job = { summary "x" "Jarvis" (JobStatus.Completed 7) start with CompletedAt = Some(start.AddSeconds 83) }
    duration (start.AddHours 4) job |> shouldEqual "1m 23s"
