module WorkStatefulApiReadTests

open System
open System.IO
open NUnit.Framework
open Server
open Jarvis.WorkStatefulApi
open Jarvis.WorkStatefulServer

let private withStore f =
    let folder = Path.Combine(Path.GetTempPath(), "jarvis-h1a-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory folder |> ignore
    let path = Path.Combine(folder, "authority.sqlite")
    try
        let store = WorkSqliteAuthority(path)
        match store.Initialize() with
        | Ok 1 -> f store path
        | other -> Assert.Fail(sprintf "SQLite initialize failed: %A" other)
    finally
        Directory.Delete(folder, true)

let private checkedResult = function
    | Ok value -> value
    | Error failure -> Assert.Fail(sprintf "Unexpected read/commit failure: %A" failure); Unchecked.defaultof<_>

let private value body graph stage stop role source =
    { BodyJson = body
      GraphVersion = graph
      StageGeneration = stage
      ClaimGeneration = 0L
      StopGeneration = stop
      RoleVersion = role
      SourceSha = source
      ReviewSha = None
      IntegrationSha = None
      ExternalEffect = WorkSqliteEffect.NoneRecorded }

let private makeRecord project kind id body graph stage stop role source =
    { Key = { ProjectId = project; Kind = kind; EntityId = id }
      ExpectedVersion = None
      Value = value body graph stage stop role source }

let private seed (store: WorkSqliteAuthority) =
    let project name =
        let account = if name = "project-A" then "account-A" else "account-B"
        [ makeRecord name WorkSqliteRecordKind.Project "authority"
              (sprintf """{"coverage":"Verified","queueVersion":1,"accountId":"%s","roleIds":["worker"]}""" account) 2L 0L 3L 0L None
          makeRecord name WorkSqliteRecordKind.Work "same-work"
              """{"kind":"Feature","title":"Private project work","state":"Open","proposalState":"Open","approvalReceipt":"decision-1","requiredForParent":true}"""
              2L 0L 3L 0L None
          makeRecord name WorkSqliteRecordKind.Stage "same-stage"
              """{"nodeId":"same-work","kind":"Implementation","state":"Available","proposalState":"Open","gate":"Clear","queueId":"implementation","availableSequence":7}"""
              2L 4L 3L 0L (Some(String.replicate 40 "a"))
          makeRecord name WorkSqliteRecordKind.Role "worker"
              """{"state":"Active","queues":["implementation"]}"""
              2L 0L 3L 2L None ]
    store.Commit
        { OperationId = Guid.NewGuid()
          Guards = []
          Records = project "project-A" @ project "project-B"
          Capacities =
              [ { AccountId = "account-A"
                  ExpectedVersion = None
                  Maximum = Some 2
                  ReservedDelta = 1 }
                { AccountId = "account-B"
                  ExpectedVersion = None
                  Maximum = Some 7
                  ReservedDelta = 6 } ] }
    |> checkedResult |> ignore

let private grant dbPath =
    { Authenticated = true
      SubjectId = "authenticated-subject"
      AccountId = "account-A"
      AuthorityId = "authority-A"
      BoundDatabasePath = dbPath
      AllowedProjectIds = Set.singleton "project-A"
      VerifiedRoleId = Some "worker" }

let private request project role =
    { ApiVersion = WorkReadVersion.Current
      ProjectId = project
      RoleId = role }

[<Test>]
let two_projects_same_local_ids_never_cross_authorized_scope () =
    withStore (fun store path ->
        seed store
        let before = store.CurrentSequence() |> checkedResult
        let result =
            WorkStatefulApiRead.readSnapshot store (grant path) (request "project-A" (Some "worker"))
            |> checkedResult
        let after = store.CurrentSequence() |> checkedResult
        Assert.That(after, Is.EqualTo before, "Read-only H1a must not consume an authority mutation sequence")
        Assert.That(result.ProjectId, Is.EqualTo "project-A")
        Assert.That(result.AuthorityId, Is.EqualTo "authority-A")
        Assert.That(result.Revision, Is.EqualTo before)
        Assert.That(result.Stages |> List.forall (fun stage -> stage.ProjectId = "project-A"), Is.True)
        Assert.That(result.Nodes |> List.forall (fun node -> node.ProjectId = "project-A"), Is.True)
        Assert.That(result.Stages.Head.SourceCommit, Is.EqualTo(Some(String.replicate 40 "a")))
        Assert.That(result.Capacity.Value.Occupied, Is.EqualTo 1)
        Assert.That(result.Capacity.Value.Limit, Is.EqualTo 2)
        Assert.That(result.VerifiedRoleId, Is.EqualTo(Some "worker"))
        Assert.That(result.Roles.Head.Queues, Is.EqualTo(Some [ "implementation" ]))
        Assert.That(result.RecoveryUnknownReason, Does.Contain("not integrated"))
        Assert.That(result.Diagnostics, Has.Some.Contains("not attested"))
        match WorkStatefulApiRead.readSnapshot store (grant path) (request "project-B" (Some "worker")) with
        | Error WorkStatefulReadFailure.OutOfScope -> ()
        | other -> Assert.Fail(sprintf "Cross-project read must fail: %A" other)
        match WorkStatefulApiRead.readSnapshot store (grant path) (request "project-A" (Some "reviewer")) with
        | Error WorkStatefulReadFailure.OutOfScope -> ()
        | other -> Assert.Fail(sprintf "Caller role must not override trusted grant: %A" other))

[<Test>]
let unauthenticated_or_wrong_database_bound_grant_fails_without_source_probing () =
    withStore (fun store path ->
        seed store
        let invalid = { grant path with Authenticated = false }
        match WorkStatefulApiRead.readSnapshot store invalid (request "project-A" None) with
        | Error WorkStatefulReadFailure.Unauthenticated -> ()
        | other -> Assert.Fail(sprintf "Unauthenticated read must fail: %A" other)
        let wrong = { grant path with BoundDatabasePath = path + ".other" }
        match WorkStatefulApiRead.readSnapshot store wrong (request "project-A" (Some "worker")) with
        | Error WorkStatefulReadFailure.OutOfScope -> ()
        | other -> Assert.Fail(sprintf "Database instance binding must fail: %A" other)
        let roleless = { grant path with VerifiedRoleId = None }
        let response = WorkStatefulApiRead.readSnapshot store roleless (request "project-A" None)
                       |> checkedResult
        Assert.That(response.Roles, Is.Empty))

[<Test>]
let no_verified_project_coverage_or_missing_database_is_not_a_successful_snapshot () =
    withStore (fun store path ->
        store.Commit
            { OperationId = Guid.NewGuid()
              Guards = []
              Records =
                  [ makeRecord "project-A" WorkSqliteRecordKind.Project "authority"
                        """{"coverage":"Unknown","accountId":"account-A"}""" 1L 0L 0L 0L None ]
              Capacities = [] } |> checkedResult |> ignore
        match WorkStatefulApiRead.readSnapshot store (grant path) (request "project-A" (Some "worker")) with
        | Error(WorkStatefulReadFailure.IncompleteAuthority _) -> ()
        | other -> Assert.Fail(sprintf "Incomplete importer coverage must fail: %A" other)
        let missing = Path.Combine(Path.GetDirectoryName path, "missing.sqlite")
        let missingStore = WorkSqliteAuthority(missing)
        match WorkStatefulApiRead.readSnapshot missingStore (grant missing) (request "project-A" (Some "worker")) with
        | Error(WorkStatefulReadFailure.StorageUnavailable _) -> ()
        | other -> Assert.Fail(sprintf "Uninitialized database must fail: %A" other)
        Assert.That(File.Exists missing, Is.False, "Read-only adapter must not create a database"))

[<Test>]
let selected_account_is_from_grant_not_client_request () =
    withStore (fun store path ->
        seed store
        let grantB = { grant path with AccountId = "account-B" }
        match WorkStatefulApiRead.readSnapshot store grantB (request "project-A" (Some "worker")) with
        | Error WorkStatefulReadFailure.OutOfScope -> ()
        | other -> Assert.Fail(sprintf "Wrong account project-instance binding must fail: %A" other)
        let response = WorkStatefulApiRead.readSnapshot store (grant path) (request "project-A" (Some "worker")) |> checkedResult
        Assert.That(response.Capacity.Value.Limit, Is.EqualTo 2)
        Assert.That(response.Capacity.Value.Occupied, Is.EqualTo 1)
        let noAccount = { grant path with AccountId = "unknown-account" }
        match WorkStatefulApiRead.readSnapshot store noAccount (request "project-A" (Some "worker")) with
        | Error WorkStatefulReadFailure.OutOfScope -> ()
        | other -> Assert.Fail(sprintf "Unknown account cannot borrow project authorization: %A" other))
