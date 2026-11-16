module Client.TaskFirstUiSnapshotAdapter

open System
open System.IO
open System.Text.Json
open Jarvis.WorkQueues
open Client.TaskFirstUiProjection
open Client.LedgerStore
open Client.WorkQueueGitLedger
open Client.WorkQueueRoleAuthority
open Client.WorkQueueOperatorConversations
open Client.AccountCapacityLedger
open Client.ReviewWorkflow
open Client.OrchestrationApi

// A manually invoked, read-only join of individually verified Git authorities.
// None of the observations below is a cross-ledger admission/dispatch proof.
type ReadOnlyPorts =
    { ReadProject: unit -> Result<LedgerSnapshot * Project, string>
      ReadRoles: LedgerSnapshot -> Result<PinnedRoleView, string>
      ReadTaskTitle: LedgerSnapshot -> string -> Result<string, string>
      ReadOperatorThread: LedgerSnapshot -> string -> string -> Result<OperatorThreadSnapshot, string>
      ReadAccount: unit -> Result<AccountSnapshot, string> }

type RoleObservation =
    { RoleId: string
      Version: int
      Enabled: bool
      Queues: string list }

type OperatorThreadObservation =
    { Kind: string
      SubjectId: string
      Head: string
      BlobOid: string option
      Version: int
      Messages: OperatorMessage list }

type AccountObservation =
    { Head: string
      AccountId: string
      StateVersion: int64
      ConfigVersion: int64
      Migration: string
      Occupied: int
      Limit: int }

type OriginalSendObservation =
    { LaunchId: string
      ProjectHead: string
      Dispatch: string
      RegisteredUuid: string option
      OriginalJournal: string }

type ReadOnlyReport =
    { ProjectHead: Evidence<string>
      RoleCatalogRevision: Evidence<int>
      Dashboard: Dashboard
      Roles: Evidence<RoleObservation list>
      OperatorThreads: Evidence<OperatorThreadObservation list>
      AccountObserved: Evidence<AccountObservation>
      OriginalSends: Evidence<OriginalSendObservation list>
      SourceWarnings: string list }

