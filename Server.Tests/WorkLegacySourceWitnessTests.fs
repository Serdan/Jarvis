module WorkLegacySourceWitnessTests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open NUnit.Framework
open Jarvis.WorkQueues
open Jarvis.WorkQueues.Contracts
open Server

let private accountId = Guid.Parse("55555555-5555-4555-8555-555555555555")
let private accountLedger = Guid.Parse("66666666-6666-4666-8666-666666666666")
let private projectOne = Guid.Parse("11111111-1111-4111-8111-111111111111")
let private projectTwo = Guid.Parse("22222222-2222-4222-8222-222222222222")
let private ledgerOne = Guid.Parse("33333333-3333-4333-8333-333333333333")
let private ledgerTwo = Guid.Parse("44444444-4444-4444-8444-444444444444")
let private deviceId = Guid.Parse("77777777-7777-4777-8777-777777777777")
let private fenceId = Guid.Parse("88888888-8888-4888-8888-888888888888")
let private importId = Guid.Parse("99999999-9999-4999-8999-999999999999")
let private approvalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")
let private roleReceipt = Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd")
let private digest (s: string) =
    SHA256.HashData(Encoding.UTF8.GetBytes s)
    |> Convert.ToHexString
    |> fun value -> value.ToLowerInvariant()

let private runGit directory args =
    let start = ProcessStartInfo("git")
    start.WorkingDirectory <- directory
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    start.UseShellExecute <- false
    for arg in args do start.ArgumentList.Add arg
    use child = Process.Start start
    let output = child.StandardOutput.ReadToEnd()
    let errors = child.StandardError.ReadToEnd()
    if not (child.WaitForExit 15000) then
        child.Kill true
        Assert.Fail("Fixture git command timed out.")
    if child.ExitCode <> 0 then
        Assert.Fail(sprintf "Fixture git %A failed: %s" args errors)
    output.Trim()

type private GitFixture =
    { Folder: string
      Original: string
      Fenced: string
      CanonicalId: string
      RemoteFingerprint: string }

let private makeRepo root name =
    let path = Path.Combine(root, name)
    Directory.CreateDirectory path |> ignore
    runGit path [ "init"; "-q"; "-b"; "main" ] |> ignore
    runGit path [ "config"; "user.email"; "fixture@example.invalid" ] |> ignore
    runGit path [ "config"; "user.name"; "Work Fixture" ] |> ignore
    File.WriteAllText(Path.Combine(path, "original.txt"), name)
    runGit path [ "add"; "original.txt" ] |> ignore
    runGit path [ "commit"; "-q"; "-m"; "original committed witness" ] |> ignore
    let original = runGit path [ "rev-parse"; "refs/heads/main" ]
    // A separate commit represents the exclusive original admission fence.
    runGit path [ "commit"; "-q"; "--allow-empty"; "-m"; "stop fence" ] |> ignore
    let fenced = runGit path [ "rev-parse"; "refs/heads/main" ]
    let canonical =
        if name = "account" then "refs/heads/jarvis-account-admission"
        else "refs/heads/jarvis-orchestration"
    runGit path [ "update-ref"; canonical; fenced ] |> ignore
    runGit path [ "branch"; "witness-effects"; "main" ] |> ignore
    { Folder = path
      Original = original
      Fenced = fenced
      CanonicalId = digest ("repo:" + name)
      RemoteFingerprint = digest ("fingerprint:" + name) }

let private head oid =
    { PinnedCommit = oid
      ObservedCommit = oid
      AncestryVerified = true
      HistoryComplete = true
      SourceHighWater = 1L
      ObservedHighWater = 1L }

let private operation (id: Guid) =
    { OperationId = id
      Sequence = 1L
      RequestSha256 = digest ("request:" + id.ToString("D"))
      ResultSha256 = digest ("result:" + id.ToString("D"))
      Kind = "original-project-operation"
      TerminalResult = None }

