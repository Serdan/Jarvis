module Client.LegacyWriterProtocolFence

open System
open System.IO
open System.Text.Json
open Common
open Client.LedgerStore
open Client.AccountCapacityLedger
open Client.WorkQueueGitLedger
open Jarvis.WorkQueues

// Default-off, host-selected legacy command fence. This is NOT an account
// migration writer, a new permission system, or proof that every client has
// installed the protocol. No wire command can supply a waiver.
[<RequireQualifiedAccess>]
type LegacyCommandKind =
    | HistoricalRead
    | WorkNative
    | OperatorRecovery
    | LegacyMutation of string

[<RequireQualifiedAccess>]
type LegacyWriterDecision =
    | Allowed
    | Blocked of code: string * accountHead: string * projectHead: string
    | Unknown of code: string

type internal LegacyFenceScope =
    { ProjectName: string
      Project: LedgerBinding
      Account: AccountBinding
      ExpectedAccountHead: string
      ExpectedProjectHead: string
      ProtocolVersion: int }

// The opt-in host document is a local provisioning boundary, not an MCP field.
// Even a syntactically valid file does not grant migration GO: the underlying
// account/project identities, authority and Git snapshots must verify.
[<CLIMutable>]
type LegacyFenceFile =
    { SchemaVersion: int
      ProtocolVersion: int
      ProjectName: string
      ProjectRepositoryPath: string
      ProjectRemoteName: string
      ProjectApprovedFetchUrl: string
      ProjectApprovedPushUrl: string
      ProjectId: string
      ProjectLedgerId: string
      ProjectEpoch: int
      AccountRepositoryPath: string
      AccountRemoteName: string
      AccountApprovedFetchUrl: string
      AccountApprovedPushUrl: string
      AccountId: string
      AccountLedgerId: string
      AccountEpoch: int
      ExpectedAccountHead: string
      ExpectedProjectHead: string }

