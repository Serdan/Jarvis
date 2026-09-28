module ActivityPresentationTests

open Client
open Client.ActivityPresentation
open Common
open FsUnitTyped
open NUnit.Framework

let private runCommand =
    RunCommandCommand
        { ProjectName = "Jarvis"
          Executable = "dotnet"
          Args = [ "test" ]
          WorkingDirectory = None
          TimeoutSeconds = None
          MaxOutputBytes = None }

[<Test>]
let ``run command result shows exit code`` () =
    tryResultSummary runCommand """{"ExitCode":0,"TimedOut":false,"StdOut":"","StdErr":"","Truncated":false}"""
    |> shouldEqual (Some "exit 0")

[<Test>]
let ``timed out run command result shows timeout and exit code`` () =
    tryResultSummary runCommand """{"ExitCode":124,"TimedOut":true,"StdOut":"","StdErr":"","Truncated":false}"""
    |> shouldEqual (Some "timeout, exit 124")

[<Test>]
let ``git commit result abbreviates commit hash`` () =
    let command =
        GitCommitCommand
            { ProjectName = "Jarvis"
              Message = "Test"
              Body = None
              Paths = [ "Client" ]
              AllowEmpty = false }

    tryResultSummary command """{"CommitHash":"381c2f93cfcedcdc9b2d24acbf4aabf5773b0800","Summary":"","StdOut":"","StdErr":""}"""
    |> shouldEqual (Some "commit 381c2f93")

[<Test>]
let ``search result reports match count`` () =
    let command =
        SearchTextCommand
            { ProjectName = "Jarvis"
              Query = "SignalR"
              FolderPath = None
              IncludeGlobs = []
              ExcludeGlobs = []
              MaxResults = None }

    tryResultSummary command """[{"FilePath":"a.fs"},{"FilePath":"b.fs"}]"""
    |> shouldEqual (Some "2 matches")

[<Test>]
let ``patch result reports hunks and changed lines`` () =
    let command =
        PatchFileCommand
            { ProjectName = "Jarvis"
              FilePath = "a.fs"
              ExpectedHash = None
              Format = UnifiedDiff
              Patch = ""
              DryRun = None
              FuzzyContextLines = None
              ReturnContent = None }

    tryResultSummary command """{"HunksApplied":2,"ChangedLines":7}"""
    |> shouldEqual (Some "2 hunks, 7 lines")

[<Test>]
let ``malformed result payload does not affect presentation`` () =
    tryResultSummary runCommand "not json"
    |> shouldEqual None

[<Test>]
let ``short errors keep category and compact message`` () =
    let error: EffectError = Client.PermissionDenied "Run dotnet"
    shortError error |> shouldEqual "PermissionDenied: Run dotnet"
