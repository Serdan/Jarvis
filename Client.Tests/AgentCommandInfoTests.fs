module AgentCommandInfoTests

open Common
open FsUnitTyped
open NUnit.Framework

[<Test>]
let ``project-scoped commands include project in activity description`` () =
    let command =
        RunCommandCommand
            { ProjectName = "Wayfold"
              Executable = "dotnet"
              Args = [ "test" ]
              WorkingDirectory = None
              TimeoutSeconds = None
              MaxOutputBytes = None }

    AgentCommandInfo.name command |> shouldEqual "RunCommandCommand"
    AgentCommandInfo.projectName command |> shouldEqual (Some "Wayfold")
    AgentCommandInfo.describe command |> shouldEqual "RunCommandCommand project=Wayfold"

[<Test>]
let ``global commands omit project from activity description`` () =
    let command = ListProjectsCommand

    AgentCommandInfo.projectName command |> shouldEqual None
    AgentCommandInfo.describe command |> shouldEqual "ListProjectsCommand"

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

    AgentCommandInfo.describe scoped |> shouldEqual "ListJobsCommand project=Jarvis"
    AgentCommandInfo.describe globalCommand |> shouldEqual "ListJobsCommand"