let private project (repo: GitFixture) (projectId: Guid) (ledgerId: Guid) (operationId: Guid) =
    let workId = "same-name-across-projects"
    let stage =
        { Id = workId
          ProjectId = projectId.ToString("D")
          SubjectId = "task-duplicate-label"
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
      Head = head repo.Original
      Queue =
        { emptyProject (Map.ofList [ "Worker", Set.singleton "implementation" ]) with
            Migration = MigrationEpoch.Frozen
            Works = Map.ofList [ workId, stage ] }
      Graph = WorkComposition.empty
      Origins =
        [ { WorkId = workId
            StageGeneration = 1
            SubjectId = stage.SubjectId
            SourceHead = repo.Original
            SourceHighWater = 1L
            SourceAncestryVerified = true
            RepositoryId = repo.CanonicalId
            SourceRef = "refs/heads/main"
            SourceCommit = repo.Original
            RoleId = "Worker"
            RoleVersion = 1
            GraphVersion = 0
            ContextDigest = digest "context" } ]
      Reviews = []
      Integrations = []
      Terminals = []
      Validations = []
      Operations = [ operation operationId ] }

let private target scope repo ledger project =
    { Purpose = scope
      AccountId = accountId
      ProjectId = project
      LedgerId = ledger
      LedgerEpoch = 1
      CanonicalRepositoryId = repo.CanonicalId
      ApprovedRemoteFingerprintSha256 = repo.RemoteFingerprint
      GitRef =
        match scope with
        | OriginalGitRefPurpose.AccountLedger -> "refs/heads/jarvis-account-admission"
        | OriginalGitRefPurpose.ProjectLedger -> "refs/heads/jarvis-orchestration"
        | OriginalGitRefPurpose.SourceEffect -> "refs/heads/witness-effects" }

let private fence scope entity ledger (repo: GitFixture) sourceEffectWater =
    { Binding =
        { Scope = scope
          EntityId = entity
          LedgerId = ledger
          LedgerEpoch = 1
          DeviceId = deviceId
          TrustedRemoteId = repo.CanonicalId
          PinnedHead = repo.Original
          SourceHighWater = 1L }
      DecisionId = approvalId
      DecisionVersion = 1L
      FenceOperationId = fenceId
      ExclusiveReceiptId = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb")
      OriginalHead = repo.Original
      FencedHead = repo.Fenced
      OriginalHighWater = 1L
      FenceHighWater = 2L
      WriterGeneration = 1L
      NoNewOriginalMutations = true
      DurableUntilExplicitCutover = true
      OriginalPendingIntentHighWater = 1L
      OriginalSourceEffectHighWater = sourceEffectWater
      MinimumClientProtocolVersion = 1
      AllOriginalClientsQuiesced = true
      AllClientQuiescenceProofSha256 = digest "all-clients"
      AuditSha256 = digest ("audit:" + entity.ToString("D")) }

let private projectProof (p: WorkLegacyImportProject) =
    { ProjectId = p.ProjectId
      LedgerId = p.LedgerId
      SourceHead = p.Head.PinnedCommit
      StopGeneration = 0L
      Decisions = []
      Roles =
        [ { RoleId = "Worker"
            Version = 1
            InstructionsDigest = digest "role-instructions"
            Queues = Set.singleton "implementation"
            EvidenceReceiptId = roleReceipt
            SourceHead = p.Head.PinnedCommit } ]
      OptionalUnitIds = Set.empty }

let private projectLineage (p: WorkLegacyImportProject) =
    p.Origins
    |> List.map (fun origin ->
        { ProjectId = p.ProjectId
          WorkId = origin.WorkId
          CanonicalRepositoryId = origin.RepositoryId
          ApprovedRemoteFingerprintSha256 =
            digest (if p.ProjectId = projectOne then "fingerprint:p1" else "fingerprint:p2")
          SourceRef = origin.SourceRef
          SourceCommit = origin.SourceCommit
          OriginalOperationId = p.Operations.Head.OperationId
          OriginalRequestSha256 = p.Operations.Head.RequestSha256
          ReachabilityReceiptSha256 = digest ("reachable:" + origin.SourceCommit) })

