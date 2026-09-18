module PermissionPolicyTests

open Client
open Client.Effect
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
let ``effect errors map to protocol errors`` () =
    EffectError.toAgentError (Client.ValidationError "bad input")
    |> shouldEqual (AgentError.ValidationFailed "bad input")

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
              Patch = "patch"
              DryRun = None
              FuzzyContextLines = None
              ReturnContent = None }

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


[<Test>]
let ``workspace-write mode allows write and patch commands`` () =
    let writeCommand =
        WriteFileCommand
            { ProjectName = "Project1"
              FilePath = "readme.md"
              Content = "updated"
              FileWriteMode = FileWriteMode.Write
              ExpectedHash = None }

    let patchCommand =
        PatchFileCommand
            { ProjectName = "Project1"
              FilePath = "readme.md"
              ExpectedHash = None
              Format = PatchFormat.UnifiedDiff
              Patch = "patch"
              DryRun = None
              FuzzyContextLines = None
              ReturnContent = None }

    evaluateWithMode AllowWorkspaceWrite writeCommand |> shouldEqual (Ok())
    evaluateWithMode AllowWorkspaceWrite patchCommand |> shouldEqual (Ok())

[<Test>]
let ``workspace-write mode still confirms process execution`` () =
    let command =
        RunCommandCommand
            { ProjectName = "Project1"
              Executable = "dotnet"
              Args = [ "test" ]
              WorkingDirectory = None
              TimeoutSeconds = Some 60
              MaxOutputBytes = Some 4096 }

    match evaluateWithMode AllowWorkspaceWrite command with
    | Error(Client.ConfirmationRequired request) ->
        request.CommandName |> shouldEqual "RunCommand"
        request.Permissions |> shouldEqual [ ProcessExecution ]
    | other -> Assert.Fail($"Expected ConfirmationRequired, got {other}")

[<Test>]
let ``trust-session mode allows confirmable commands`` () =
    let command =
        GitCommitCommand
            { ProjectName = "Project1"
              Message = "Test commit"
              Body = None
              Paths = [ "readme.md" ]
              AllowEmpty = false }

    evaluateWithMode TrustSession command |> shouldEqual (Ok())


[<Test>]
let ``trust-except-run-command mode confirms run command`` () =
    let command =
        RunCommandCommand
            { ProjectName = "Project1"
              Executable = "dotnet"
              Args = [ "test" ]
              WorkingDirectory = None
              TimeoutSeconds = Some 60
              MaxOutputBytes = Some 4096 }

    match evaluateWithMode TrustExceptRunCommand command with
    | Error(Client.ConfirmationRequired request) ->
        request.CommandName |> shouldEqual "RunCommand"
        request.Permissions |> shouldEqual [ ProcessExecution ]
    | other -> Assert.Fail($"Expected ConfirmationRequired, got {other}")

[<Test>]
let ``trust-except-run-command mode allows non-process mutating commands`` () =
    let writeCommand =
        WriteFileCommand
            { ProjectName = "Project1"
              FilePath = "readme.md"
              Content = "updated"
              FileWriteMode = FileWriteMode.Write
              ExpectedHash = None }

    let patchCommand =
        PatchFileCommand
            { ProjectName = "Project1"
              FilePath = "readme.md"
              ExpectedHash = None
              Format = PatchFormat.UnifiedDiff
              Patch = "patch"
              DryRun = None
              FuzzyContextLines = None
              ReturnContent = None }

    let commitCommand =
        GitCommitCommand
            { ProjectName = "Project1"
              Message = "Test commit"
              Body = None
              Paths = [ "readme.md" ]
              AllowEmpty = false }

    let cancelJobCommand = CancelJobCommand { JobId = "job-1" }

    evaluateWithMode TrustExceptRunCommand writeCommand |> shouldEqual (Ok())
    evaluateWithMode TrustExceptRunCommand patchCommand |> shouldEqual (Ok())
    evaluateWithMode TrustExceptRunCommand commitCommand |> shouldEqual (Ok())
    evaluateWithMode TrustExceptRunCommand cancelJobCommand |> shouldEqual (Ok())

