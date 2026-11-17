namespace Jarvis.WorkStatefulServer

open Server
open System
open System.IO
open System.Text.Json
open Microsoft.Data.Sqlite
open Jarvis.WorkStatefulApi

// H1a is a library-only, read-only server adapter. A future authenticated
// host must derive this grant from its verified session/reservation, NEVER
// deserialize it from a client request or trust a caller-supplied role label.
type internal AuthenticatedWorkReadGrant =
    { Authenticated: bool
      SubjectId: string
      AccountId: string
      AuthorityId: string
      BoundDatabasePath: string
      AllowedProjectIds: Set<string>
      VerifiedRoleId: string option }

[<RequireQualifiedAccess>]
type WorkStatefulReadFailure =
    | Unauthenticated
    | OutOfScope
    | UnsupportedVersion
    | IncompleteAuthority of string
    | StorageUnavailable of string

type private WorkStatefulRawRow =
    { Kind: string
      Id: string
      Version: int64
      Json: string
      GraphVersion: int64
      StageGeneration: int64
      ClaimGeneration: int64
      StopGeneration: int64
      RoleVersion: int64
      SourceSha: string option
      ReviewSha: string option
      IntegrationSha: string option }

[<RequireQualifiedAccess>]
module internal WorkStatefulApiRead =
    [<Literal>]
    let private MaxRows = 10000
    [<Literal>]
    let private MaxJsonBytes = 4 * 1024 * 1024
    [<Literal>]
    let private AuthorityApplicationId = 0x4A57524B

    let private nonblank (value: string) =
        not (String.IsNullOrWhiteSpace value) && value = value.Trim()

    let private field (body: JsonElement) (key: string) =
        let mutable found = Unchecked.defaultof<JsonElement>
        if body.TryGetProperty(key, &found) then Some found else None

    let private str (body: JsonElement) key =
        match field body key with
        | Some v when v.ValueKind = JsonValueKind.String ->
            match v.GetString() with
            | value when nonblank value -> Some value
            | _ -> None
        | _ -> None

    let private boolean (body: JsonElement) key =
        match field body key with
        | Some v when v.ValueKind = JsonValueKind.True -> Some true
        | Some v when v.ValueKind = JsonValueKind.False -> Some false
        | _ -> None

    let private number (body: JsonElement) key =
        match field body key with
        | Some v when v.ValueKind = JsonValueKind.Number ->
            let mutable result = 0L
            if v.TryGetInt64(&result) then Some result else None
        | _ -> None

    let private strings (body: JsonElement) key =
        match field body key with
        | Some v when v.ValueKind = JsonValueKind.Array ->
            let items = v.EnumerateArray() |> Seq.toList
            if items |> List.forall (fun item -> item.ValueKind = JsonValueKind.String &&
                                                   nonblank (item.GetString())) then
                Some(items |> List.map (fun item -> item.GetString()))
            else None
        | _ -> None

    let private parse (row: WorkStatefulRawRow) f =
        use document = JsonDocument.Parse row.Json
        if document.RootElement.ValueKind <> JsonValueKind.Object then
            invalidOp "Work SQLite body is not an object."
        f document.RootElement

    let private nullable (reader: SqliteDataReader) index =
        if reader.IsDBNull index then None else Some(reader.GetString index)

    let private query (connection: SqliteConnection) (tx: SqliteTransaction) sql project =
        use command = connection.CreateCommand()
        command.Transaction <- tx
        command.CommandText <- sql
        if not (isNull project) then
            command.Parameters.AddWithValue("$project", project) |> ignore
        command

    let private scalar (connection: SqliteConnection) (tx: SqliteTransaction) statement =
        use command = query connection tx statement null
        Convert.ToInt64(command.ExecuteScalar())

    let private rows (connection: SqliteConnection) (tx: SqliteTransaction) project =
        use command = query connection tx
                          """SELECT record_kind, entity_id, version, body_json,
                                    graph_version, stage_generation, claim_generation,
                                    stop_generation, role_version, source_sha,
                                    review_sha, integration_sha
                             FROM work_records
                             WHERE project_id=$project
                             AND record_kind IN ('project','work','stage','role','review','integration')
                             ORDER BY record_kind, entity_id;""" project
        use reader = command.ExecuteReader()
        let found = ResizeArray<WorkStatefulRawRow>()
        let mutable size = 0
        while reader.Read() do
            if found.Count >= MaxRows then
                invalidOp "Work snapshot exceeds the bounded read limit."
            let json = reader.GetString 3
            size <- size + System.Text.Encoding.UTF8.GetByteCount json
            if size > MaxJsonBytes then
                invalidOp "Work snapshot exceeds the bounded JSON budget."
            found.Add
                { Kind = reader.GetString 0
                  Id = reader.GetString 1
                  Version = reader.GetInt64 2
                  Json = json
                  GraphVersion = reader.GetInt64 4
                  StageGeneration = reader.GetInt64 5
                  ClaimGeneration = reader.GetInt64 6
                  StopGeneration = reader.GetInt64 7
                  RoleVersion = reader.GetInt64 8
                  SourceSha = nullable reader 9
                  ReviewSha = nullable reader 10
                  IntegrationSha = nullable reader 11 }
        found |> Seq.toList

    let private capacity (connection: SqliteConnection) (tx: SqliteTransaction) accountId =
        use command = query connection tx
                          """SELECT version, maximum, reserved
                             FROM work_capacity WHERE account_id=$project;""" accountId
        use reader = command.ExecuteReader()
        if reader.Read() then
            Some { RowVersion = reader.GetInt64 0
                   Limit = reader.GetInt32 1
                   Occupied = reader.GetInt32 2 }
        else None

    let private projectNode project (row: WorkStatefulRawRow) =
        parse row (fun body ->
            { ProjectId = project
              Id = row.Id
              RowVersion = row.Version
              GraphVersion = row.GraphVersion
              Kind = str body "kind"
              Title = str body "title"
              ParentId = str body "parentId"
              State = str body "state"
              ProposalState = str body "proposalState"
              ApprovalReceipt = str body "approvalReceipt"
              RequiredForParent = boolean body "requiredForParent"
              AcceptanceEvidence = str body "acceptanceEvidence" })

    let private stage project (row: WorkStatefulRawRow) =
        parse row (fun body ->
            { ProjectId = project
              Id = row.Id
              RowVersion = row.Version
              GraphVersion = row.GraphVersion
              NodeId = str body "nodeId"
              QueueId = str body "queueId"
              Kind = str body "kind"
              State = str body "state"
              ProposalState = str body "proposalState"
              Gate = str body "gate"
              ClaimLaunchId = str body "claimLaunch"
              StageGeneration = row.StageGeneration
              ClaimGeneration = row.ClaimGeneration
              StopGeneration = row.StopGeneration
              SourceCommit = row.SourceSha
              ReviewCommit = row.ReviewSha
              IntegrationCommit = row.IntegrationSha
              AvailableSequence = number body "availableSequence"
              LeadPriority =
                  number body "leadPriority"
                  |> Option.bind (fun n ->
                      if n >= int64 Int32.MinValue && n <= int64 Int32.MaxValue
                      then Some(int n) else None) })

    let private role project (row: WorkStatefulRawRow) =
        parse row (fun body ->
            { ProjectId = project
              Id = row.Id
              RoleVersion = row.RoleVersion
              Queues =
                  if str body "state" = Some "Active" then strings body "queues"
                  else None })

    /// No host registration, mutation, scheduler, or default-off flag is
    /// touched. Every requested row and global capacity sample comes from
    /// one read transaction in the database bound to the trusted grant.
    let readSnapshot (store: WorkSqliteAuthority)
                     (grant: AuthenticatedWorkReadGrant)
                     (request: WorkReadRequest)
                     : Result<WorkReadSnapshot, WorkStatefulReadFailure> =
        if request.ApiVersion <> WorkReadVersion.Current then
            Error WorkStatefulReadFailure.UnsupportedVersion
        elif not grant.Authenticated || not (nonblank grant.SubjectId) ||
             not (nonblank grant.AuthorityId) || not (nonblank grant.AccountId) then
            Error WorkStatefulReadFailure.Unauthenticated
        elif not (nonblank request.ProjectId)
             || not (Set.contains request.ProjectId grant.AllowedProjectIds)
             || (request.RoleId.IsSome && request.RoleId <> grant.VerifiedRoleId) then
            Error WorkStatefulReadFailure.OutOfScope
        elif not (nonblank grant.BoundDatabasePath) ||
             not (nonblank store.DatabasePath) ||
             not (String.Equals(Path.GetFullPath grant.BoundDatabasePath,
                                Path.GetFullPath store.DatabasePath, StringComparison.Ordinal)) then
            Error WorkStatefulReadFailure.OutOfScope
        else
            try
                let builder = SqliteConnectionStringBuilder()
                builder.DataSource <- store.DatabasePath
                builder.Mode <- SqliteOpenMode.ReadOnly
                builder.Pooling <- false
                use connection = new SqliteConnection(builder.ToString())
                connection.Open()
                use transaction = connection.BeginTransaction()
                let schema = scalar connection transaction "PRAGMA user_version;"
                let app = scalar connection transaction "PRAGMA application_id;"
                if schema <> 1L || app <> int64 AuthorityApplicationId then
                    Error(WorkStatefulReadFailure.IncompleteAuthority
                        "Missing or incompatible Work SQLite schema; telemetry/foreign databases are not authority.")
                else
                    let revision = scalar connection transaction
                                       "SELECT last_sequence FROM work_meta WHERE id=1;"
                    let recordRows = rows connection transaction request.ProjectId
                    let project =
                        recordRows
                        |> List.tryFind (fun r -> r.Kind = "project" && r.Id = "authority")
                    match project with
                    | None ->
                        Error(WorkStatefulReadFailure.IncompleteAuthority
                            "No canonical project authority row at the requested project identity.")
                    | Some authoritative ->
                        let covered, boundAccount, registeredRoleIds = parse authoritative (fun body ->
                            str body "coverage" = Some "Verified", str body "accountId", strings body "roleIds")
                        if boundAccount.IsNone then
                            Error(WorkStatefulReadFailure.IncompleteAuthority
                                "Project/account instance binding is missing from the authority row.")
                        elif boundAccount <> Some grant.AccountId then
                            Error WorkStatefulReadFailure.OutOfScope
                        elif not covered then
                            Error(WorkStatefulReadFailure.IncompleteAuthority
                                "Project source/role/graph coverage has not been verified.")
                        else
                            let allStages =
                                recordRows
                                |> List.filter (fun r -> r.Kind = "stage")
                            let nodes =
                                recordRows
                                |> List.filter (fun r -> r.Kind = "work")
                                |> List.map (projectNode request.ProjectId)
                            let stages =
                                allStages |> List.map (stage request.ProjectId)
                            let visibleRoles =
                                recordRows
                                |> List.filter (fun r ->
                                    r.Kind = "role" &&
                                    grant.VerifiedRoleId = Some r.Id &&
                                    (registeredRoleIds |> Option.exists (List.contains r.Id)) &&
                                    r.StopGeneration = authoritative.StopGeneration)
                                |> List.map (role request.ProjectId)
                            let diagnostics =
                                [ if nodes |> List.exists (fun node ->
                                        node.Kind.IsNone || node.Title.IsNone || node.State.IsNone) then
                                      "Some Work rows lack verified graph/node metadata; they are not display-ready."
                                  if stages |> List.exists (fun stage ->
                                        stage.NodeId.IsNone || stage.Kind.IsNone ||
                                        stage.QueueId.IsNone || stage.ProposalState.IsNone) then
                                      "Stage/link/readiness metadata is incomplete; never infer runnable Work from its state."
                                  if allStages |> List.exists (fun stage ->
                                        stage.GraphVersion <> authoritative.GraphVersion ||
                                        stage.StopGeneration <> authoritative.StopGeneration) then
                                      "Stage graph/STOP generations diverge from project authority."
                                  if visibleRoles.IsEmpty then
                                      "No matching verified active role subscription was readable."
                                  "Derived blocking, eligibility and source authorization are not attested by this read-only adapter."
                                  "Backup and Recovery have no integrated authoritative F/G proof." ]
                            let snapshot =
                                { ApiVersion = WorkReadVersion.Current
                                  AuthorityId = grant.AuthorityId
                                  ProjectId = request.ProjectId
                                  Revision = revision
                                  CapturedAt = DateTimeOffset.UtcNow
                                  ProjectRowVersion = authoritative.Version
                                  GraphVersion = authoritative.GraphVersion
                                  StopGeneration = authoritative.StopGeneration
                                  VerifiedRoleId = grant.VerifiedRoleId
                                  Nodes = nodes
                                  Stages = stages
                                  Roles = visibleRoles
                                  Capacity = capacity connection transaction grant.AccountId
                                  BackupUnknownReason = "F1 verified backup/export evidence is not integrated."
                                  RecoveryUnknownReason = "G1 Recovery fencing evidence is not integrated."
                                  Diagnostics = diagnostics }
                            transaction.Commit()
                            Ok snapshot
            with
            | :? SqliteException as ex ->
                Error(WorkStatefulReadFailure.StorageUnavailable ex.Message)
            | :? IOException as ex ->
                Error(WorkStatefulReadFailure.StorageUnavailable ex.Message)
            | :? UnauthorizedAccessException as ex ->
                Error(WorkStatefulReadFailure.StorageUnavailable ex.Message)
            | :? InvalidOperationException as ex ->
                Error(WorkStatefulReadFailure.IncompleteAuthority ex.Message)
            | :? JsonException as ex ->
                Error(WorkStatefulReadFailure.IncompleteAuthority ex.Message)
            | :? ArgumentException as ex ->
                Error(WorkStatefulReadFailure.OutOfScope)