let private projectJournal (p: WorkLegacyImportProject) (repo: GitFixture) =
    let effectTarget = target OriginalGitRefPurpose.SourceEffect repo p.LedgerId (Some p.ProjectId)
    let e =
        { Sequence = 1L
          OperationId = p.Operations.Head.OperationId
          RequestSha256 = p.Operations.Head.RequestSha256
          ResultSha256 = p.Operations.Head.ResultSha256
          Outcome = WorkLegacySourceEffectOutcome.Observed repo.Original }
    { Target = effectTarget
      RemoteHead = runGit repo.Folder [ "rev-parse"; "refs/heads/witness-effects" ]
      Events = [ e ]
      DurableHighWater = 1L
      RemoteHighWater = 1L
      DurableDigestSha256 = digest ("effect:" + p.ProjectId.ToString("D"))
      RemoteDigestSha256 = digest ("effect:" + p.ProjectId.ToString("D")) }

let private ledgerCapture account (repo: GitFixture) ops effectJournal f =
    { Target =
        target
            (if account then OriginalGitRefPurpose.AccountLedger else OriginalGitRefPurpose.ProjectLedger)
            repo f.Binding.LedgerId
            (if account then None else Some f.Binding.EntityId)
      CurrentRemoteHead = repo.Fenced
      Operations = ops
      PendingIntentHighWater = 1L
      ResolvedIntentHighWater = 1L
      UnresolvedIntentIds = []
      Fence = f
      EffectJournal = effectJournal }

type private Fixture =
    { Raw: WorkLegacyImportInspection
      C2: WorkLegacyFencedOfflineReceipt
      Captured: WorkLegacyAuthenticatedSourceCapture
      Repos: GitFixture list
      DatabasePath: string }

let private withFixture action =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-work-source-witness-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    try
        let a = makeRepo root "account"
        let r1 = makeRepo root "p1"
        let r2 = makeRepo root "p2"
        let projects =
            [ project r1 projectOne ledgerOne (Guid.Parse "10101010-1010-4010-8010-101010101010")
              project r2 projectTwo ledgerTwo (Guid.Parse "20202020-2020-4020-8020-202020202020") ]
        let raw =
            { Account =
                { AccountId = accountId
                  LedgerId = accountLedger
                  LedgerEpoch = 1
                  Head = head a.Original
                  MaxConcurrent = 2
                  Tokens = []
                  Operations = [ operation (Guid.Parse "30303030-3030-4030-8030-303030303030") ] }
              Projects = projects }
        let db = Path.Combine(root, "original-import.sqlite")
        File.WriteAllText(
            Path.Combine(root, WorkLegacyImportSqlite.DisposableMarker),
            WorkLegacyImportSqlite.DisposableMarkerContent)
        let sourceFence =
            { AccountHead = a.Original
              ProjectHeads = projects |> List.map (fun p -> p.ProjectId, p.Head.PinnedCommit)
              FenceVersion = 1L
              FenceOperationId = fenceId }
        let imported =
            match WorkLegacyImportSqlite.commitOfflineFixture db importId raw sourceFence with
            | Ok receipt -> receipt
            | Error gap -> Assert.Fail(sprintf "C1 fixture rejected: %A" gap); Unchecked.defaultof<_>
        let accountFence = fence WorkLegacyGitScope.Account accountId accountLedger a 0L
        let f1 = fence WorkLegacyGitScope.Project projectOne ledgerOne r1 1L
        let f2 = fence WorkLegacyGitScope.Project projectTwo ledgerTwo r2 1L
        let c2 =
            { Import = imported
              ApprovalId = approvalId
              OriginalWriterFence = [ accountFence; f1; f2 ] }
        let captures =
            [ ledgerCapture true a raw.Account.Operations None accountFence
              ledgerCapture false r1 projects.[0].Operations
                  (Some(projectJournal projects.[0] r1)) f1
              ledgerCapture false r2 projects.[1].Operations
                  (Some(projectJournal projects.[1] r2)) f2 ]
        let targets =
            (captures |> List.map (fun c -> c.Target))
            @ (captures |> List.choose (fun c -> c.EffectJournal |> Option.map (fun j -> j.Target)))
        let snapshot =
            { Inspection = raw
              C2Receipt = c2
              Projection =
                { AccountId = accountId
                  AccountLedgerId = accountLedger
                  AccountHead = a.Original
                  DeviceId = deviceId
                  ApprovalId = approvalId
                  ImportOperationId = importId
                  Maximum = 2
                  Reserved = 0
                  Projects = projects |> List.map projectProof }
              AccountCapacityVersion = 1L
              LedgerCaptures = captures
              SourceLineages = projects |> List.collect projectLineage
              OriginalBarrier =
                { FenceOperationId = fenceId
                  FenceEpoch = 1L
                  ExactTargets = targets
                  RemoteWitnessSha256 = digest "original-remote-barrier"
                  OriginalPendingIntentHighWater = 1L
                  OriginalSourceEffectHighWater = 1L } }
        action { Raw = raw; C2 = c2; Captured = snapshot; Repos = [a;r1;r2]; DatabasePath = db }
    finally
        if Directory.Exists root then Directory.Delete(root, true)

