module WorkLegacyImportProjectionTests

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open Jarvis.WorkQueues
open Jarvis.WorkQueues.Contracts
open Server

let private shaA = String.replicate 40 "a"
let private shaB = String.replicate 40 "b"
let private digest = String.replicate 64 "e"
let private projectOne = Guid.Parse("11111111-1111-4111-8111-111111111111")
let private projectTwo = Guid.Parse("22222222-2222-4222-8222-222222222222")
let private ledgerOne = Guid.Parse("33333333-3333-4333-8333-333333333333")
let private ledgerTwo = Guid.Parse("44444444-4444-4444-8444-444444444444")
let private accountId = Guid.Parse("55555555-5555-4555-8555-555555555555")
let private accountLedger = Guid.Parse("66666666-6666-4666-8666-666666666666")
let private deviceId = Guid.Parse("77777777-7777-4777-8777-777777777777")
let private fenceOperationId = Guid.Parse("88888888-8888-4888-8888-888888888888")
let private importOperationId = Guid.Parse("99999999-9999-4999-8999-999999999999")
let private approvalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")
let private exclusiveId = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb")
let private approvalReceipt = Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc")
let private roleReceipt = Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd")

let private ok = function
    | Ok value -> value
    | Error why -> Assert.Fail(sprintf "Unexpected failure: %A" why); Unchecked.defaultof<_>

let private head =
    { PinnedCommit = shaA
      ObservedCommit = shaA
      AncestryVerified = true
      HistoryComplete = true
      SourceHighWater = 1L
      ObservedHighWater = 1L }

let private operation =
    { OperationId = approvalReceipt
      Sequence = 1L
      RequestSha256 = digest
      ResultSha256 = digest
      Kind = "verified-ledger-operation"
      TerminalResult = None }

let private project (projectId: Guid) (ledgerId: Guid) workId =
    let stage =
        { Id = workId
          ProjectId = projectId.ToString("D")
          SubjectId = "task-" + workId
          Kind = WorkKind.Implementation
          QueueId = "implementation"
          StageGeneration = 1
          State = WorkState.Available
          Domain = DomainState.Undecided
          Gate = GateState.Clear
          ClaimGeneration = 0
          Dependencies = Set.empty
          WaivedDependencies = Map.empty
          AvailableAt = 1L
          AvailableSequence = 1L
          SourceLineage = None
          Push = None
          IntegrationAuthorized = false }
    { ProjectId = projectId
      LedgerId = ledgerId
      LedgerEpoch = 1
      Head = head
      Queue =
        { emptyProject (Map.ofList [ "Worker", Set.singleton "implementation" ]) with
            Migration = MigrationEpoch.Frozen
            Works = Map.ofList [ workId, stage ] }
      Graph = WorkComposition.empty
      Origins =
        [ { WorkId = workId
            StageGeneration = 1
            SubjectId = stage.SubjectId
            SourceHead = shaA
            SourceHighWater = 1L
            SourceAncestryVerified = true
            RepositoryId = "trusted-test-repository"
            SourceRef = "refs/heads/verified-source"
            SourceCommit = shaB
            RoleId = "Worker"
            RoleVersion = 1
            GraphVersion = 0
            ContextDigest = digest } ]
      Reviews = []
      Integrations = []
      Terminals = []
      Validations = []
      Operations = [ operation ] }

let private input () =
    { Account =
        { AccountId = accountId
          LedgerId = accountLedger
          LedgerEpoch = 1
          Head = head
          MaxConcurrent = 1
          Tokens = []
          Operations = [ operation ] }
      Projects = [ project projectOne ledgerOne "I1"; project projectTwo ledgerTwo "I2" ] }

let private binding scope entity ledger =
    { Scope = scope
      EntityId = entity
      LedgerId = ledger
      LedgerEpoch = 1
      DeviceId = deviceId
      TrustedRemoteId = "authenticated-test-remote:" + ledger.ToString("D")
      PinnedHead = shaA
      SourceHighWater = 1L }

let private plan () =
    { Account = binding WorkLegacyGitScope.Account accountId accountLedger
      Projects =
        [ binding WorkLegacyGitScope.Project projectOne ledgerOne
          binding WorkLegacyGitScope.Project projectTwo ledgerTwo ]
      Approval =
        { DecisionId = approvalId
          DecisionVersion = 1L
          DeviceId = deviceId
          AccountId = accountId
          ProjectIds = [ projectOne; projectTwo ]
          FenceOperationId = fenceOperationId
          ImportOperationId = importOperationId } }