module LegacyWriterProtocolFence =
    [<Literal>]
    let HostBindingEnvironment = "JARVIS_WQ10_LEGACY_WRITER_FENCE_BINDING"

    [<Literal>]
    let ProtocolVersion = 1

    let classify = function
        | TaskWriteCommand cmd when cmd.Action = "CompositionMutate"
                                    || cmd.Action = "CompositionAcceptIntegrated" ->
            LegacyCommandKind.WorkNative
        // AgentInbox is not read-only: it can commit Ready -> Working before
        // returning the nominal TaskRead result.
        | TaskReadCommand cmd when cmd.Action = "AgentInbox" ->
            LegacyCommandKind.LegacyMutation "AgentInboxImplicitActivation"
        | TaskWriteCommand cmd when cmd.Action = "ExportTaskSpec" ->
            LegacyCommandKind.HistoricalRead
        | TaskWriteCommand cmd ->
            LegacyCommandKind.LegacyMutation ("TaskWrite." + cmd.Action)
        | ReviewWriteCommand cmd ->
            LegacyCommandKind.LegacyMutation ("ReviewWrite." + cmd.Action)
        | ChallengeWriteCommand cmd ->
            LegacyCommandKind.LegacyMutation ("ChallengeWrite." + cmd.Action)
        | CrossProjectNotificationCommand _ ->
            LegacyCommandKind.LegacyMutation "CrossProjectLegacyNotification"
        | RetryPendingIntentCommand _ ->
            // Retrying an old command can republish a stale legacy mutation.
            LegacyCommandKind.LegacyMutation "RetryPendingLegacyIntent"
        | ScheduleExecutionCommand _ ->
            LegacyCommandKind.LegacyMutation "ScheduleLegacyExecution"
        | ScheduleBrowserExecutionCommand _ ->
            LegacyCommandKind.LegacyMutation "ScheduleLegacyBrowser"
        | AbandonPendingIntentCommand _ | SupersedePendingIntentCommand _ ->
            // Both require the original SHA/head and keep recovery evidence;
            // unlike RetryPendingIntent, neither replays an old Git write.
            LegacyCommandKind.OperatorRecovery
        | WorkQueueReadCommand _ | WorkQueueWriteCommand _
        | OperatorConversationReadCommand _ | OperatorConversationWriteCommand _ ->
            LegacyCommandKind.WorkNative
        | _ -> LegacyCommandKind.HistoricalRead

    let private validOid (text: string) =
        not (String.IsNullOrWhiteSpace text)
        && (text.Length = 40 || text.Length = 64)
        && (text |> Seq.forall (fun c ->
            (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))

    let internal sameProjectAuthority (a: LedgerBinding) (b: LedgerBinding) =
        a.ProjectId = b.ProjectId && a.LedgerId = b.LedgerId
        && a.Epoch = b.Epoch && a.Epoch > 0
        && a.RemoteName = b.RemoteName
        && a.ApprovedFetchUrl = b.ApprovedFetchUrl
        && a.ApprovedPushUrl = b.ApprovedPushUrl

    let internal scopeValid (scope: LegacyFenceScope) =
        scope.ProtocolVersion = ProtocolVersion
        && not (String.IsNullOrWhiteSpace scope.ProjectName)
        && not (isNull (box scope.Project))
        && not (isNull (box scope.Account))
        && scope.Project.ProjectId <> Guid.Empty
        && scope.Project.LedgerId <> Guid.Empty
        && scope.Account.AccountId <> Guid.Empty
        && scope.Account.LedgerId <> Guid.Empty
        && scope.Account.Epoch > 0
        && validOid scope.ExpectedAccountHead
        && validOid scope.ExpectedProjectHead

    // Every evaluation fetches genuine original account/project Git authority
    // (never a local UI/cache/SQLite phase). Unknown is a DENY, not approval.
    // Account and project are different Git authorities: this is a boundary
    // guard, NOT an atomic cross-ledger freeze or a complete-client witness.
    let internal decideWithScope (scope: LegacyFenceScope) (command: AgentCommand) =
        match classify command with
        | LegacyCommandKind.HistoricalRead
        | LegacyCommandKind.OperatorRecovery
        | LegacyCommandKind.WorkNative -> LegacyWriterDecision.Allowed
        | LegacyCommandKind.LegacyMutation _ ->
            if not (scopeValid scope) then
                LegacyWriterDecision.Unknown "FenceProtocolOrAuthorityInvalid"
            elif AgentCommandInfo.projectName command <> Some scope.ProjectName then
                LegacyWriterDecision.Unknown "FenceProjectScopeMismatch"
            else
                match AccountCapacityLedger.fetch scope.Account with
                | Error _ -> LegacyWriterDecision.Unknown "AccountEpochUnreadable"
                | Ok account ->
                    if account.State.MigrationPhase = "LegacyActive" then
                        // No old-writer exclusion has been requested yet.
                        // This preserves pre-migration legacy behavior while
                        // still requiring a verified account Git identity.
                        LegacyWriterDecision.Allowed
                    elif account.State.MigrationPhase = "Aborted" then
                        // An Aborted marker does not by itself re-authorize old
                        // writers; only a separately reviewed rollback can.
                        LegacyWriterDecision.Unknown "AbortedEpochRequiresOperatorReconciliation"
                    elif not (List.contains account.State.MigrationPhase
                                 [ "Frozen"; "Preparing"; "Validated"; "Activated" ]) then
                        LegacyWriterDecision.Unknown "UnknownAccountMigrationPhase"
                    elif account.Head <> scope.ExpectedAccountHead then
                        LegacyWriterDecision.Unknown "AccountEpochHeadChanged"
                    else
                        match LedgerStore.fetch scope.Project with
                        | Error _ -> LegacyWriterDecision.Unknown "ProjectEpochUnreadable"
                        | Ok snapshot when snapshot.Commit <> scope.ExpectedProjectHead ->
                            LegacyWriterDecision.Unknown "ProjectEpochHeadChanged"
                        | Ok snapshot ->
                            match WorkQueueGitLedger.loadAt scope.Project snapshot with
                            | Error _ -> LegacyWriterDecision.Unknown "ProjectMigrationProofMissing"
                            | Ok project when project.Migration = MigrationEpoch.Frozen
                                              || project.Migration = MigrationEpoch.Preparing
                                              || project.Migration = MigrationEpoch.Validated
                                              || project.Migration = MigrationEpoch.Activated ->
                                LegacyWriterDecision.Blocked(
                                    "OldWriterFenced", account.Head, snapshot.Commit)
                            | _ -> LegacyWriterDecision.Unknown "ProjectMigrationEpochMismatch"

    let private message = function
        | LegacyWriterDecision.Allowed -> Ok ()
        | LegacyWriterDecision.Blocked(code, accountHead, projectHead) ->
            Error($"{code}: legacy Agent/Manager mutation denied at verified account {accountHead} and project {projectHead}; use Work-native operations or explicit audited operator recovery.")
        | LegacyWriterDecision.Unknown code ->
            Error($"OldWriterFenceUnknown/{code}: original Git epoch/protocol proof is unavailable or stale; do not retry under a new UUID. Reconcile the approved host binding and heads.")

    let internal protectWithScope scope command operation =
        decideWithScope scope command
        |> message
        |> Result.bind (fun () -> operation ())

    let private noSymlinkAncestor path =
        try
            let rec loop current =
                if String.IsNullOrWhiteSpace current then true
                elif (File.GetAttributes current).HasFlag(FileAttributes.ReparsePoint) then false
                else
                    let parent = Path.GetDirectoryName current
                    isNull parent || parent = current || loop parent
            loop path
        with _ -> false

    let internal readHostScope () : Result<LegacyFenceScope option, string> =
        let file = Environment.GetEnvironmentVariable HostBindingEnvironment
        if String.IsNullOrWhiteSpace file then Ok None // DEFAULT OFF
        else
            try
                if not (Path.IsPathFullyQualified file)
                   || not (File.Exists file)
                   || not (noSymlinkAncestor file)
                   || (not (OperatingSystem.IsWindows())
                       && (File.GetUnixFileMode file
                           &&& (UnixFileMode.GroupWrite ||| UnixFileMode.OtherWrite))
                              <> UnixFileMode.None) then
                    Error "HostFenceBindingUntrustedPath"
                else
                    let text = File.ReadAllText file
                    if text.Length > 8192 then Error "HostFenceBindingTooLarge"
                    else
                        let dto = JsonSerializer.Deserialize<LegacyFenceFile>(
                            text, JsonSerializerOptions(PropertyNameCaseInsensitive = true))
                        if isNull (box dto) || dto.SchemaVersion <> 1 then
                            Error "HostFenceBindingSchemaUnsupported"
                        else
                            let p, projectId = Guid.TryParse dto.ProjectId
                            let pl, projectLedger = Guid.TryParse dto.ProjectLedgerId
                            let a, accountId = Guid.TryParse dto.AccountId
                            let al, accountLedger = Guid.TryParse dto.AccountLedgerId
                            if not (p && pl && a && al) then Error "HostFenceBindingIdsInvalid"
                            else
                                let project =
                                    { RepositoryPath = dto.ProjectRepositoryPath
                                      RemoteName = dto.ProjectRemoteName
                                      ApprovedFetchUrl = dto.ProjectApprovedFetchUrl
                                      ApprovedPushUrl = dto.ProjectApprovedPushUrl
                                      ProjectId = projectId; LedgerId = projectLedger
                                      Epoch = dto.ProjectEpoch }
                                let account =
                                    { RepositoryPath = dto.AccountRepositoryPath
                                      RemoteName = dto.AccountRemoteName
                                      ApprovedFetchUrl = dto.AccountApprovedFetchUrl
                                      ApprovedPushUrl = dto.AccountApprovedPushUrl
                                      AccountId = accountId; LedgerId = accountLedger
                                      Epoch = dto.AccountEpoch }
                                Ok(Some {
                                    ProjectName = dto.ProjectName
                                    Project = project; Account = account
                                    ExpectedAccountHead = dto.ExpectedAccountHead
                                    ExpectedProjectHead = dto.ExpectedProjectHead
                                    ProtocolVersion = dto.ProtocolVersion })
            with ex -> Error ("HostFenceBindingUnreadable/" + ex.GetType().Name)

    // Called once before any legacy dispatch or implicit activity. The current
    // existing per-project trust binding comes from Jarvis configuration, NOT
    // command-supplied project/agent label or this host file itself.
    let checkHost (trustedProject: string -> Result<LedgerBinding, string>)
                  (command: AgentCommand) : Result<unit, string> =
        match classify command with
        | LegacyCommandKind.HistoricalRead
        | LegacyCommandKind.WorkNative
        | LegacyCommandKind.OperatorRecovery -> Ok ()
        | LegacyCommandKind.LegacyMutation _ ->
            match readHostScope () with
            | Error reason -> message (LegacyWriterDecision.Unknown reason)
            | Ok None -> Ok ()
            | Ok(Some scope) ->
                match AgentCommandInfo.projectName command with
                | None -> message (LegacyWriterDecision.Unknown "ProjectScopeMissing")
                | Some name when name <> scope.ProjectName ->
                    message (LegacyWriterDecision.Unknown "HostFenceProjectMismatch")
                | Some name ->
                    match trustedProject name with
                    | Error _ -> message (LegacyWriterDecision.Unknown "TrustedProjectBindingUnavailable")
                    | Ok approved when not (sameProjectAuthority approved scope.Project) ->
                        message (LegacyWriterDecision.Unknown "TrustedProjectBindingChanged")
                    | Ok approved ->
                        decideWithScope { scope with Project = approved } command |> message

    // Historical read commands may still implicitly activate a Ready Agent or
    // consume its response alerts. Evaluate that *internal legacy side effect*
    // separately instead of blocking the read. A synthetic typed AgentInbox
    // request is only a discriminator; neither the caller's agent label nor
    // a supplied boolean becomes authority.
    let implicitLegacyActivityAllowed trustedProject (projectName: string) =
        let effect =
            TaskReadCommand
                { ProjectName = projectName; AgentId = ""; Action = "AgentInbox"
                  TaskId = ""; AssignmentGeneration = 0; CheckpointSequence = 0L
                  Cursor = ""; PageSize = 0; BeforeOid = ""; AfterOid = ""
                  RelativePath = "" }
        checkHost trustedProject effect |> Result.isOk