let private reader first second : IWorkLegacyAuthenticatedSourceReader =
    { new IWorkLegacyAuthenticatedSourceReader with
        member _.ReadAuthenticated() = Ok first
        member _.ReinspectAuthenticated() = Ok second }

let private inspect f captured =
    WorkLegacySourceWitness.capture
        (reader captured captured) f.DatabasePath f.Raw f.C2

let private isUnknown = function
    | Error(WorkLegacySourceWitnessGap.Unknown _) -> true
    | _ -> false

let private isRejected = function
    | Error(WorkLegacySourceWitnessGap.Rejected _) -> true
    | _ -> false

let private isIncomplete = function
    | Error(WorkLegacySourceWitnessGap.Incomplete _) -> true
    | _ -> false

[<Test>]
let ``two original projects sharing a work name keep distinct Git account role and source witnesses`` () =
    withFixture (fun f ->
        // The fixture reads real disposable Git SHA/ref state, then matches
        // the independently captured source to C0 and C1 SQLite provenance.
        for repo, ledger in List.zip f.Repos f.Captured.LedgerCaptures do
            Assert.That(runGit repo.Folder [ "rev-parse"; "refs/heads/main" ], Is.EqualTo(repo.Fenced))
            Assert.That(runGit repo.Folder [ "rev-parse"; ledger.Target.GitRef ], Is.EqualTo(ledger.CurrentRemoteHead))
            Assert.That(ledger.Target.CanonicalRepositoryId, Is.EqualTo repo.CanonicalId)
        match inspect f f.Captured with
        | Error gap -> Assert.Fail(sprintf "Expected verified offline fixture: %A" gap)
        | Ok report ->
            Assert.That(report.AccountId, Is.EqualTo accountId)
            Assert.That(report.AccountCapacityVersion, Is.EqualTo 1L)
            Assert.That(report.SourceRefs.Length, Is.EqualTo 3)
            Assert.That(report.StopGenerations.Length, Is.EqualTo 3)
            Assert.That(report.EffectHighWaters.Length, Is.EqualTo 3)
            Assert.That(report.SourceLineages.Length, Is.EqualTo 2)
            Assert.That(report.Roles.Length, Is.EqualTo 2)
            Assert.That(report.Stages.Length, Is.EqualTo 2)
            Assert.That(report.OperationIds.Length, Is.EqualTo 3)
            Assert.That(report.AccountReserved, Is.EqualTo 0)
            Assert.That(report.OriginalBarrierEpoch, Is.EqualTo 1L))

