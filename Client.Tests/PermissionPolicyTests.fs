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
    let command = ReadFileCommand { ProjectName = "Project1"; FilePath = "readme.md"; StartLine = None; EndLine = None; IncludeLineNumbers = None }
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
              ExpectedHash = None
              CreateParents = None }

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
              Reason = None
              WorkingDirectory = None
              TimeoutSeconds = Some 60
              MaxOutputBytes = Some 4096 }

    let changed =
        RunCommandCommand
            { ProjectName = "Project1"
              Executable = "dotnet"
              Args = [ "build" ]
              Reason = None
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
let ``exact run command grant ignores presentation reason`` () =
    let original =
        RunCommandCommand
            { ProjectName = "Project1"
              Executable = "dotnet"
              Args = [ "test" ]
              Reason = Some "verify the feature"
              WorkingDirectory = None
              TimeoutSeconds = Some 60
              MaxOutputBytes = Some 4096 }

    let sameCommandDifferentReason =
        RunCommandCommand
            { ProjectName = "Project1"
              Executable = "dotnet"
              Args = [ "test" ]
              Reason = Some "run the tests"
              WorkingDirectory = None
              TimeoutSeconds = Some 60
              MaxOutputBytes = Some 4096 }

    let request =
        match evaluate original with
        | Error(Client.ConfirmationRequired request) -> request
        | other -> failwith $"Expected ConfirmationRequired, got {other}"

    grant original request None |> ignore
    evaluate sameCommandDifferentReason |> shouldEqual (Ok())


[<Test>]
let ``partial trust allows workspace mutations`` () =
    let writeCommand =
        WriteFileCommand
            { ProjectName = "Project1"
              FilePath = "readme.md"
              Content = "updated"
              FileWriteMode = FileWriteMode.Write
              ExpectedHash = None
              CreateParents = None }

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

    evaluateWithTrust PartialTrust writeCommand |> shouldEqual (Ok())
    evaluateWithTrust PartialTrust patchCommand |> shouldEqual (Ok())

[<Test>]
let ``partial trust confirms run command`` () =
    let command =
        RunCommandCommand
            { ProjectName = "Project1"
              Executable = "dotnet"
              Args = [ "test" ]
              Reason = None
              WorkingDirectory = None
              TimeoutSeconds = Some 60
              MaxOutputBytes = Some 4096 }

    match evaluateWithTrust PartialTrust command with
    | Error(Client.ConfirmationRequired request) ->
        request.CommandName |> shouldEqual "RunCommand"
        request.Permissions |> shouldEqual [ ProcessExecution ]
    | other -> Assert.Fail($"Expected ConfirmationRequired, got {other}")

[<Test>]
let ``full trust allows structured mutation`` () =
    let command =
        GitCommitCommand
            { ProjectName = "Project1"
              Message = "Test commit"
              Body = None
              Paths = [ "readme.md" ]
              AllowEmpty = false }

    evaluateWithTrust FullTrust command |> shouldEqual (Ok())


[<Test>]
let ``partial trust confirms run command consistently`` () =
    let command =
        RunCommandCommand
            { ProjectName = "Project1"
              Executable = "dotnet"
              Args = [ "test" ]
              Reason = None
              WorkingDirectory = None
              TimeoutSeconds = Some 60
              MaxOutputBytes = Some 4096 }

    match evaluateWithTrust PartialTrust command with
    | Error(Client.ConfirmationRequired request) ->
        request.CommandName |> shouldEqual "RunCommand"
        request.Permissions |> shouldEqual [ ProcessExecution ]
    | other -> Assert.Fail($"Expected ConfirmationRequired, got {other}")

[<Test>]
let ``partial trust allows non-process mutating commands`` () =
    let writeCommand =
        WriteFileCommand
            { ProjectName = "Project1"
              FilePath = "readme.md"
              Content = "updated"
              FileWriteMode = FileWriteMode.Write
              ExpectedHash = None
              CreateParents = None }

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

    evaluateWithTrust PartialTrust writeCommand |> shouldEqual (Ok())
    evaluateWithTrust PartialTrust patchCommand |> shouldEqual (Ok())
    evaluateWithTrust PartialTrust commitCommand |> shouldEqual (Ok())

[<Test>]
let ``partial trust confirms start job`` () =
    let startJobCommand =
        StartJobCommand
            { ProjectName = "Project1"
              Executable = "dotnet"
              Args = [ "watch" ]
              Reason = None
              WorkingDirectory = None
              MaxOutputBytes = Some 4096 }

    match evaluateWithTrust PartialTrust startJobCommand with
    | Error(Client.ConfirmationRequired request) ->
        request.CommandName |> shouldEqual "StartJob"
        request.Permissions |> shouldEqual [ ProcessExecution ]
    | other -> Assert.Fail($"Expected ConfirmationRequired, got {other}")


[<Test>]
let ``partial trust confirms cancel job`` () =
    let command = CancelJobCommand { JobId = "job-1" }

    match evaluateWithTrust PartialTrust command with
    | Error(Client.ConfirmationRequired request) ->
        request.CommandName |> shouldEqual "CancelJob"
        request.Permissions |> shouldEqual [ ProcessExecution ]
    | other -> Assert.Fail($"Expected ConfirmationRequired, got {other}")

[<Test>]
let ``trust level parse accepts public values and defaults to partial`` () =
    TrustLevel.parse null |> shouldEqual (Ok PartialTrust)
    TrustLevel.parse "none" |> shouldEqual (Ok NoTrust)
    TrustLevel.parse "partial" |> shouldEqual (Ok PartialTrust)
    TrustLevel.parse "full" |> shouldEqual (Ok FullTrust)

[<Test>]
let ``trust level parse rejects legacy and unknown values`` () =
    for value in [ ""; "confirm"; "workspace-write"; "trust-except-run-command"; "trust-session"; "YOLO" ] do
        match TrustLevel.parse value with
        | Error message -> message.Contains("Unknown trust level") |> shouldEqual true
        | other -> Assert.Fail($"Expected Error for {value}, got {other}")

[<Test>]
let ``trust display names match CLI values`` () =
    TrustLevel.toDisplayName NoTrust |> shouldEqual "none"
    TrustLevel.toDisplayName PartialTrust |> shouldEqual "partial"
    TrustLevel.toDisplayName FullTrust |> shouldEqual "full"

[<Test>]
let ``authorizeWithTrust allow once does not create grant`` () =
    task {
        let command =
            RunCommandCommand
                { ProjectName = "Project1"
                  Executable = "dotnet"
                  Args = [ "test" ]
                  Reason = None
                  WorkingDirectory = None
                  TimeoutSeconds = Some 60
                  MaxOutputBytes = Some 4096 }

        let mutable prompts = 0
        let prompt _ _ =
            task {
                prompts <- prompts + 1
                return AllowOnce
            }

        let! first = authorizeWithTrust NoTrust prompt command
        let! second = authorizeWithTrust NoTrust prompt command

        first |> shouldEqual (Ok())
        second |> shouldEqual (Ok())
        prompts |> shouldEqual 2
    }

[<Test>]
let ``authorizeWithTrust allow exact for session creates grant`` () =
    task {
        let command =
            RunCommandCommand
                { ProjectName = "Project1"
                  Executable = "dotnet"
                  Args = [ "test" ]
                  Reason = None
                  WorkingDirectory = None
                  TimeoutSeconds = Some 60
                  MaxOutputBytes = Some 4096 }

        let mutable prompts = 0
        let prompt _ _ =
            task {
                prompts <- prompts + 1
                return AllowExactForSession
            }

        let! first = authorizeWithTrust NoTrust prompt command
        let! second = authorizeWithTrust NoTrust prompt command

        first |> shouldEqual (Ok())
        second |> shouldEqual (Ok())
        prompts |> shouldEqual 1
    }

[<Test>]
let ``authorize executable for session allows changed args in same project`` () =
    task {
        clearGrants()
        let first =
            RunCommandCommand
                { ProjectName = "Project1"
                  Executable = "dotnet"
                  Args = [ "test" ]
                  Reason = None
                  WorkingDirectory = None
                  TimeoutSeconds = Some 60
                  MaxOutputBytes = Some 4096 }

        let second =
            RunCommandCommand
                { ProjectName = "Project1"
                  Executable = "dotnet"
                  Args = [ "build" ]
                  Reason = None
                  WorkingDirectory = None
                  TimeoutSeconds = Some 60
                  MaxOutputBytes = Some 4096 }

        let prompt _ _ = task { return AllowExecutableForSession }
        let! approved = authorizeWithTrust NoTrust prompt first

        approved |> shouldEqual (Ok())
        evaluate second |> shouldEqual (Ok())
    }

[<Test>]
let ``executable session grant is scoped by project and command kind`` () =
    clearGrants()
    let run =
        RunCommandCommand
            { ProjectName = "Project1"
              Executable = "dotnet"
              Args = [ "test" ]
              Reason = None
              WorkingDirectory = None
              TimeoutSeconds = Some 60
              MaxOutputBytes = Some 4096 }

    grantExecutable run |> shouldEqual true

    let otherProject =
        RunCommandCommand
            { ProjectName = "Project2"
              Executable = "dotnet"
              Args = [ "test" ]
              Reason = None
              WorkingDirectory = None
              TimeoutSeconds = Some 60
              MaxOutputBytes = Some 4096 }

    let startJob =
        StartJobCommand
            { ProjectName = "Project1"
              Executable = "dotnet"
              Args = [ "watch" ]
              Reason = None
              WorkingDirectory = None
              MaxOutputBytes = Some 4096 }

    match evaluate otherProject with
    | Error(Client.ConfirmationRequired _) -> ()
    | other -> Assert.Fail($"Expected project-scoped confirmation, got {other}")

    match evaluate startJob with
    | Error(Client.ConfirmationRequired _) -> ()
    | other -> Assert.Fail($"Expected command-kind-scoped confirmation, got {other}")

[<Test>]
let ``authorizeWithTrust deny returns permission denied`` () =
    task {
        let command =
            GitCommitCommand
                { ProjectName = "Project1"
                  Message = "Test commit"
                  Body = None
                  Paths = [ "readme.md" ]
                  AllowEmpty = false }

        let prompt _ _ = task { return (Client.PermissionApproval.Deny) }
        let! result = authorizeWithTrust NoTrust prompt command

        match result with
        | Error(Client.PermissionDenied message) -> message.Contains("Commit") |> shouldEqual true
        | other -> Assert.Fail($"Expected PermissionDenied, got {other}")
    }

[<Test>]
let ``full trust allows run command`` () =
    let command =
        RunCommandCommand
            { ProjectName = "Project1"
              Executable = "dotnet"
              Args = [ "test" ]
              Reason = None
              WorkingDirectory = None
              TimeoutSeconds = Some 60
              MaxOutputBytes = Some 4096 }

    evaluateWithTrust FullTrust command |> shouldEqual (Ok())

[<Test>]
let ``project task listing is read only`` () =
    let command = ListProjectTasksCommand { ProjectName = "Project1" }
    evaluate command |> shouldEqual (Ok())

[<Test>]
let ``partial trust confirms project task execution`` () =
    let command =
        RunProjectTaskCommand
            { ProjectName = "Project1"
              TaskName = "build" }

    match evaluateWithTrust PartialTrust command with
    | Error(Client.ConfirmationRequired request) ->
        request.CommandName |> shouldEqual "RunProjectTask"
        request.Permissions |> shouldEqual [ ProcessExecution ]
    | other -> Assert.Fail($"Expected ConfirmationRequired, got {other}")

[<Test>]
let create_skill_requires_workspace_write_confirmation () =
    let command =
        CreateSkillCommand
            { ProjectName = "Project1"
              SkillName = "release"
              Content = "# Release"
              Overwrite = false }

    match evaluate command with
    | Error(Client.ConfirmationRequired request) ->
        request.CommandName |> shouldEqual "CreateSkill"
        request.Permissions |> shouldEqual [ WorkspaceWrite ]
        request.Paths |> shouldEqual [ ".jarvis/skills/release/SKILL.md" ]
    | other -> Assert.Fail($"Expected ConfirmationRequired, got {other}")

[<Test>]
let partial_trust_allows_create_skill () =
    CreateSkillCommand
        { ProjectName = "Project1"
          SkillName = "release"
          Content = "# Release"
          Overwrite = false }
    |> evaluateWithTrust PartialTrust
    |> shouldEqual (Ok())

[<Test>]
let ``start job exact grant ignores presentation reason`` () =
    let original =
        StartJobCommand
            { ProjectName = "Project1"
              Executable = "dotnet"
              Args = [ "watch" ]
              Reason = Some "Watch tests"
              WorkingDirectory = None
              MaxOutputBytes = Some 4096 }

    let sameJobDifferentReason =
        StartJobCommand
            { ProjectName = "Project1"
              Executable = "dotnet"
              Args = [ "watch" ]
              Reason = Some "Keep the test watcher running"
              WorkingDirectory = None
              MaxOutputBytes = Some 4096 }

    let request =
        match evaluate original with
        | Error(Client.ConfirmationRequired request) -> request
        | other -> failwith $"Expected ConfirmationRequired, got {other}"

    grant original request None |> ignore
    evaluate sameJobDifferentReason |> shouldEqual (Ok())