[<Test>]
let ``trust-except-run-command mode confirms start job`` () =
    let startJobCommand =
        StartJobCommand
            { ProjectName = "Project1"
              Executable = "dotnet"
              Args = [ "watch" ]
              WorkingDirectory = None
              MaxOutputBytes = Some 4096 }

    match evaluateWithMode TrustExceptRunCommand startJobCommand with
    | Error(Client.ConfirmationRequired request) ->
        request.CommandName |> shouldEqual "StartJob"
        request.Permissions |> shouldEqual [ ProcessExecution ]
    | other -> Assert.Fail($"Expected ConfirmationRequired, got {other}")


[<Test>]
let ``permission mode parse accepts aliases`` () =
    PermissionMode.parse null |> shouldEqual (Ok Confirm)
    PermissionMode.parse "" |> shouldEqual (Ok Confirm)
    PermissionMode.parse "default" |> shouldEqual (Ok Confirm)
    PermissionMode.parse "workspace-write" |> shouldEqual (Ok AllowWorkspaceWrite)
    PermissionMode.parse "write" |> shouldEqual (Ok AllowWorkspaceWrite)
    PermissionMode.parse "trust-except-run-command" |> shouldEqual (Ok TrustExceptRunCommand)
    PermissionMode.parse "trust-no-run" |> shouldEqual (Ok TrustExceptRunCommand)
    PermissionMode.parse "trust-session" |> shouldEqual (Ok TrustSession)
    PermissionMode.parse "trusted" |> shouldEqual (Ok TrustSession)

[<Test>]
let ``permission mode parse rejects unknown values`` () =
    match PermissionMode.parse "YOLO" with
    | Error message -> message.Contains("Unknown permission mode") |> shouldEqual true
    | other -> Assert.Fail($"Expected Error, got {other}")

[<Test>]
let ``authorizeWithMode allow once does not create grant`` () =
    task {
        let command =
            RunCommandCommand
                { ProjectName = "Project1"
                  Executable = "dotnet"
                  Args = [ "test" ]
                  WorkingDirectory = None
                  TimeoutSeconds = Some 60
                  MaxOutputBytes = Some 4096 }

        let mutable prompts = 0
        let prompt _ _ =
            task {
                prompts <- prompts + 1
                return AllowOnce
            }

        let! first = authorizeWithMode Confirm prompt command
        let! second = authorizeWithMode Confirm prompt command

        first |> shouldEqual (Ok())
        second |> shouldEqual (Ok())
        prompts |> shouldEqual 2
    }

[<Test>]
let ``authorizeWithMode allow exact for session creates grant`` () =
    task {
        let command =
            RunCommandCommand
                { ProjectName = "Project1"
                  Executable = "dotnet"
                  Args = [ "test" ]
                  WorkingDirectory = None
                  TimeoutSeconds = Some 60
                  MaxOutputBytes = Some 4096 }

        let mutable prompts = 0
        let prompt _ _ =
            task {
                prompts <- prompts + 1
                return AllowExactForSession
            }

        let! first = authorizeWithMode Confirm prompt command
        let! second = authorizeWithMode Confirm prompt command

        first |> shouldEqual (Ok())
        second |> shouldEqual (Ok())
        prompts |> shouldEqual 1
    }

[<Test>]
let ``authorizeWithMode deny returns permission denied`` () =
    task {
        let command =
            GitCommitCommand
                { ProjectName = "Project1"
                  Message = "Test commit"
                  Body = None
                  Paths = [ "readme.md" ]
                  AllowEmpty = false }

        let prompt _ _ = task { return (Client.PermissionApproval.Deny) }
        let! result = authorizeWithMode Confirm prompt command

        match result with
        | Error(Client.PermissionDenied message) -> message.Contains("Commit") |> shouldEqual true
        | other -> Assert.Fail($"Expected PermissionDenied, got {other}")
    }

[<Test>]
let ``trust-session mode allows run command`` () =
    let command =
        RunCommandCommand
            { ProjectName = "Project1"
              Executable = "dotnet"
              Args = [ "test" ]
              WorkingDirectory = None
              TimeoutSeconds = Some 60
              MaxOutputBytes = Some 4096 }

    evaluateWithMode TrustSession command |> shouldEqual (Ok())
