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

let private hashCommand command =
    let json = JsonSerializer.Serialize command
    let bytes = Encoding.UTF8.GetBytes json
    let hash = SHA256.HashData bytes |> Convert.ToHexString
    hash.ToLowerInvariant()

let private readOnly = Ok()

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

let clearGrants () = Store.grants.Clear()

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
    | GetGitStatusCommand _
    | GetGitDiffCommand _
    | ListJobsCommand _
    | GetJobResultCommand _ -> readOnly
    | WriteFileCommand cmd ->
        confirmation "WriteFile" (Some cmd.ProjectName) [ WorkspaceWrite ] [ cmd.FilePath ] None [] $"Write file {cmd.FilePath}" true
    | PatchFileCommand cmd ->
        confirmation "PatchFile" (Some cmd.ProjectName) [ WorkspaceWrite ] [ cmd.FilePath ] None [] $"Patch file {cmd.FilePath}" true
    | RunCommandCommand cmd ->
        confirmation "RunCommand" (Some cmd.ProjectName) [ ProcessExecution ] [] (Some cmd.Executable) cmd.Args $"Run {cmd.Executable}" true
    | GitCommitCommand cmd ->
        confirmation "GitCommit" (Some cmd.ProjectName) [ VersionControlWrite ] cmd.Paths (Some "git") [ cmd.Message ] $"Commit {cmd.Paths.Length} path(s)" true
    | StartJobCommand cmd ->
        confirmation "StartJob" (Some cmd.ProjectName) [ ProcessExecution ] [] (Some cmd.Executable) cmd.Args $"Start job {cmd.Executable}" true
    | CancelJobCommand cmd ->
        confirmation "CancelJob" None [ ProcessExecution ] [] None [ cmd.JobId ] $"Cancel job {cmd.JobId}" false

let private modeAllows mode command =
    match mode, command with
    | TrustSession, _ -> true
    | TrustExceptRunCommand, RunCommandCommand _ -> false
    | TrustExceptRunCommand, _ -> true
    | AllowWorkspaceWrite, WriteFileCommand _
    | AllowWorkspaceWrite, PatchFileCommand _ -> true
    | _ -> false

let evaluateWithMode mode command =
    if hasGrant command || modeAllows mode command then
        Ok()
    else
        requiresConfirmation command

let evaluate command = evaluateWithMode Confirm command

let authorizeWithMode mode (prompt: AgentCommand -> ConfirmationRequest -> Task<PermissionApproval>) command =
    task {
        match evaluateWithMode mode command with
        | Ok() -> return Ok()
        | Error(ConfirmationRequired request) ->
            let! approval = prompt command request

            match approval with
            | AllowOnce -> return Ok()
            | AllowExactForSession ->
                grant command request None |> ignore
                return Ok()
            | Deny -> return Error(PermissionDenied request.Summary)
        | Error error -> return Error error
    }

let authorize prompt command = authorizeWithMode Confirm prompt command