[<Test>]
let ``caller-forged original repository ref commit and changed project identity are rejected`` () =
    withFixture (fun f ->
        let p = f.Raw.Projects.Head
        let origin = p.Origins.Head
        let changedOrigin = { origin with SourceRef = "refs/heads/forged" }
        let forged = { f.Raw with Projects = { p with Origins = [changedOrigin] } :: f.Raw.Projects.Tail }
        Assert.That(
            WorkLegacySourceWitness.capture
                (reader f.Captured f.Captured) f.DatabasePath forged f.C2
            |> isRejected, Is.True)
        let altered =
            { f.Captured with
                LedgerCaptures =
                    f.Captured.LedgerCaptures
                    |> List.map (fun l ->
                        if l.Target.ProjectId = Some projectOne then
                            { l with Target = { l.Target with CanonicalRepositoryId = digest "forged" } }
                        else l) }
        Assert.That(inspect f altered |> isRejected, Is.True)
        let wrongSha =
            { f.Captured with
                SourceLineages =
                    f.Captured.SourceLineages
                    |> List.map (fun l ->
                        if l.ProjectId = projectOne then
                            { l with SourceCommit = f.Repos.[2].Original }
                        else l) }
        Assert.That(inspect f wrongSha |> isIncomplete, Is.True))

[<Test>]
let ``pending effect gaps rollback duplicate UUID high-water and UNKNOWN fail closed`` () =
    withFixture (fun f ->
        let changeProject transform =
            { f.Captured with
                LedgerCaptures =
                    f.Captured.LedgerCaptures
                    |> List.map (fun l ->
                        if l.Target.ProjectId = Some projectOne then transform l else l) }
        let lostAck = changeProject (fun l -> { l with ResolvedIntentHighWater = 0L })
        Assert.That(inspect f lostAck |> isUnknown, Is.True)
        let changeJournal amend =
            changeProject (fun l -> { l with EffectJournal = l.EffectJournal |> Option.map amend })
        let rollback = changeJournal (fun j -> { j with RemoteHighWater = 2L })
        Assert.That(inspect f rollback |> isUnknown, Is.True)
        let corrupt = changeJournal (fun j -> { j with RemoteDigestSha256 = digest "not-the-journal" })
        Assert.That(inspect f corrupt |> isUnknown, Is.True)
        let effectUnknown =
            changeJournal (fun j ->
                let entries = j.Events |> List.map (fun e -> { e with Outcome = WorkLegacySourceEffectOutcome.Unknown })
                { j with Events = entries })
        Assert.That(inspect f effectUnknown |> isUnknown, Is.True)
        let forgedResult =
            changeJournal (fun j ->
                let forged = j.Events |> List.map (fun e ->
                    { e with ResultSha256 = digest "a different result for same UUID" })
                { j with Events = forged })
        Assert.That(inspect f forgedResult |> isUnknown, Is.True)
        let duplicate = changeJournal (fun j ->
            { j with Events = j.Events @ j.Events; DurableHighWater = 2L; RemoteHighWater = 2L })
        Assert.That(inspect f duplicate |> isUnknown, Is.True)
        let brokenOriginal =
            let p = f.Raw.Projects.Head
            let op = p.Operations.Head
            let bad =
                { p with
                    Operations = [ op; { op with Sequence = 2L; RequestSha256 = digest "changed-payload" } ]
                    Head = { p.Head with SourceHighWater = 2L; ObservedHighWater = 2L } }
            { f.Raw with Projects = bad :: f.Raw.Projects.Tail }
        Assert.That(
            WorkLegacySourceWitness.capture
                (reader f.Captured f.Captured) f.DatabasePath brokenOriginal f.C2
            |> isRejected, Is.True))

