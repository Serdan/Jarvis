module Client.Tests.WorkQueueSchedulerHostPreviewTests

open System
open NUnit.Framework
open Jarvis.WorkQueues
open Jarvis.WorkQueues.Contracts
open Client.LedgerStore
open Client.WorkQueueGitLedger
open Client.WorkQueueRoleAuthority
open Client.AccountCapacityLedger
open Client.WorkQueueSchedulerHostPreview

[<TestFixture>]
type WorkQueueSchedulerHostPreviewTests() =
    let projectId = "11111111-1111-4111-8111-111111111111"
    let projectHead = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
    let accountHead = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"

    let role: RoleConfiguration =
        { RoleId = "Worker"; Version = 4; Instructions = "Read your inbox."
          QueueSubscriptions = set [ "implementation" ]; Enabled = true }

    let stage: WorkStage =
        { Id = "work-1"; ProjectId = projectId; SubjectId = "task-1"
          Kind = WorkKind.Implementation; QueueId = "implementation"; StageGeneration = 2
          State = WorkState.Available; Domain = DomainState.Undecided; Gate = GateState.Clear
          ClaimGeneration = 0; Dependencies = Set.empty; WaivedDependencies = Map.empty
          AvailableAt = 1L; AvailableSequence = 1L; SourceLineage = None
          Push = None; IntegrationAuthorized = false }

    let empty = { emptyProject (Map.ofList [ "Worker", role.QueueSubscriptions ]) with
                    QueueCovered = true; GraphCovered = true
                    MessageCovered = true; SourceCovered = true
                    Works = Map.ofList [ stage.Id, stage ] }

    let catalog: RoleCatalog =
        { Revision = 1; Covered = true; Current = Map.ofList [ "Worker", 4 ]
          Versions = Map.ofList [ "Worker", Map.ofList [ 4, role ] ]
          Receipts = Map.empty }

    let pinned project =
        { Project = project; Catalog = catalog
          Proof =
            { ProjectId = projectId; LedgerId = "22222222-2222-4222-8222-222222222222"
              Epoch = 1; Head = projectHead; ProjectBlobOid = "project-oid"
              ProjectVersion = project.Version; CatalogBlobOid = "role-oid"
              CatalogRevision = 1; ProvisionOperationId = "role-init" } }

    let account held =
        { Head = accountHead
          State =
            { SchemaVersion = 1; AccountId = "33333333-3333-4333-8333-333333333333"
              LedgerId = "44444444-4444-4444-8444-444444444444"
              Epoch = 1; MigrationPhase = "Activated"; StateVersion = 1L
              ConfigVersion = 1L; MaxConcurrentAgents = 1; NextSequence = 1L
              Tokens =
                if not held then [||] else
                    [| { LaunchId = "55555555-5555-4555-8555-555555555555"
                         ProjectId = projectId
                         ProjectLedgerId = "22222222-2222-4222-8222-222222222222"
                         ProjectLedgerEpoch = 1
                         ProjectAuthoritySha256 = String.replicate 64 "a"
                         RoleId = "Worker"; RoleConfigVersion = 4L
                         AdmissionOperationId = "77777777-7777-4777-8777-777777777777"
                         Status = "Held"; ReleaseProof = Unchecked.defaultof<_> } |] } }

    let gitSnapshot =
        { Commit = projectHead; Header = Unchecked.defaultof<LedgerHeader> }

    let canonicalOrigin: CanonicalWorkOrigin =
        { SchemaVersion = 1; ProjectId = projectId; WorkId = stage.Id
          SourceHead = projectHead; SourceHighWater = 9L
          SourceOperationId = "source-operation-1"; SourceOperationSequence = 4L
          SourceOperationKind = "ImplementationReady"; SourceOperationActor = "fixture"
          SourceRequestHash = "source-request-hash"; SourceOriginHead = projectHead
          SourceEntityPath = "tasks/task-1/task.json"; SourceEntityBlobOid = "source-blob"
          SourceEntitySha256 = "source-content-hash"; SourceSelector = "ImplementationReady"
          SourceRef = "refs/heads/task-1"; SourceCommit = "1234567890abcdef1234567890abcdef12345678"
          RoleId = "Worker"; RoleVersion = 4
          AccountCreditStatus = "NoCredit"; Ambiguity = "" }

    let ports project held (readOriginal: string -> Result<OriginalBrowserJournalEntry option,string>) : SchedulerHostPreviewPorts =
        { ReadAccount = fun () -> Ok(account held)
          ReadProject = fun () -> Ok(gitSnapshot, pinned project)
          ReadOrigin = fun _ _ -> Ok None
          ReadAdmission = fun _ _ -> Ok None
          ReadOriginal = readOriginal }

    [<Test>]
    member _.PreviewOnTwoClientsNeverReservesOrSubmits() =
        let mutable reads = 0
        let passive () = ports empty false (fun _ -> reads <- reads + 1; Ok None)
        let first = WorkQueueSchedulerHostPreview.previewWith (passive ()) "Worker"
        let second = WorkQueueSchedulerHostPreview.previewWith (passive ()) "Worker"
        Assert.That(first.CanDispatch, Is.False)
        Assert.That(second.CanDispatch, Is.False)
        Assert.That(first.Mode, Is.EqualTo("ReadOnlyDryRun"))
        Assert.That(first.MaxConcurrentAgents, Is.EqualTo(Some 1))
        Assert.That(first.AvailableSlots, Is.EqualTo(Some 1))
        Assert.That(first.ProjectHead, Is.EqualTo(Some projectHead))
        Assert.That(first.SelectedRoleVersion, Is.EqualTo(Some 4))
        Assert.That(first.WouldLaunch |> Option.map _.WorkId, Is.EqualTo(Some stage.Id))
        Assert.That(first.Issues |> List.exists (fun e -> e.Code = "SourceReceiptMissing"), Is.True)
        Assert.That(first.Issues |> List.exists (fun e -> e.Code = "DispatchDisabled"), Is.True)
        Assert.That(second.WouldLaunch, Is.EqualTo(first.WouldLaunch))
        Assert.That(reads, Is.Zero)

    [<Test>]
    member _.RecordedSourceShaAndReceiptAreVisibleWithoutPromotingThemToAuthority() =
        let sha = "1234567890abcdef1234567890abcdef12345678"
        let origin = { canonicalOrigin with SourceCommit = sha }
        let original = ports empty false (fun _ -> Ok None)
        let withOrigin =
            { original with
                ReadOrigin = fun _ workId ->
                    if workId = stage.Id then Ok(Some(origin, "immutable-origin-blob"))
                    else Error "unexpected work identity" }
        let report = WorkQueueSchedulerHostPreview.previewWith withOrigin "Worker"
        let chosen = report.WouldLaunch |> Option.get
        Assert.That(chosen.SourceCommit, Is.EqualTo(Some sha))
        Assert.That(chosen.SourceRef, Is.EqualTo(Some "refs/heads/task-1"))
        Assert.That(chosen.SourceOperationId, Is.EqualTo(Some "source-operation-1"))
        Assert.That(chosen.SourceRequestHash, Is.EqualTo(Some "source-request-hash"))
        Assert.That(chosen.OriginBlobOid, Is.EqualTo(Some "immutable-origin-blob"))
        Assert.That(report.CanDispatch, Is.False)

    [<Test>]
    member _.RegisteredProviderSessionIsNotPrintedInReadOnlyPreview() =
        let launchId = "55555555-5555-4555-8555-555555555555"
        let registered: Launch =
            { Id = launchId; ProjectId = projectId; RoleId = "Worker"; RoleVersion = 4
              AccountTokenId = "token:" + launchId
              State = LaunchState.Registered("private-agent-uuid", "private-provider-session-key")
              Lease = Some { LastSeenAt = 10L; Generation = 1; StopRequested = false }
              Version = 2; Credit = Some stage.Id; PrimaryWorkId = None
              EverClaimed = false; AncillaryMessages = Set.empty; DispatchCount = 1 }
        let report = WorkQueueSchedulerHostPreview.previewWith
                         (ports { empty with Launches = Map.ofList [launchId, registered] }
                             true (fun _ -> Error "Browser reader intentionally absent")) "Worker"
        Assert.That(report.ExistingLaunches.Length, Is.EqualTo(1))
        Assert.That(report.ExistingLaunches.Head.State, Is.EqualTo("Registered"))
        Assert.That(report.ExistingLaunches.Head.State, Does.Not.Contain("private-provider-session-key"))
        Assert.That(report.Issues |> List.exists (fun issue -> issue.Code = "BrowserJournalUnknown"), Is.True)

    [<Test>]
    member _.FullAccountCapacityIsVisibleWithoutAnotherSubmission() =
        let report = WorkQueueSchedulerHostPreview.previewWith
                         (ports empty true (fun _ -> failwith "No live submission or browser read allowed")) "Worker"
        Assert.That(report.OccupiedAgents, Is.EqualTo(Some 1))
        Assert.That(report.AvailableSlots, Is.EqualTo(Some 0))
        Assert.That(report.Issues |> List.exists (fun issue -> issue.Code = "CapacityFull"), Is.True)
        Assert.That(report.CanDispatch, Is.False)

    [<Test>]
    member _.PreviouslyUnknownBrowserResultIncludesOriginalIdentity() =
        let launchId = "55555555-5555-4555-8555-555555555555"
        let originalOp = "browser-original-123"
        let launch: Launch =
            { Id = launchId; ProjectId = projectId; RoleId = "Worker"; RoleVersion = 4
              AccountTokenId = "token:" + launchId; State = LaunchState.SubmittedUnregistered DispatchState.OutcomeUnknown
              Lease = None; Version = 1; Credit = Some stage.Id; PrimaryWorkId = None
              EverClaimed = false; AncillaryMessages = Set.empty; DispatchCount = 1 }
        let browser: OriginalBrowserJournalEntry =
            { Intent =
                { LaunchId = launchId; ProjectId = projectId; WorkId = stage.Id
                  WorkGeneration = 2; RoleId = "Worker"; RoleVersion = 4
                  RequestFingerprint = "f"; OperationId = originalOp
                  Provenance =
                    { AccountHead = accountHead; ProjectHead = projectHead
                      SourceRef = None; SourceCommit = None } }
              Outcome = Some(OriginalBrowserOutcome.Unknown "transport timed out") }
        let withLaunch = { empty with Launches = Map.ofList [ launchId, launch ] }
        let report = WorkQueueSchedulerHostPreview.previewWith
                         (ports withLaunch true (fun id -> if id = launchId then Ok(Some browser) else Ok None))
                         "Worker"
        Assert.That(report.ExistingLaunches.Length, Is.EqualTo(1))
        let existing = report.ExistingLaunches.Head
        Assert.That(existing.OriginalBrowserOperationId, Is.EqualTo(Some originalOp))
        Assert.That(existing.OriginalBrowserOutcome, Does.Contain("transport timed out"))
        Assert.That(report.Issues |> List.exists (fun issue -> issue.Code = "AdmissionReceiptMissing"), Is.True)
        Assert.That(report.CanDispatch, Is.False)

    [<Test>]
    member _.UnreachableGitAuthorityRemainsAnActionableUnknown() =
        let disconnected =
            { ports empty false (fun _ -> Ok None) with
                ReadAccount = fun () -> Error "account remote unavailable"
                ReadProject = fun () -> Error "project checkout stale" }
        let report = WorkQueueSchedulerHostPreview.previewWith disconnected "Worker"
        Assert.That(report.WouldLaunch, Is.EqualTo(None))
        Assert.That(report.AccountHead, Is.EqualTo(None))
        Assert.That(report.ProjectHead, Is.EqualTo(None))
        Assert.That(report.Issues |> List.exists (fun issue ->
            issue.Code = "AccountUnknown" && issue.NextAction.Contains("account remote unavailable")), Is.True)
        Assert.That(report.Issues |> List.exists (fun issue ->
            issue.Code = "ProjectUnknown" && issue.NextAction.Contains("project checkout stale")), Is.True)

    [<Test>]
    member _.SourceOriginIdentityConflictIsVisibleWithoutTrustingBorrowedReceipt() =
        let mismatched = { canonicalOrigin with ProjectId = "wrong-project"; WorkId = "wrong-work" }
        let original = ports empty false (fun _ -> Ok None)
        let withOrigin = { original with ReadOrigin = fun _ _ -> Ok(Some(mismatched, "unexpected-source-oid")) }
        let report = WorkQueueSchedulerHostPreview.previewWith withOrigin "Worker"
        Assert.That(report.Issues |> List.exists (fun e ->
            e.Code = "SourceReceiptIdentityConflict"
            && e.Record = stage.Id), Is.True)
        Assert.That(report.CanDispatch, Is.False)
        let selected = report.WouldLaunch |> Option.get
        Assert.That(selected.SourceCommit, Is.EqualTo(None))
        Assert.That(selected.SourceOperationId, Is.EqualTo(None))
        Assert.That(selected.SourceRequestHash, Is.EqualTo(None))
        Assert.That(selected.OriginBlobOid, Is.EqualTo(None))

    [<Test>]
    member _.AccountTokenStatusMatchesExistingLaunchWithoutChangingGlobalCapacity() =
        let launchId = "55555555-5555-4555-8555-555555555555"
        let originalAdmission = "77777777-7777-4777-8777-777777777777"
        let launch: Launch =
            { Id = launchId; ProjectId = projectId; RoleId = "Worker"; RoleVersion = 4
              AccountTokenId = originalAdmission; State = LaunchState.ReservedUnregistered
              Lease = None; Version = 0; Credit = None; PrimaryWorkId = None
              EverClaimed = false; AncillaryMessages = Set.empty; DispatchCount = 0 }
        let project = { empty with Launches = Map.ofList [ launchId, launch ] }
        let original = ports project true (fun _ -> Ok None)
        let report = WorkQueueSchedulerHostPreview.previewWith original "Worker"
        Assert.That(report.ExistingLaunches.Head.AccountTokenStatus, Is.EqualTo("Held"))
        Assert.That(report.Issues |> List.exists (fun e ->
            e.Code = "AccountTokenMissing" || e.Code = "AccountTokenIdentityConflict"), Is.False)
        Assert.That(report.AvailableSlots, Is.EqualTo(Some 0))
        Assert.That(report.CanDispatch, Is.False)

        let originalAccount = account true
        let wrongTokens =
            originalAccount.State.Tokens
            |> Array.map (fun t -> { t with ProjectLedgerId = "different-ledger" })
        let wrongState = { originalAccount.State with Tokens = wrongTokens }
        let mismatched = { originalAccount with State = wrongState }
        let wrongLedger = WorkQueueSchedulerHostPreview.previewWith
                              { original with ReadAccount = fun () -> Ok mismatched } "Worker"
        Assert.That(wrongLedger.ExistingLaunches.Head.AccountTokenStatus, Is.EqualTo("IdentityConflict"))
        Assert.That(wrongLedger.Issues |> List.exists (fun e ->
            e.Code = "AccountTokenIdentityConflict"), Is.True)

        let releasedTokens =
            originalAccount.State.Tokens
            |> Array.map (fun t -> { t with Status = "Released" })
        let releasedState = { originalAccount.State with Tokens = releasedTokens }
        let released = { originalAccount with State = releasedState }
        let afterRelease = WorkQueueSchedulerHostPreview.previewWith
                               { original with ReadAccount = fun () -> Ok released } "Worker"
        Assert.That(afterRelease.ExistingLaunches.Head.AccountTokenStatus, Is.EqualTo("Released"))
        Assert.That(afterRelease.Issues |> List.exists (fun e ->
            e.Code = "AccountTokenReleasedWhileActive"), Is.True)
        Assert.That(afterRelease.CanDispatch, Is.False)

    [<Test>]
    member _.MissingAccountTokenAndDifferentRoleHistoryAreNotSilentlyConflated() =
        let launchId = "not-reserved"
        let launch: Launch =
            { Id = launchId; ProjectId = projectId; RoleId = "Worker"; RoleVersion = 4
              AccountTokenId = "original-admission"; State = LaunchState.ReservedUnregistered
              Lease = None; Version = 0; Credit = None; PrimaryWorkId = None
              EverClaimed = false; AncillaryMessages = Set.empty; DispatchCount = 0 }
        let otherRole =
            [ 1 .. 65 ]
            |> List.map (fun n ->
                let id = "reviewer-history-" + string n
                id, { launch with Id = id; RoleId = "Reviewer" })
        let project = { empty with Launches = Map.ofList ((launchId, launch) :: otherRole) }
        let report = WorkQueueSchedulerHostPreview.previewWith
                         (ports project false (fun _ -> Ok None)) "Worker"
        Assert.That(report.ExistingLaunches.Length, Is.EqualTo(1))
        Assert.That(report.ExistingLaunches.Head.AccountTokenStatus, Is.EqualTo("Missing"))
        Assert.That(report.Issues |> List.exists (fun e -> e.Code = "AccountTokenMissing"), Is.True)
        Assert.That(report.Issues |> List.exists (fun e -> e.Code = "LaunchListTruncated"), Is.False)
        Assert.That(report.CanDispatch, Is.False)

    [<Test>]
    member _.ConflictingAdmissionAndBrowserWitnessesExposeOriginalIdsForRepair() =
        let launchId = "55555555-5555-4555-8555-555555555555"
        let admissionOp = "foreign-admission-operation"
        let browserOp = "original-browser-operation"
        let launch: Launch =
            { Id = launchId; ProjectId = projectId; RoleId = "Worker"; RoleVersion = 4
              AccountTokenId = "77777777-7777-4777-8777-777777777777"
              State = LaunchState.SubmittedUnregistered DispatchState.OutcomeUnknown
              Lease = None; Version = 1; Credit = Some stage.Id; PrimaryWorkId = None
              EverClaimed = false; AncillaryMessages = Set.empty; DispatchCount = 1 }
        let foreignAdmission: ProjectAdmissionRecord =
            { SchemaVersion = 1; ProjectId = "foreign-project"; LaunchId = "foreign-launch"
              AccountTokenId = "foreign-token"; AdmissionOperationId = admissionOp
              Kind = "AdmissionWon"; AbortOperationId = "" }
        let foreignBrowser: OriginalBrowserJournalEntry =
            { Intent =
                { LaunchId = "foreign-launch"; ProjectId = projectId; WorkId = stage.Id
                  WorkGeneration = 2; RoleId = "Worker"; RoleVersion = 4
                  RequestFingerprint = "original-fingerprint"; OperationId = browserOp
                  Provenance =
                    { AccountHead = accountHead; ProjectHead = projectHead
                      SourceRef = None; SourceCommit = None } }
              Outcome = Some(OriginalBrowserOutcome.Unknown "original timeout") }
        let withLaunch = { empty with Launches = Map.ofList [ launchId, launch ] }
        let original = ports withLaunch true (fun _ -> Ok(Some foreignBrowser))
        let withWitness =
            { original with
                ReadAdmission = fun _ _ -> Ok(Some(foreignAdmission, "foreign-blob")) }
        let report = WorkQueueSchedulerHostPreview.previewWith withWitness "Worker"
        let observed = report.ExistingLaunches.Head
        Assert.That(observed.AdmissionOperationId, Is.EqualTo(Some admissionOp))
        Assert.That(observed.AdmissionKind, Is.EqualTo(Some "IdentityConflict: AdmissionWon"))
        Assert.That(observed.OriginalBrowserOperationId, Is.EqualTo(Some browserOp))
        Assert.That(observed.OriginalBrowserOutcome, Is.EqualTo("IdentityConflict"))
        Assert.That(report.Issues |> List.exists (fun issue ->
            issue.Code = "AdmissionReceiptIdentityConflict"
            && issue.NextAction.Contains(admissionOp)), Is.True)
        Assert.That(report.Issues |> List.exists (fun issue ->
            issue.Code = "BrowserIdentityConflict"
            && issue.NextAction.Contains(browserOp)), Is.True)
        Assert.That(report.CanDispatch, Is.False)
