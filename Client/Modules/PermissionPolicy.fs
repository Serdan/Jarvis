module Client.PermissionPolicy

open System
open System.Collections.Concurrent
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Common
open Client

type Grant =
    { GrantId: string
      CommandHash: string
      Request: ConfirmationRequest
      CreatedAt: DateTimeOffset
      ExpiresAt: DateTimeOffset option }

module private Store =
    let grants = ConcurrentDictionary<string, Grant>()
    let executableGrants = ConcurrentDictionary<string, byte>()

let private hashCommand command =
    let authorizationIdentity =
        match command with
        | RunCommandCommand cmd ->
            RunCommandCommand { cmd with Reason = None }
        | _ -> command

    let json = JsonSerializer.Serialize authorizationIdentity
    let bytes = Encoding.UTF8.GetBytes json
    let hash = SHA256.HashData bytes |> Convert.ToHexString
    hash.ToLowerInvariant()

let private readOnly = Ok()

let private executableGrantKey command =
    let normalizeExecutable (executable: string) =
        if OperatingSystem.IsWindows() then executable.Trim().ToUpperInvariant()
        else executable.Trim()

    match command with
    | RunCommandCommand cmd ->
        Some $"RunCommand\u0000{cmd.ProjectName}\u0000{normalizeExecutable cmd.Executable}"
    | StartJobCommand cmd ->
        Some $"StartJob\u0000{cmd.ProjectName}\u0000{normalizeExecutable cmd.Executable}"
    | _ -> None

let private request commandName projectName permissions paths executable args impact supportsDryRun =
    { CommandName = commandName
      ProjectName = projectName
      Permissions = permissions
      Summary = impact
      Paths = paths
      Executable = executable
      Args = args
      EstimatedImpact = impact
      SupportsDryRun = supportsDryRun }

let private confirmation commandName projectName permissions paths executable args impact supportsDryRun =
    request commandName projectName permissions paths executable args impact supportsDryRun
    |> ConfirmationRequired
    |> Error

let grant command request expiresAt =
    let grant =
        { GrantId = Guid.NewGuid().ToString("N")
          CommandHash = hashCommand command
          Request = request
          CreatedAt = DateTimeOffset.UtcNow
          ExpiresAt = expiresAt }

    Store.grants[grant.CommandHash] <- grant
    grant

let grantExecutable command =
    match executableGrantKey command with
    | Some key ->
        Store.executableGrants[key] <- 0uy
        true
    | None -> false

let private hasExecutableGrant command =
    match executableGrantKey command with
    | Some key -> Store.executableGrants.ContainsKey key
    | None -> false

let private hasGrant command =
    let hash = hashCommand command

    match Store.grants.TryGetValue hash with
    | false, _ -> false
    | true, grant ->
        match grant.ExpiresAt with
        | Some expiresAt when expiresAt <= DateTimeOffset.UtcNow ->
            let mutable ignored = Unchecked.defaultof<Grant>
            Store.grants.TryRemove(hash, &ignored) |> ignore
            false
        | _ -> true

let clearGrants () =
    Store.grants.Clear()
    Store.executableGrants.Clear()

let private requiresConfirmation command =
    match command with
    | ListCommandsCommand
    | ListProjectsCommand
    | GetProjectDetailsCommand _
    | ListDirectoryCommand _
    | SearchFilesCommand _
    | SearchTextCommand _
    | ReadFileCommand _
    | ReadFilesCommand _
    | ReadImageCommand _
    | ListProjectTasksCommand _
    | ListSkillsCommand _
    | GetSkillCommand _
    | GetGitStatusCommand _
    | GetGitDiffCommand _
    | ListJobsCommand _
    | GetJobResultCommand _
    | GetClientActivityCommand _ -> readOnly
    | WriteFileCommand cmd ->
        confirmation "WriteFile" (Some cmd.ProjectName) [ WorkspaceWrite ] [ cmd.FilePath ] None [] $"Write file {cmd.FilePath}" true
    | PatchFileCommand cmd ->
        confirmation "PatchFile" (Some cmd.ProjectName) [ WorkspaceWrite ] [ cmd.FilePath ] None [] $"Patch file {cmd.FilePath}" true
    | CreateSkillCommand cmd ->
        confirmation "CreateSkill" (Some cmd.ProjectName) [ WorkspaceWrite ] [ $".jarvis/skills/{cmd.SkillName}/SKILL.md" ] None [] $"Create skill {cmd.SkillName}" false
    | RunCommandCommand cmd ->
        confirmation "RunCommand" (Some cmd.ProjectName) [ ProcessExecution ] [] (Some cmd.Executable) cmd.Args $"Run {cmd.Executable}" true
    | RunProjectTaskCommand cmd ->
        confirmation "RunProjectTask" (Some cmd.ProjectName) [ ProcessExecution ] [] None [ cmd.TaskName ] $"Run project task {cmd.TaskName}" false
    | GitCommitCommand cmd ->
        confirmation "GitCommit" (Some cmd.ProjectName) [ VersionControlWrite ] cmd.Paths (Some "git") [ cmd.Message ] $"Commit {cmd.Paths.Length} path(s)" true
    | StartJobCommand cmd ->
        confirmation "StartJob" (Some cmd.ProjectName) [ ProcessExecution ] [] (Some cmd.Executable) cmd.Args $"Start job {cmd.Executable}" true
    | CancelJobCommand cmd ->
        confirmation "CancelJob" None [ ProcessExecution ] [] None [ cmd.JobId ] $"Cancel job {cmd.JobId}" false

let private trustAllows trustLevel command =
    match trustLevel, command with
    | FullTrust, _ -> true
    | PartialTrust, RunCommandCommand _
    | PartialTrust, RunProjectTaskCommand _
    | PartialTrust, StartJobCommand _
    | PartialTrust, CancelJobCommand _ -> false
    | PartialTrust, _ -> true
    | NoTrust, _ -> false

let evaluateWithTrust trustLevel command =
    if hasGrant command || hasExecutableGrant command || trustAllows trustLevel command then
        Ok()
    else
        requiresConfirmation command

let evaluate command = evaluateWithTrust NoTrust command

let authorizeWithTrust trustLevel (prompt: AgentCommand -> ConfirmationRequest -> Task<PermissionApproval>) command =
    task {
        match evaluateWithTrust trustLevel command with
        | Ok() -> return Ok()
        | Error(ConfirmationRequired request) ->
            let! approval = prompt command request

            match approval with
            | AllowOnce -> return Ok()
            | AllowExactForSession ->
                grant command request None |> ignore
                return Ok()
            | AllowExecutableForSession ->
                if grantExecutable command then
                    return Ok()
                else
                    return Error(PermissionDenied "Executable-scoped approval only applies to RunCommand and StartJob.")
            | Deny -> return Error(PermissionDenied request.Summary)
        | Error error -> return Error error
    }

let authorize prompt command = authorizeWithTrust NoTrust prompt command