module TaskFirstUiSnapshotAdapter =
    let private unknown reason = Evidence.Unknown reason
    let private catchRead label read =
        try read()
        with ex -> Error(label + ": " + ex.Message)

    let private projectUnknown reason =
        let reason = "Project snapshot unknown: " + reason
        projectDashboard "" Map.empty (Error reason) (Error "Separate account ledger is unjoined") None 0L

    let private dispatch (launch: Launch) =
        match launch.State with
        | LaunchState.ReservedUnregistered -> "ReservedUnregistered"
        | LaunchState.SubmittedUnregistered DispatchState.NotSubmitted -> "Unregistered; NotSubmitted in WQ02"
        | LaunchState.SubmittedUnregistered DispatchState.Submitted -> "Unregistered; Submitted in WQ02"
        | LaunchState.SubmittedUnregistered DispatchState.OutcomeUnknown -> "UNKNOWN original dispatch outcome in WQ02"
        | LaunchState.Registered (uuid, _) ->
            if String.IsNullOrWhiteSpace uuid then "Registered but UUID missing in WQ02"
            else "Registered in WQ02"
        | LaunchState.DemandInvalidatedPendingTerminal _ -> "Demand invalidated; terminal proof pending"
        | LaunchState.TerminalPendingRelease _ -> "Terminal; global release pending"

    let private originalSend head (launch: Launch) =
        let uuid =
            match launch.State with
            | LaunchState.Registered (id, _) when not (String.IsNullOrWhiteSpace id) -> Some id
            | _ -> None
        { LaunchId = launch.Id
          ProjectHead = head
          Dispatch = dispatch launch
          RegisteredUuid = uuid
          // No WQ03 original-browser journal adapter is part of the accepted
          // read-only contract. Never convert missing proof into "not sent".
          OriginalJournal = "UNKNOWN: original browser one-shot journal is not bound to this view" }

    let private accountObservation snapshot =
        let state = snapshot.State
        { Head = snapshot.Head
          AccountId = state.AccountId
          StateVersion = state.StateVersion
          ConfigVersion = state.ConfigVersion
          Migration = state.MigrationPhase
          Occupied = AccountCapacityLedger.occupied state
          Limit = state.MaxConcurrentAgents }

    let private roleRows (view: PinnedRoleView) =
        view.Catalog.Current
        |> Map.toList
        |> List.map (fun (id, version) ->
            match view.Catalog.Versions.TryFind id |> Option.bind (Map.tryFind version) with
            | None -> Error("Role " + id + " version " + string version + " missing from pinned catalog")
            | Some role ->
                Ok { RoleId = id
                     Version = version
                     Enabled = role.Enabled
                     Queues = role.QueueSubscriptions |> Set.toList })
        |> List.fold (fun state row ->
            match state, row with
            | Ok rows, Ok item -> Ok(item :: rows)
            | Error error, _ | _, Error error -> Error error) (Ok [])
        |> Result.map List.rev

    /// Snapshot ports are injectable for deterministic tests. The actual ports
    /// below fetch verified Git heads and read all project records at one head.
    /// Independent account observations never certify a free scheduler slot.
    let inspectWith (ports: ReadOnlyPorts) : ReadOnlyReport =
        let warnings = ResizeArray<string>()
        let account = catchRead "account" ports.ReadAccount
        let observed =
            match account with
            | Error reason -> unknown ("Account source missing/unverified: " + reason)
            | Ok snapshot -> Evidence.Known(accountObservation snapshot)
        let project = catchRead "project" ports.ReadProject
        match project with
        | Error reason ->
            { ProjectHead = unknown reason
              RoleCatalogRevision = unknown ("Project head unavailable: " + reason)
              Dashboard = projectUnknown reason
              Roles = unknown ("Project head unavailable: " + reason)
              OperatorThreads = unknown ("Project head unavailable: " + reason)
              AccountObserved = observed
              OriginalSends = unknown ("Project head unavailable: " + reason)
              SourceWarnings = [] }
        | Ok (head, projectState) ->
            let roles = catchRead "roles" (fun () -> ports.ReadRoles head)
            let roleEvidence, revision, roleIds =
                match roles with
                | Error reason ->
                    unknown ("WQ04 role authority unavailable at " + head.Commit + ": " + reason),
                    unknown ("WQ04 role authority unavailable: " + reason), []
                | Ok pinned when pinned.Proof.Head <> head.Commit
                                 || pinned.Proof.ProjectId <> head.Header.ProjectId
                                 || pinned.Project <> projectState ->
                    unknown ("WQ04 role/project head or content mismatch at " + head.Commit),
                    unknown "WQ04 project/role identity mismatch", []
                | Ok pinned ->
                    match roleRows pinned with
                    | Error reason ->
                        unknown reason, unknown reason, []
                    | Ok rows ->
                        Evidence.Known rows, Evidence.Known pinned.Catalog.Revision,
                        rows |> List.map (fun row -> row.RoleId)

            let taskIds =
                projectState.Works
                |> Map.toList
                |> List.choose (fun (_, stage) ->
                    match stage.Kind with
                    | WorkKind.RoleMessage _ -> None
                    | _ -> Some stage.SubjectId)
                |> List.distinct |> List.sort
            let titles =
                taskIds
                |> List.choose (fun id ->
                    match catchRead ("task title " + id) (fun () -> ports.ReadTaskTitle head id) with
                    | Ok title when not (String.IsNullOrWhiteSpace title) -> Some(id, title)
                    | Ok _ ->
                        warnings.Add("UNKNOWN task title for " + id + " at " + head.Commit + ": blank canonical title")
                        None
                    | Error reason ->
                        warnings.Add("UNKNOWN task title for " + id + " at " + head.Commit + ": " + reason)
                        None)
                |> Map.ofList

            let subjects =
                ([ for id in taskIds -> "Task", id ]
                 @ [ for id in ("Lead" :: roleIds @
                                (projectState.Subscriptions |> Map.toList |> List.map fst)
                                |> List.distinct |> List.sort) -> "Role", id ])
                |> List.distinct
            let conversations =
                subjects
                |> List.map (fun (kind, id) ->
                    match catchRead (kind + "(" + id + ")") (fun () -> ports.ReadOperatorThread head kind id) with
                    | Error reason -> Error(kind + "(" + id + ") @ " + head.Commit + ": " + reason)
                    | Ok snapshot when snapshot.GitHead <> head.Commit
                                       || snapshot.Thread.SubjectId <> id
                                       || snapshot.Thread.SubjectKind <> kind
                                       || snapshot.Thread.ProjectId <> head.Header.ProjectId ->
                        Error(kind + "(" + id + "): subject, project or head mismatch")
                    | Ok snapshot ->
                        Ok { Kind = kind; SubjectId = id; Head = head.Commit
                             BlobOid = snapshot.BlobOid; Version = snapshot.Thread.Version
                             Messages = snapshot.Thread.Messages |> Array.toList })
            let threads =
                let errors = conversations |> List.choose (function Error reason -> Some reason | _ -> None)
                if List.isEmpty errors then
                    conversations
                    |> List.choose (function Ok value -> Some value | _ -> None)
                    |> Evidence.Known
                else
                    unknown ("WQ06 conversation source incomplete: " + String.concat "; " errors)

            let original =
                if projectState.QueueCovered then
                    projectState.Launches
                    |> Map.toList
                    |> List.map (snd >> originalSend head.Commit)
                    |> Evidence.Known
                else unknown ("Launch index not covered at " + head.Commit)
            { ProjectHead = Evidence.Known head.Commit
              RoleCatalogRevision = revision
              Dashboard =
                  projectDashboard head.Header.ProjectId titles (Ok projectState)
                      (Error "Account ledger has no verified cross-ledger high-water join")
                      None 0L
              Roles = roleEvidence
              OperatorThreads = threads
              AccountObserved = observed
              OriginalSends = original
              SourceWarnings = List.ofSeq warnings }

    let portsForBindings (project: LedgerBinding) (account: AccountBinding option) =
        { ReadProject = fun () ->
              LedgerStore.fetch project
              |> Result.bind (fun head ->
                  WorkQueueGitLedger.loadAt project head |> Result.map (fun state -> head, state))
              |> Result.mapError (sprintf "%A")
          ReadRoles = fun head ->
              WorkQueueRoleAuthority.loadAt project head |> Result.mapError (sprintf "%A")
          ReadTaskTitle = fun head id ->
              ReviewWorkflow.getTaskAt project head id
              |> Result.map (fun (task, _) -> task.Title)
              |> Result.mapError (sprintf "%A")
          ReadOperatorThread = fun head kind id ->
              WorkQueueOperatorConversations.readAt project head kind id
              |> Result.mapError (sprintf "%A")
          ReadAccount = fun () ->
              match account with
              | None -> Error "No explicit account trust binding was selected"
              | Some binding ->
                  AccountCapacityLedger.fetch binding |> Result.mapError (sprintf "%A") }

    let private terminalSafe (value: string) =
        if isNull value then "(null)"
        else value
             |> Seq.map (fun ch ->
                 match Char.GetUnicodeCategory ch with
                 | Globalization.UnicodeCategory.Format
                 | Globalization.UnicodeCategory.LineSeparator
                 | Globalization.UnicodeCategory.ParagraphSeparator -> ' '
                 | _ when Char.IsControl ch -> ' '
                 | _ -> ch)
             |> Array.ofSeq |> String

    let private evidence heading render value =
        match value with
        | Evidence.Unknown reason -> [ heading; "  UNKNOWN - " + reason ]
        | Evidence.Known item -> [ heading ] @ render item

    /// Informational display only. No generated IDs, post/claim controls,
    /// scheduler routes, side-effect journals or mutation APIs exist here.
    let renderReport report =
        let source =
            evidence "Canonical project head" (fun head -> [ "  " + head ]) report.ProjectHead
        let roles =
            evidence "Versioned WQ04 roles" (fun items ->
                if List.isEmpty items then [ "  (none in verified catalog)" ]
                else items |> List.map (fun role ->
                    sprintf "  Role(%s) v%d %s | queues: %s"
                        role.RoleId role.Version
                        (if role.Enabled then "enabled" else "disabled")
                        (String.concat "," role.Queues))) report.Roles
        let revision =
            evidence "Role catalog revision" (fun value -> [ "  " + string value ])
                report.RoleCatalogRevision
        let threads =
            evidence "WQ06 operator Task/Role conversations (read only)" (fun items ->
                if List.isEmpty items then [ "  (none in verified snapshot)" ]
                else
                    items |> List.collect (fun thread ->
                        let ident = sprintf "  %s(%s) v%d at %s | blob: %s"
                                        thread.Kind thread.SubjectId thread.Version
                                        thread.Head (thread.BlobOid |> Option.defaultValue "(absent)")
                        let messages =
                            if List.isEmpty thread.Messages then [ "    (no messages at pinned head)" ]
                            else
                                thread.Messages |> List.map (fun m ->
                                    sprintf "    %s [%s] by %s | generation %d | operation %s | reply %s | %s"
                                        m.Id m.Status m.Author m.HandlerGeneration
                                        m.LastOperationId
                                        (if String.IsNullOrWhiteSpace m.ReplyId then "(none)" else m.ReplyId)
                                        m.Body)
                        ident :: messages)) report.OperatorThreads
        let account =
            evidence "Independent account occupancy (observation; NOT a capacity grant)" (fun item ->
                [ sprintf "  head %s | account %s | state v%d config v%d"
                      item.Head item.AccountId item.StateVersion item.ConfigVersion
                  sprintf "  %d HELD at that head / configured limit %d | migration %s"
                      item.Occupied item.Limit item.Migration
                  "  UNKNOWN safe free slots: no project/account high-water join" ])
                report.AccountObserved
        let original =
            evidence "Original browser sends / unregistered launches" (fun items ->
                if List.isEmpty items then [ "  (none in verified WQ02 launch index)" ]
                else items |> List.map (fun item ->
                    sprintf "  %s @ %s: %s | UUID: %s | %s"
                        item.LaunchId item.ProjectHead item.Dispatch
                        (item.RegisteredUuid |> Option.defaultValue "(unregistered/unknown)")
                        item.OriginalJournal)) report.OriginalSends
        (source @ TaskFirstUiProjection.renderPreview report.Dashboard
         @ revision @ roles @ threads @ account @ original
         @ (if List.isEmpty report.SourceWarnings then []
            else "Source evidence gaps" :: (report.SourceWarnings |> List.map (fun reason -> "  " + reason))))
        |> List.map terminalSafe

    [<CLIMutable>]
    type private AccountBindingFile =
        { RepositoryPath: string
          RemoteName: string
          ApprovedFetchUrl: string
          ApprovedPushUrl: string
          AccountId: string
          LedgerId: string
          Epoch: int }

    let private parseAccountBinding file : Result<AccountBinding, string> =
        try
            let value =
                JsonSerializer.Deserialize<AccountBindingFile>(
                    File.ReadAllText file, JsonSerializerOptions(PropertyNameCaseInsensitive = true))
            if isNull (box value) then Error "Empty account binding file"
            else
                let accountOk, accountId = Guid.TryParse value.AccountId
                let ledgerOk, ledgerId = Guid.TryParse value.LedgerId
                if not accountOk || accountId = Guid.Empty
                   || not ledgerOk || ledgerId = Guid.Empty || value.Epoch < 1 then
                    Error "Account and ledger identities must be nonempty UUIDs with positive epoch"
                else
                    Ok {
                        RepositoryPath = value.RepositoryPath
                        RemoteName = value.RemoteName
                        ApprovedFetchUrl = value.ApprovedFetchUrl
                        ApprovedPushUrl = value.ApprovedPushUrl
                        AccountId = accountId
                        LedgerId = ledgerId
                        Epoch = value.Epoch
                    }
        with ex -> Error ex.Message

    let run argv =
        match argv with
        | [ "tasks"; "preview"; root ]
        | [ "tasks"; "preview"; root; _ ] ->
            try
                let account =
                    match argv with
                    | [ _; _; _; path ] -> parseAccountBinding path |> Result.map Some
                    | _ -> Ok None
                match LedgerBindingConfig.loadAtRootWithSnapshotUsing
                          LedgerStore.fetch (Path.GetFullPath root), account with
                | Error failure, _ ->
                    eprintfn "UNKNOWN: approved project trust binding unavailable: %A" failure
                    3
                | _, Error failure ->
                    eprintfn "UNKNOWN: explicit account binding unreadable: %s" failure
                    3
                | Ok (binding, _), Ok account ->
                    let report = inspectWith (portsForBindings binding account)
                    renderReport report |> List.iter (printfn "%s")
                    match report.ProjectHead with
                    | Evidence.Unknown _ -> 3
                    | Evidence.Known _ -> 0
            with ex ->
                eprintfn "UNKNOWN: task-first read-only snapshot failed: %s" ex.Message
                3
        | _ ->
            eprintfn "Usage: JarvisClient tasks preview PROJECT_ROOT [ACCOUNT_BINDING_JSON]"
            eprintfn "Read-only verified Git snapshots only; no Scheduler, post, claim or browser dispatch."
            2
