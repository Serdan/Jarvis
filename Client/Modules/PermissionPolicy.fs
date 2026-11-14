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
        | StartJobCommand cmd ->
            StartJobCommand { cmd with Reason = None }
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
    | BrowserListTabsCommand
    | BrowserListWindowsCommand
    | ListScheduledBrowserExecutionsCommand
    | ListWorktreesCommand _
    | GetWorktreeCommand _
    | InspectWorktreeRemovalCommand _
    | TaskReadCommand _
    | ObserveAgentCommand _
    | LedgerReadCommand _
    | ReviewReadCommand _
    | GetProjectDetailsCommand _
    | ListDirectoryCommand _
    | SearchFilesCommand _
    | SearchTextCommand _
    | ReadFileCommand _
    | ReadFilePageCommand _
    | ReadFilesCommand _
    | GetAgentInstructionsCommand _
    | ReadImageCommand _
    | ExportFileCommand _
    | ListConfiguredTasksCommand _
    | ListSkillsCommand _
    | GetSkillCommand _
    | GetGitStatusCommand _
    | GetGitDiffCommand _
    | ListJobsCommand _
    | GetJobResultCommand _
    | GetProcessOwnershipEvidenceCommand _
    | MessageCommand _
    | GetClientActivityCommand _ -> readOnly
    | ScheduleBrowserExecutionCommand cmd ->
        confirmation "ScheduleBrowserExecution" (Some cmd.ProjectName) [ ProcessExecution; NetworkAccess ]
            [ cmd.ScriptPath ] None [] ("Schedule browser execution " + cmd.ScheduleId) false
    | CancelScheduledBrowserExecutionCommand cmd ->
        confirmation "CancelScheduledBrowserExecution" None [ ProcessExecution ] [] None [] ("Cancel scheduled browser action " + cmd.ScheduleId) false
    | BrowserOpenTabCommand cmd ->
        let origin =
            match Uri.TryCreate(cmd.Url, UriKind.Absolute) with
            | true, uri when uri.Scheme = "https" || uri.Scheme = "http" -> uri.GetLeftPart(UriPartial.Authority)
            | _ -> "(invalid URL)"
        confirmation "BrowserOpenTab" None [ ProcessExecution; NetworkAccess ] [] None [] ("Open browser tab on " + origin) false
    | BrowserCloseTabCommand cmd ->
        confirmation "BrowserCloseTab" None [ ProcessExecution ] [] None [] ("Close Jarvis-created tab " + cmd.TabRegistration) false
    | BrowserRunInNewTabCommand cmd ->
        confirmation "BrowserRunInNewTab" (Some cmd.ProjectName) [ ProcessExecution; NetworkAccess ] [cmd.ScriptPath] None []
            ("Open browser tab in " + cmd.WindowRegistration + " and execute " + cmd.ScriptPath) false
    | BrowserExecuteFileCommand cmd ->
        confirmation "BrowserExecuteFile" (Some cmd.ProjectName) [ ProcessExecution; NetworkAccess ] [ cmd.ScriptPath ] None [] ("Execute browser script in registered tab " + cmd.TabRegistration) false
    | BrowserExecuteJsCommand cmd ->
        confirmation "BrowserExecuteJs" None [ ProcessExecution; NetworkAccess ] [] None [] ("Execute JavaScript in registered tab " + cmd.TabRegistration) false
    | BrowserCaptureDomCommand cmd ->
        confirmation "BrowserCaptureDom" None [ ProcessExecution; NetworkAccess ] [] None []
            ("Capture rendered DOM in registered tab " + cmd.TabRegistration) false
    | BrowserExportDomCommand cmd ->
        confirmation "BrowserExportDom" (Some cmd.ProjectName)
            [ ProcessExecution; NetworkAccess; WorkspaceWrite ] [ cmd.FilePath ] None []
            ("Export rendered DOM from " + cmd.TabRegistration + " into " + cmd.ProjectName + "/" + cmd.FilePath) false
    | ImportFileCommand cmd ->
        // Never show a temporary signed file URL (query credentials) in
        // confirmation dialogs, audits or error messages.
        let host =
            match ImportNetwork.classifyUrl cmd.DownloadUrl with
            | Ok ImportNetwork.ProviderAttachment -> "ChatGPT file delivery"
            | Ok (ImportNetwork.OtherPublicHttps name) -> name
            | Error _ -> "unrecognized HTTPS source"
        confirmation "ImportFile" (Some cmd.ProjectName) [ WorkspaceWrite; NetworkAccess ]
            [ cmd.FilePath ] None [ host ] $"Import from {host} to {cmd.FilePath}" false
    | UpdateClientCommand ->
        confirmation "UpdateClient" None [ ProcessExecution; NetworkAccess; Destructive ] [] None [] "Update and restart Jarvis client" false
    | WriteFileCommand cmd ->
        confirmation "WriteFile" (Some cmd.ProjectName) [ WorkspaceWrite ] [ cmd.FilePath ] None [] $"Write file {cmd.FilePath}" true
    | PatchFileCommand cmd ->
        confirmation "PatchFile" (Some cmd.ProjectName) [ WorkspaceWrite ] [ cmd.FilePath ] None [] $"Patch file {cmd.FilePath}" true
    | CreateSkillCommand cmd ->
        confirmation "CreateSkill" (Some cmd.ProjectName) [ WorkspaceWrite ] [ $".agents/skills/{cmd.SkillName}/SKILL.md" ] None [] $"Create skill {cmd.SkillName}" false
    | RunCommandCommand cmd ->
        confirmation "RunCommand" (Some cmd.ProjectName) [ ProcessExecution ] [] (Some cmd.Executable) cmd.Args $"Run {cmd.Executable}" true
    | RunProjectTaskCommand cmd ->
        confirmation "RunProjectTask" (Some cmd.ProjectName) [ ProcessExecution ] [] None [ cmd.TaskName ] $"Run project task {cmd.TaskName}" false
    | RemoveWorktreeCommand cmd ->
        confirmation "RemoveWorktree" (Some cmd.ProjectName) [ VersionControlWrite; Destructive ] [] (Some "git") [ cmd.WorktreePath ] $"Remove worktree {cmd.WorktreePath}" false
    | TaskWriteCommand cmd
    | CrossProjectNotificationCommand { Notification = cmd } ->
        confirmation ("Task" + cmd.Action) (Some cmd.ProjectName)
            [ VersionControlWrite; NetworkAccess ] [] (Some "git")
            [ cmd.TaskId; cmd.AttemptId; cmd.OperationId ]
            (" ledger task " + cmd.Action) false
    | LedgerWriteCommand cmd ->
        confirmation ("Ledger" + cmd.Action) (Some cmd.ProjectName)
            [ VersionControlWrite; NetworkAccess ] [] (Some "git")
            [ cmd.RemoteName; cmd.ExpectedTrustedHead; cmd.OperationId ]
            (" ledger setup " + cmd.Action) false
    | RetryPendingIntentCommand cmd ->
        confirmation "RetryPendingIntent" (Some cmd.ProjectName)
            [ VersionControlWrite; NetworkAccess ] [] (Some "git")
            [ cmd.OperationId; cmd.ExpectedCommandSha256; cmd.ExpectedVerifiedHead ]
            "Retry exactly the saved pending operation through current canonical ledger guards" false
    | SupersedePendingIntentCommand cmd ->
        confirmation "SupersedePendingIntent" (Some cmd.ProjectName)
            [ WorkspaceWrite; VersionControlWrite; NetworkAccess ] [] (Some "git")
            [ cmd.OperationId; cmd.RelatedOperationId; cmd.ExpectedCommandSha256; cmd.Reason ]
            "Archive original pending intent after verifying a related Applied receipt" false
    | AbandonPendingIntentCommand cmd ->
        confirmation "AbandonPendingIntent" (Some cmd.ProjectName)
            [ WorkspaceWrite; VersionControlWrite; NetworkAccess ] [] (Some "git")
            [ cmd.OperationId; cmd.ExpectedCommandSha256; cmd.ExpectedVerifiedHead; cmd.Reason ]
            "Archive only after current canonical task/review state proves the original saved action obsolete" false
    | ReviewWriteCommand cmd ->
        confirmation ("Review" + cmd.Action) (Some cmd.ProjectName)
            [ VersionControlWrite; NetworkAccess ] [] (Some "git")
            [ cmd.TaskId; cmd.RequestId; cmd.OperationId ]
            (" canonical ledger review " + cmd.Action) false
    | CreateProjectCommand cmd ->
        let permissions =
            [ WorkspaceWrite ]
            @ (if cmd.InitializeGit then [ VersionControlWrite ] else [])
            @ (if cmd.InitializeLedger then [ NetworkAccess ] else [])
        confirmation "CreateProject" None permissions [ cmd.Name ]
            (if cmd.InitializeGit then Some "git" else None)
            [ cmd.Name; cmd.RemoteUrl ]
            $"Create workspace project {cmd.Name}" true
    | CreateWorktreeCommand cmd ->
        confirmation "CreateWorktree" (Some cmd.ProjectName) [ VersionControlWrite ] [] (Some "git") [ cmd.Branch; cmd.Name ] $"Create worktree {cmd.Name}" true
    | GitCommitCommand cmd ->
        confirmation "GitCommit" (Some cmd.ProjectName) [ VersionControlWrite ] cmd.Paths (Some "git") [ cmd.Message ] $"Commit {cmd.Paths.Length} path(s)" true
    | StartJobCommand cmd ->
        confirmation "StartJob" (Some cmd.ProjectName) [ ProcessExecution ] [] (Some cmd.Executable) cmd.Args $"Start job {cmd.Executable}" true
    | CancelJobCommand cmd ->
        confirmation "CancelJob" None [ ProcessExecution ] [] None [ cmd.JobId ] $"Cancel job {cmd.JobId}" false

let private trustAllows trustLevel command =
    match trustLevel, command with
    | FullTrust, _ -> true
    | PartialTrust, ScheduleBrowserExecutionCommand _
    | PartialTrust, CancelScheduledBrowserExecutionCommand _
    | PartialTrust, BrowserOpenTabCommand _
    | PartialTrust, BrowserCloseTabCommand _
    | PartialTrust, BrowserRunInNewTabCommand _
    | PartialTrust, BrowserExecuteFileCommand _
    | PartialTrust, BrowserExecuteJsCommand _
    | PartialTrust, BrowserCaptureDomCommand _
    | PartialTrust, BrowserExportDomCommand _
    | PartialTrust, RunCommandCommand _
    | PartialTrust, RunProjectTaskCommand _
    | PartialTrust, StartJobCommand _
    | PartialTrust, CancelJobCommand _ -> false
    // A trusted attachment URL may be imported in PartialTrust.
    | PartialTrust, ImportFileCommand cmd -> ImportNetwork.isKnownAttachment cmd.DownloadUrl
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