// Source-backed C2 verifier double. No production Git privileges or grants.
// Each old writer is fenced at the shared barrier before its Git CAS.
type private SourceGateway(accepted: WorkLegacyFencePlan) =
    let current = Dictionary<Guid,string>()
    let committed = Dictionary<Guid,WorkLegacyGitFenceReceipt>()
    let mutable quiesced = false
    do
        for b in accepted.Account :: accepted.Projects do current.[b.LedgerId] <- b.PinnedHead
    let fencedHead (b: WorkLegacyGitBinding) =
        SHA256.HashData(Encoding.UTF8.GetBytes(b.LedgerId.ToString("D") + accepted.Approval.FenceOperationId.ToString("D")))
        |> Convert.ToHexString |> fun x -> x.ToLowerInvariant().Substring(0,40)
    member _.IsQuiesced = quiesced
    member _.MutateOriginal (ledger: Guid) =
        if quiesced then false else
            current.[ledger] <- String.replicate 40 "f"
            true
    interface IWorkLegacyOriginalGitFenceGateway with
        member _.VerifyOperatorDecision approval =
            if approval = accepted.Approval then Ok approval else Error "operator decision mismatch"
        member _.ReadTrustedBinding b =
            if current.ContainsKey b.LedgerId
               && (current.[b.LedgerId] = b.PinnedHead
                   || (committed.ContainsKey b.LedgerId
                       && current.[b.LedgerId] = committed.[b.LedgerId].FencedHead)) then Ok b
            else Error "original head changed"
        member _.AcquireExclusiveBarrier approval =
            if approval <> accepted.Approval then Error "decision changed"
            else
                quiesced <- true
                Ok exclusiveId
        member _.CasFenceOriginalWriter(b, approval, exclusive) =
            if not quiesced || approval <> accepted.Approval || exclusive <> exclusiveId then
                Error "old-writer barrier missing"
            elif committed.ContainsKey b.LedgerId then Ok committed.[b.LedgerId]
            elif current.[b.LedgerId] <> b.PinnedHead then Error "original head advanced"
            else
                let receipt =
                    { Binding = b
                      DecisionId = approval.DecisionId
                      DecisionVersion = approval.DecisionVersion
                      FenceOperationId = approval.FenceOperationId
                      ExclusiveReceiptId = exclusive
                      OriginalHead = b.PinnedHead
                      FencedHead = fencedHead b
                      OriginalHighWater = b.SourceHighWater
                      FenceHighWater = b.SourceHighWater + 1L
                      WriterGeneration = 1L
                      NoNewOriginalMutations = true
                      DurableUntilExplicitCutover = true
                      OriginalPendingIntentHighWater = 1L
                      OriginalSourceEffectHighWater = 1L
                      MinimumClientProtocolVersion = 1
                      AllOriginalClientsQuiesced = true
                      AllClientQuiescenceProofSha256 = digest
                      AuditSha256 = digest }
                committed.[b.LedgerId] <- receipt
                current.[b.LedgerId] <- receipt.FencedHead
                Ok receipt
        member _.VerifyDurableFence(b,receipt) =
            if quiesced && committed.ContainsKey b.LedgerId
               && committed.[b.LedgerId] = receipt
               && current.[b.LedgerId] = receipt.FencedHead then Ok receipt
            else Error "original fence cannot be independently reverified"

type private CaptureVerifier(evidence: WorkLegacyProjectionEvidence option) =
    interface IWorkLegacyProjectionVerifier with
        member _.Verify(_,_) =
            match evidence with
            | Some proof -> Ok proof
            | None -> Error "no independently authenticated proposal/role/catalog/STOP capture"

