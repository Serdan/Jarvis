module PermissionPolicyTests

open Client
open Client.PermissionPolicy
open Common
open NUnit.Framework
open FsUnitTyped

[<SetUp>]
let setup () = clearGrants ()

[<Test>]
let ``read only commands are allowed`` () =
    let command = ReadFileCommand { ProjectName = "Project1"; FilePath = "readme.md" }
    evaluate command |> shouldEqual (Ok())

[<Test>]
let ``mutating commands require confirmation`` () =
    let command =
        WriteFileCommand
            { ProjectName = "Project1"
              FilePath = "readme.md"
              Content = "updated"
              FileWriteMode = FileWriteMode.Write
              ExpectedHash = None }

    match evaluate command with
    | Error(Client.ConfirmationRequired request) ->
        request.CommandName |> shouldEqual "WriteFile"
        request.Permissions |> shouldEqual [ WorkspaceWrite ]
        request.Paths |> shouldEqual [ "readme.md" ]
    | other -> Assert.Fail($"Expected ConfirmationRequired, got {other}")

[<Test>]
let ``grant allows exact command payload`` () =
    let command =
        PatchFileCommand
            { ProjectName = "Project1"
              FilePath = "readme.md"
              ExpectedHash = None
              Format = PatchFormat.UnifiedDiff
              Patch = "patch" }

    let request =
        match evaluate command with
        | Error(Client.ConfirmationRequired request) -> request
        | other -> failwith $"Expected ConfirmationRequired, got {other}"

    grant command request None |> ignore
    evaluate command |> shouldEqual (Ok())

[<Test>]
let ``grant does not allow changed command payload`` () =
    let original =
        RunCommandCommand
            { ProjectName = "Project1"
              Executable = "dotnet"
              Args = [ "test" ]
              WorkingDirectory = None
              TimeoutSeconds = Some 60
              MaxOutputBytes = Some 4096 }

    let changed =
        RunCommandCommand
            { ProjectName = "Project1"
              Executable = "dotnet"
              Args = [ "build" ]
              WorkingDirectory = None
              TimeoutSeconds = Some 60
              MaxOutputBytes = Some 4096 }

    let request =
        match evaluate original with
        | Error(Client.ConfirmationRequired request) -> request
        | other -> failwith $"Expected ConfirmationRequired, got {other}"

    grant original request None |> ignore
    evaluate original |> shouldEqual (Ok())

    match evaluate changed with
    | Error(Client.ConfirmationRequired request) -> request.Args |> shouldEqual [ "build" ]
    | other -> Assert.Fail($"Expected ConfirmationRequired, got {other}")
