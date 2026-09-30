module AgentCommandInfoTests

open Common
open FsUnitTyped
open NUnit.Framework

[<Test>]
let ``project-scoped commands render compact activity labels`` () =
    let command =
        RunCommandCommand
            { ProjectName = "Wayfold"
              Executable = "dotnet"
              Args = [ "test" ]
              Reason = None
              WorkingDirectory = None
              TimeoutSeconds = None
              MaxOutputBytes = None }

    AgentCommandInfo.name command |> shouldEqual "RunCommandCommand"
    AgentCommandInfo.displayName command |> shouldEqual "RunCommand"
    AgentCommandInfo.projectName command |> shouldEqual (Some "Wayfold")
    AgentCommandInfo.detail command |> shouldEqual (Some "dotnet test")
    AgentCommandInfo.activityLabel command |> shouldEqual "@Wayfold RunCommand(dotnet test)"

[<Test>]
let ``run command reason is the primary activity label`` () =
    let command =
        RunCommandCommand
            { ProjectName = "Jarvis"
              Executable = "dotnet"
              Args = [ "test" ]
              Reason = Some "verify client activity changes"
              WorkingDirectory = None
              TimeoutSeconds = None
              MaxOutputBytes = None }

    AgentCommandInfo.reason command |> shouldEqual (Some "verify client activity changes")
    AgentCommandInfo.activityLabel command
    |> shouldEqual "@Jarvis verify client activity changes · dotnet test"

[<Test>]
let ``file commands show the target path`` () =
    let command =
        ReadFileCommand
            { ProjectName = "Jarvis"
              FilePath = "docs/AI.md"
              StartLine = None
              EndLine = None
              IncludeLineNumbers = None }

    AgentCommandInfo.activityLabel command |> shouldEqual "@Jarvis ReadFile(docs/AI.md)"

[<Test>]
let ``global commands omit project from activity description`` () =
    let command = ListProjectsCommand

    AgentCommandInfo.projectName command |> shouldEqual None
    AgentCommandInfo.activityLabel command |> shouldEqual "ListProjects"

[<Test>]
let ``list jobs includes optional project when supplied`` () =
    let scoped =
        ListJobsCommand
            { ProjectName = Some "Jarvis"
              IncludeCompleted = false }

    let globalCommand =
        ListJobsCommand
            { ProjectName = None
              IncludeCompleted = true }

    AgentCommandInfo.activityLabel scoped |> shouldEqual "@Jarvis ListJobs"
    AgentCommandInfo.activityLabel globalCommand |> shouldEqual "ListJobs"