let private evidence (inspection: WorkLegacyImportInspection)
                     (receipt: WorkLegacyFencedOfflineReceipt) =
    { AccountId = inspection.Account.AccountId
      AccountLedgerId = inspection.Account.LedgerId
      AccountHead = inspection.Account.Head.PinnedCommit
      DeviceId = deviceId
      ApprovalId = receipt.ApprovalId
      ImportOperationId = receipt.Import.ImportOperationId
      Maximum = inspection.Account.MaxConcurrent
      Reserved = 0
      Projects =
        [ for p in inspection.Projects do
            yield
                { ProjectId = p.ProjectId
                  LedgerId = p.LedgerId
                  SourceHead = p.Head.PinnedCommit
                  StopGeneration = 0L
                  Decisions =
                    [ for KeyValue(id, _) in p.Queue.Works do
                        { WorkId = id
                          State = "Open"
                          DecisionReceiptId = approvalReceipt
                          DecisionVersion = 1L
                          SourceHead = p.Head.PinnedCommit } ]
                  Roles =
                    [ for KeyValue(role, queues) in p.Queue.Subscriptions do
                        { RoleId = role
                          Version = 1
                          Queues = queues
                          InstructionsDigest = digest
                          EvidenceReceiptId = roleReceipt
                          SourceHead = p.Head.PinnedCommit } ]
                  OptionalUnitIds = Set.empty } ] }