[<Test>]
let ``stale STOP post-STOP push stale role and missing journal do not provide authority`` () =
    withFixture (fun f ->
        let changedRepo = f.Repos.[2]
        File.AppendAllText(Path.Combine(changedRepo.Folder, "original.txt"), "late mutation")
        runGit changedRepo.Folder [ "add"; "original.txt" ] |> ignore
        runGit changedRepo.Folder [ "commit"; "-q"; "-m"; "late post-STOP Git effect" ] |> ignore
        let mutatedHead = runGit changedRepo.Folder [ "rev-parse"; "refs/heads/main" ]
        runGit changedRepo.Folder [ "update-ref"; "refs/heads/jarvis-orchestration"; mutatedHead ] |> ignore
        Assert.That(mutatedHead, Is.Not.EqualTo(changedRepo.Fenced))
        let delayed =
            { f.Captured with
                LedgerCaptures =
                    f.Captured.LedgerCaptures |> List.map (fun l ->
                        if l.Target.ProjectId = Some projectTwo then
                            { l with CurrentRemoteHead = mutatedHead }
                        else l) }
        Assert.That(
            WorkLegacySourceWitness.capture
                (reader f.Captured delayed) f.DatabasePath f.Raw f.C2
            |> isUnknown, Is.True)
        let staleRole =
            { f.Captured with
                Projection =
                    { f.Captured.Projection with
                        Projects =
                            f.Captured.Projection.Projects |> List.map (fun p ->
                                if p.ProjectId = projectOne then
                                    { p with Roles = p.Roles |> List.map (fun r -> { r with Version = 2 }) }
                                else p) } }
        Assert.That(inspect f staleRole |> isIncomplete, Is.True)
        let missingEffect =
            { f.Captured with
                LedgerCaptures = f.Captured.LedgerCaptures |> List.map (fun l ->
                    if l.Target.ProjectId = Some projectTwo then
                        { l with EffectJournal = None }
                    else l) }
        Assert.That(inspect f missingEffect |> isIncomplete, Is.True))

[<Test>]
let ``no authenticated original reader or missing C1 receipt remains incomplete`` () =
    withFixture (fun f ->
        Assert.That(
            WorkLegacySourceWitness.capture null f.DatabasePath f.Raw f.C2 |> isIncomplete,
            Is.True)
        let unbound =
            { new IWorkLegacyAuthenticatedSourceReader with
                member _.ReadAuthenticated() =
                    Error(WorkLegacySourceReadFailure.NoAuthenticatedBinding "no real original writer gate")
                member _.ReinspectAuthenticated() =
                    Error(WorkLegacySourceReadFailure.NoAuthenticatedBinding "no real original writer gate") }
        Assert.That(WorkLegacySourceWitness.capture unbound f.DatabasePath f.Raw f.C2 |> isIncomplete, Is.True)
        let differentC1 =
            { f.C2 with Import = { f.C2.Import with ImportOperationId = Guid.NewGuid() } }
        let snapshot = { f.Captured with C2Receipt = differentC1 }
        Assert.That(
            WorkLegacySourceWitness.capture
                (reader snapshot snapshot) f.DatabasePath f.Raw differentC1
            |> isIncomplete, Is.True))

[<Test>]
let ``C1 imported project source-head tampering is incomplete even with an intact import receipt`` () =
    withFixture (fun f ->
        let store = WorkSqliteAuthority f.DatabasePath
        let key =
            { ProjectId = projectOne.ToString("D")
              Kind = WorkSqliteRecordKind.Project
              EntityId = "legacy-queue" }
        let original =
            match store.ReadRecord key with
            | Ok(Some row) -> row
            | other -> Assert.Fail(sprintf "Missing original C1 project: %A" other); Unchecked.defaultof<_>
        let changed =
            { Key = key
              ExpectedVersion = Some original.Version
              Value = { original.Value with SourceSha = Some f.Repos.[2].Original } }
        match store.Commit
            { OperationId = Guid.NewGuid()
              Guards = []
              Records = [ changed ]
              Capacities = [] } with
        | Ok _ -> ()
        | Error why -> Assert.Fail(sprintf "Disposable source mutation failed: %A" why)
        Assert.That(inspect f f.Captured |> isIncomplete, Is.True))