let private disposable action =
    let folder = Path.Combine(Path.GetTempPath(), "jarvis-work-c1-projection-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory folder |> ignore
    File.WriteAllText(Path.Combine(folder, WorkLegacyImportSqlite.DisposableMarker),
                      WorkLegacyImportSqlite.DisposableMarkerContent)
    let db = Path.Combine(folder, "work.sqlite")
    try action db
    finally if Directory.Exists folder then Directory.Delete(folder,true)

let private withImport action =
    disposable (fun path ->
        let raw = input ()
        let gateway = SourceGateway(plan ())
        let c2 = WorkLegacyImportFence.commitFencedOfflineFixture
                     (gateway :> IWorkLegacyOriginalGitFenceGateway) path raw (plan ()) |> ok
        Assert.That(gateway.IsQuiesced, Is.True)
        Assert.That(gateway.MutateOriginal ledgerOne, Is.False)
        action path raw c2 (evidence raw c2))

let private projectEvidence p (proof: WorkLegacyProjectionEvidence) =
    { proof with Projects = proof.Projects |> List.map (fun x -> if x.ProjectId = projectOne then p x else x) }

let private projectCommit path raw c2 proof operationId =
    let verifier = CaptureVerifier(Some proof) :> IWorkLegacyProjectionVerifier
    WorkLegacyImportProjection.commitOfflineFixture verifier path raw c2 operationId

let private tamperImportedJson path kind projectId entityId change =
    let store = WorkSqliteAuthority(path)
    let key = { ProjectId = projectId; Kind = kind; EntityId = entityId }
    let original = store.ReadRecord(key) |> ok |> Option.get
    let json = System.Text.Json.Nodes.JsonNode.Parse(original.Value.BodyJson).AsObject()
    change json
    store.Commit
        { OperationId = Guid.NewGuid()
          Guards = []
          Records =
            [ { Key = key
                ExpectedVersion = Some original.Version
                Value = { original.Value with BodyJson = json.ToJsonString() } } ]
          Capacities = [] }
    |> ok |> ignore

let private assertIncompleteWithoutProjection path raw c2 proof detail =
    let store = WorkSqliteAuthority(path)
    let before = store.CurrentSequence() |> ok
    match projectCommit path raw c2 proof (Guid.NewGuid()) with
    | Error(WorkLegacyProjectionGap.Incomplete _) -> ()
    | other -> Assert.Fail(sprintf "Unsealed %s promoted to authority: %A" detail other)
    Assert.That(store.CurrentSequence() |> ok, Is.EqualTo before)

[<Test>]
let c1_c2_e0_e1a_disposable_two_project_cap_one_positive_chain () =
    withImport (fun path raw c2 proof ->
        let store = WorkSqliteAuthority(path)
        let projected = projectCommit path raw c2 proof (Guid.NewGuid()) |> ok
        Assert.That(projected.Snapshot.Projects, Has.Length.EqualTo 2)
        Assert.That(projected.Snapshot.CompleteProjectSet, Is.True)
        Assert.That(projected.Snapshot.Account.Maximum, Is.EqualTo 1)
        Assert.That(projected.Snapshot.Account.Reserved, Is.Zero)
        Assert.That((store.ReadCapacity(accountId.ToString("D")) |> ok |> Option.get).Version, Is.EqualTo 2L)
        for p in projected.Snapshot.Projects do
            Assert.That(p.Stored.Value.BodyJson, Does.Contain("\"coverage\":\"Verified\""))
            Assert.That(p.RoleRecords.Head.Stored.Value.BodyJson, Does.Contain("\"LegacyValue\""))
            Assert.That(p.StageRecords |> Map.forall (fun _ r -> r.Value.BodyJson.Contains("\"State\"")), Is.True)
        let launch = WorkSchedulerSqlite.reserve store projected.Snapshot (Guid.NewGuid()) "one-global-launch" |> ok
        Assert.That(launch.WorkId, Is.EqualTo "I1")
        Assert.That(launch.ProjectId, Is.EqualTo (projectOne.ToString("D")))
        Assert.That((store.ReadCapacity(accountId.ToString("D")) |> ok |> Option.get).Reserved, Is.EqualTo 1)
        let prepared = WorkSchedulerSqliteDispatch.prepare store (accountId.ToString("D"))
                           launch.ProjectId launch.LaunchId (Guid.NewGuid()) |> ok
        match WorkSchedulerSqliteDispatch.refuseExternalDispatch prepared with
        | Error _ -> ()
        | Ok () -> Assert.Fail("C2 offline projection must never dispatch a real provider")
        Assert.That((store.ReadCapacity(accountId.ToString("D")) |> ok |> Option.get).Reserved, Is.EqualTo 1)
    )

[<Test>]
let c1_projection_missing_capture_or_missing_role_digest_is_incomplete_without_any_mutation () =
    withImport (fun path raw c2 proof ->
        let store = WorkSqliteAuthority(path)
        let before = store.CurrentSequence() |> ok
        let missing = CaptureVerifier(None) :> IWorkLegacyProjectionVerifier
        match WorkLegacyImportProjection.commitOfflineFixture missing path raw c2 (Guid.NewGuid()) with
        | Error(WorkLegacyProjectionGap.Incomplete _) -> ()
        | other -> Assert.Fail(sprintf "Unverified external capture promoted to authority: %A" other)
        let noDigest =
            projectEvidence (fun p -> { p with Roles = p.Roles |> List.map (fun x -> { x with InstructionsDigest = "" }) }) proof
        match projectCommit path raw c2 noDigest (Guid.NewGuid()) with
        | Error(WorkLegacyProjectionGap.Incomplete _) -> ()
        | other -> Assert.Fail(sprintf "Missing exact role instructions digests accepted: %A" other)
        Assert.That(store.CurrentSequence() |> ok, Is.EqualTo before))

[<Test>]
let c1_projection_draft_proposed_and_wrong_device_or_source_never_eligible () =
    withImport (fun path raw c2 proof ->
        let store = WorkSqliteAuthority(path)
        for state in [ "Draft"; "Proposed" ] do
            let candidate =
                projectEvidence (fun p -> { p with Decisions = p.Decisions |> List.map (fun d -> { d with State = state }) }) proof
            match projectCommit path raw c2 candidate (Guid.NewGuid()) with
            | Error(WorkLegacyProjectionGap.Incomplete _) -> ()
            | other -> Assert.Fail(sprintf "%s incorrectly activated Work: %A" state other)
        for tampered in [
            { proof with DeviceId = Guid.NewGuid() }
            { proof with AccountHead = shaB }
            projectEvidence (fun p -> { p with SourceHead = shaB }) proof ] do
            match projectCommit path raw c2 tampered (Guid.NewGuid()) with
            | Error(WorkLegacyProjectionGap.Incomplete _) -> ()
            | other -> Assert.Fail(sprintf "Source or device identity changed without rejection: %A" other)
        Assert.That(store.ReadRecord({ProjectId=projectOne.ToString("D"); Kind=WorkSqliteRecordKind.Project; EntityId="authority"}) |> ok |> Option.isNone, Is.True))

[<Test>]
let c1_projection_sqlite_uuid_replay_two_clone_conflict_and_raw_immutability () =
    withImport (fun path raw c2 proof ->
        let store = WorkSqliteAuthority(path)
        let rawKey = { ProjectId = projectOne.ToString("D"); Kind = WorkSqliteRecordKind.Project; EntityId = "legacy-queue" }
        let rawBefore = store.ReadRecord rawKey |> ok |> Option.get
        let id = Guid.NewGuid()
        let first = projectCommit path raw c2 proof id |> ok
        Assert.That(first.Receipt.Replayed, Is.False)
        let replay = projectCommit path raw c2 proof id |> ok
        Assert.That(replay.Receipt.Replayed, Is.True)
        Assert.That(replay.Receipt.Sequence, Is.EqualTo first.Receipt.Sequence)
        let late = projectCommit path raw c2 proof (Guid.NewGuid())
        match late with
        | Error(WorkLegacyProjectionGap.Storage (WorkSqliteFailure.EntityAlreadyExists _))
        | Error(WorkLegacyProjectionGap.Storage (WorkSqliteFailure.StaleVersion _)) -> ()
        | other -> Assert.Fail(sprintf "Independent duplicate projection mutated authority: %A" other)
        let rawAfter = store.ReadRecord rawKey |> ok |> Option.get
        Assert.That(rawAfter, Is.EqualTo rawBefore)
        Assert.That(store.CurrentSequence() |> ok, Is.EqualTo first.Receipt.Sequence))

[<Test>]
let c1_projection_concurrent_independent_sqlite_clones_commit_only_one () =
    withImport (fun path raw c2 proof ->
        use ready = new ManualResetEventSlim(false)
        let contender () =
            Task.Run(fun () ->
                ready.Wait()
                projectCommit path raw c2 proof (Guid.NewGuid()))
        let first = contender()
        let second = contender()
        ready.Set()
        let results = Task.WhenAll([|first; second|]).GetAwaiter().GetResult()
        Assert.That(results |> Array.filter Result.isOk |> Array.length, Is.EqualTo 1)
        Assert.That(results |> Array.filter Result.isError |> Array.length, Is.EqualTo 1)
        Assert.That((WorkSqliteAuthority(path)).CurrentSequence() |> ok, Is.EqualTo 2L))


[<Test>]
let c1_projection_rejects_uncertain_effect_and_mutated_stage_or_role_provenance () =
    let key kind project id =
        { ProjectId = project; Kind = kind; EntityId = id }
    let mutate path k change =
        let store = WorkSqliteAuthority(path)
        let current = store.ReadRecord(k) |> ok |> Option.get
        store.Commit
            { OperationId = Guid.NewGuid()
              Guards = []
              Records =
                [ { Key = k
                    ExpectedVersion = Some current.Version
                    Value = change current.Value } ]
              Capacities = [] } |> ok |> ignore
    for change in [ "unknown"; "stage-tamper"; "role-tamper"; "origin-tamper" ] do
        withImport (fun path raw c2 proof ->
            let store = WorkSqliteAuthority(path)
            let p = projectTwo.ToString("D")
            match change with
            | "unknown" ->
                mutate path (key WorkSqliteRecordKind.Stage p "I2")
                    (fun value -> { value with ExternalEffect = WorkSqliteEffect.Unknown })
            | "stage-tamper" ->
                mutate path (key WorkSqliteRecordKind.Stage p "I2")
                    (fun value ->
                        let body = System.Text.Json.Nodes.JsonNode.Parse(value.BodyJson).AsObject()
                        body["Gate"] <- System.Text.Json.Nodes.JsonObject()
                        { value with BodyJson = body.ToJsonString() })
            | "role-tamper" ->
                mutate path (key WorkSqliteRecordKind.Role p "Worker")
                    (fun value ->
                        let body = System.Text.Json.Nodes.JsonNode.Parse(value.BodyJson).AsObject()
                        let arr = System.Text.Json.Nodes.JsonArray()
                        arr.Add(System.Text.Json.Nodes.JsonValue.Create("other-role-queue"))
                        body["LegacyValue"] <- arr
                        { value with BodyJson = body.ToJsonString() })
            | "origin-tamper" ->
                mutate path (key WorkSqliteRecordKind.Work p "origin/I2")
                    (fun value ->
                        let body = System.Text.Json.Nodes.JsonNode.Parse(value.BodyJson).AsObject()
                        body["SourceRef"] <- System.Text.Json.Nodes.JsonValue.Create("refs/heads/untrusted")
                        { value with BodyJson = body.ToJsonString() })
            | _ -> failwith "unreachable fixture"
            let before = store.CurrentSequence() |> ok
            match projectCommit path raw c2 proof (Guid.NewGuid()) with
            | Error(WorkLegacyProjectionGap.Incomplete _) -> ()
            | other -> Assert.Fail(sprintf "Tampered %s was promoted to authority: %A" change other)
            Assert.That(store.CurrentSequence() |> ok, Is.EqualTo before)
            for project in [ projectOne; projectTwo ] do
                let authorityKey = key WorkSqliteRecordKind.Project (project.ToString("D")) "authority"
                Assert.That(store.ReadRecord(authorityKey) |> ok |> Option.isNone, Is.True))

[<Test>]
let c1_projection_binds_full_original_payloads_not_only_keys_and_union_cases () =
    let account = accountId.ToString("D")
    let p = projectTwo.ToString("D")
    let corruptions : (string * WorkSqliteRecordKind * string * string * (System.Text.Json.Nodes.JsonObject -> unit)) list =
        [ "account payload", WorkSqliteRecordKind.Project, account, "legacy-account",
            (fun json -> json["MaxConcurrent"] <- System.Text.Json.Nodes.JsonValue.Create(9))
          "queue stage body", WorkSqliteRecordKind.Project, p, "legacy-queue",
            (fun json ->
                let works = json["Works"].AsObject()
                let stage = works["I2"].AsObject()
                stage["QueueId"] <- System.Text.Json.Nodes.JsonValue.Create("other"))
          "graph payload", WorkSqliteRecordKind.Project, p, "legacy-composition",
            (fun json -> json["rogueUnverifiedGraphField"] <- System.Text.Json.Nodes.JsonValue.Create("untrusted"))
          "stage gate same case", WorkSqliteRecordKind.Stage, p, "I2",
            (fun json ->
                let fields = System.Text.Json.Nodes.JsonArray()
                fields.Add(System.Text.Json.Nodes.JsonValue.Create("forged"))
                json["Gate"].AsObject()["fields"] <- fields)
          "role duplicate subscription", WorkSqliteRecordKind.Role, p, "Worker",
            (fun json ->
                json["LegacyValue"].AsArray().Add(System.Text.Json.Nodes.JsonValue.Create("implementation")))
          "origin context digest", WorkSqliteRecordKind.Work, p, "origin/I2",
            (fun json -> json["ContextDigest"] <- System.Text.Json.Nodes.JsonValue.Create(String.replicate 64 "f"))
          "retained operation receipt", WorkSqliteRecordKind.Work, p, "receipt/" + approvalReceipt.ToString("D"),
            (fun json ->
                json["LegacyValue"].AsObject()["RequestSha256"] <-
                    System.Text.Json.Nodes.JsonValue.Create(String.replicate 64 "f")) ]
    for detail,kind,entity,recordId,change in corruptions do
        withImport (fun path raw c2 proof ->
            tamperImportedJson path kind entity recordId change
            assertIncompleteWithoutProjection path raw c2 proof detail)

[<Test>]
let c1_projection_rejects_same_key_changed_waiver_receipt_from_original_stage () =
    // Unlike empty waivers, this preserves the dependency *key* and changes
    // only its authoritative receipt value in the transactionally imported C1.
    disposable (fun path ->
        let source = input ()
        let withWaiver (p: WorkLegacyImportProject) =
            if p.ProjectId <> projectTwo then p
            else
                let modified =
                    p.Queue.Works |> Map.map (fun _ stage ->
                        { stage with
                            Dependencies = Set.singleton "predecessor"
                            WaivedDependencies = Map.ofList [ "predecessor", "signed-waiver-receipt" ] })
                let queue = { p.Queue with Works = modified }
                { p with Queue = queue }
        let raw = { source with Projects = source.Projects |> List.map withWaiver }
        let p = plan ()
        let gateway = SourceGateway(p)
        let c2 =
            WorkLegacyImportFence.commitFencedOfflineFixture
                (gateway :> IWorkLegacyOriginalGitFenceGateway) path raw p |> ok
        let proof = evidence raw c2
        tamperImportedJson path WorkSqliteRecordKind.Stage (projectTwo.ToString("D")) "I2"
            (fun json ->
                json["WaivedDependencies"].AsObject()["predecessor"] <-
                    System.Text.Json.Nodes.JsonValue.Create("unsigned-replacement"))
        assertIncompleteWithoutProjection path raw c2 proof "same-key waiver receipt")

[<Test>]
let c1_projection_requires_complete_c2_receipt_and_retained_audit_equality () =
    let modifications : (string * (WorkLegacyGitFenceReceipt -> WorkLegacyGitFenceReceipt)) list =
        [ "pending high-water", (fun r -> { r with OriginalPendingIntentHighWater = 0L })
          "source effect high-water", (fun r -> { r with OriginalSourceEffectHighWater = 0L })
          "quiescence digest", (fun r -> { r with AllClientQuiescenceProofSha256 = String.replicate 64 "f" })
          "protocol version", (fun r -> { r with MinimumClientProtocolVersion = 2 })
          "binding scope", (fun r ->
              { r with Binding = { r.Binding with Scope = WorkLegacyGitScope.Account } })
          "original head", (fun r -> { r with OriginalHead = shaB })
          "exclusive receipt", (fun r -> { r with ExclusiveReceiptId = Guid.NewGuid() })
          "fence operation", (fun r -> { r with FenceOperationId = Guid.NewGuid() }) ]
    for detail,change in modifications do
        withImport (fun path raw c2 proof ->
            let altered =
                { c2 with
                    OriginalWriterFence =
                        c2.OriginalWriterFence |> List.map (fun r ->
                            if r.Binding.EntityId = projectTwo then change r else r) }
            assertIncompleteWithoutProjection path raw altered proof ("C2 " + detail))
    withImport (fun path raw c2 proof ->
        tamperImportedJson path WorkSqliteRecordKind.Project (accountId.ToString("D"))
            "legacy-import-c2-authority-audit"
            (fun json ->
                let fences = json["OriginalGitFences"].AsArray()
                fences.[0].AsObject()["OriginalSourceEffectHighWater"] <-
                    System.Text.Json.Nodes.JsonValue.Create(999L))
        assertIncompleteWithoutProjection path raw c2 proof "retained C2 source-effect audit")

[<Test>]
let c1_projection_rejects_c2_fence_lost_client_quiescence_before_any_sqlite_authority () =
    withImport (fun path raw c2 proof ->
        let altered =
            { c2 with
                OriginalWriterFence =
                    c2.OriginalWriterFence |> List.map (fun r ->
                        if r.Binding.Scope = WorkLegacyGitScope.Project then
                            { r with AllOriginalClientsQuiesced = false }
                        else r) }
        let store = WorkSqliteAuthority(path)
        match projectCommit path raw altered proof (Guid.NewGuid()) with
        | Error(WorkLegacyProjectionGap.Incomplete _) -> ()
        | other -> Assert.Fail(sprintf "Lost original client writer barrier accepted: %A" other)
        Assert.That(store.CurrentSequence() |> ok, Is.EqualTo 1L))

[<Test>]
let c1_projection_identical_uuid_with_changed_verified_role_authority_is_not_a_replay () =
    withImport (fun path raw c2 proof ->
        let operation = Guid.NewGuid()
        projectCommit path raw c2 proof operation |> ok |> ignore
        let changed =
            projectEvidence (fun p ->
                { p with
                    Roles = p.Roles |> List.map (fun r ->
                        { r with InstructionsDigest = String.replicate 64 "f" }) }) proof
        match projectCommit path raw c2 changed operation with
        | Error(WorkLegacyProjectionGap.Storage(WorkSqliteFailure.OperationIdReused _)) -> ()
        | Error(WorkLegacyProjectionGap.Incomplete _) -> ()
        | other -> Assert.Fail(sprintf "Mismatched original UUID rebound to new authority: %A" other)
        Assert.That((WorkSqliteAuthority(path)).CurrentSequence() |> ok, Is.EqualTo 2L))

[<Test>]
let c1_projection_existing_legacy_claim_and_capacity_are_not_synthesized_as_free () =
    withImport (fun path raw c2 proof ->
        let store = WorkSqliteAuthority(path)
        let conflicting = { proof with Reserved = 1 }
        match projectCommit path raw c2 conflicting (Guid.NewGuid()) with
        | Error(WorkLegacyProjectionGap.Incomplete _) -> ()
        | other -> Assert.Fail(sprintf "Unproved account reservation accepted: %A" other)
        Assert.That((store.ReadCapacity(accountId.ToString("D")) |> ok |> Option.get).Reserved, Is.Zero))
