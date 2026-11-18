module WorkLegacyImportAuthenticatedReadDiagnosticsTests

open System
open System.Diagnostics
open System.IO
open NUnit.Framework
open Jarvis.WorkQueues
open Jarvis.WorkQueues.Contracts
open Server

let private accountId = Guid.Parse "55555555-5555-4555-8555-555555555555"
let private ledgerAccount = Guid.Parse "66666666-6666-4666-8666-666666666666"
let private projects =
    [ Guid.Parse "11111111-1111-4111-8111-111111111111", Guid.Parse "33333333-3333-4333-8333-333333333333"
      Guid.Parse "22222222-2222-4222-8222-222222222222", Guid.Parse "44444444-4444-4444-8444-444444444444" ]
let private sha = String.replicate 40 "a"
let private hash = String.replicate 64 "b"
let private repoId project =
    match project with
    | None -> String.replicate 64 "a"
    | Some id when id = fst projects.Head -> String.replicate 64 "c"
    | Some _ -> String.replicate 64 "d"

let private head =
    { PinnedCommit = sha; ObservedCommit = sha; AncestryVerified = true
      HistoryComplete = true; SourceHighWater = 0L; ObservedHighWater = 0L }

let private binding scope id ledger =
    { Scope = scope; EntityId = id; LedgerId = ledger; LedgerEpoch = 1
      DeviceId = Guid.Parse "77777777-7777-4777-8777-777777777777"
      TrustedRemoteId = repoId (if scope = WorkLegacyGitScope.Account then None else Some id); PinnedHead = sha; SourceHighWater = 0L }

let private fence scope id ledger =
    { Binding = binding scope id ledger
      DecisionId = Guid.NewGuid(); DecisionVersion = 1L
      FenceOperationId = Guid.NewGuid(); ExclusiveReceiptId = Guid.NewGuid()
      OriginalHead = sha; FencedHead = sha; OriginalHighWater = 0L
      FenceHighWater = 1L; WriterGeneration = 1L
      NoNewOriginalMutations = true; DurableUntilExplicitCutover = true
      OriginalPendingIntentHighWater = 0L; OriginalSourceEffectHighWater = 0L
      MinimumClientProtocolVersion = 1; AllOriginalClientsQuiesced = true
      AllClientQuiescenceProofSha256 = hash; AuditSha256 = hash }

let private target purpose project ledger =
    { Purpose = purpose; AccountId = accountId; ProjectId = project
      LedgerId = ledger; LedgerEpoch = 1; CanonicalRepositoryId = repoId project
      ApprovedRemoteFingerprintSha256 = hash
      GitRef = match purpose with
               | OriginalGitRefPurpose.AccountLedger -> "refs/heads/account"
               | OriginalGitRefPurpose.ProjectLedger -> "refs/heads/project"
               | OriginalGitRefPurpose.SourceEffect -> "refs/heads/source-effects" }

let private ledger isAccount project ledger =
    let scope = if isAccount then WorkLegacyGitScope.Account else WorkLegacyGitScope.Project
    let purpose = if isAccount then OriginalGitRefPurpose.AccountLedger else OriginalGitRefPurpose.ProjectLedger
    { Target = target purpose project ledger
      CurrentRemoteHead = sha; Operations = []; PendingIntentHighWater = 0L
      ResolvedIntentHighWater = 0L; UnresolvedIntentIds = []
      Fence = fence scope (defaultArg project accountId) ledger; EffectJournal = None }

let private originalCapture () =
    let project id ledger =
        { ProjectId = id; LedgerId = ledger; LedgerEpoch = 1; Head = head
          Queue = emptyProject (Map.ofList [ "Worker", Set.singleton "implementation" ])
          Graph = WorkComposition.empty; Origins = []; Reviews = []; Integrations = []
          Terminals = []; Validations = []; Operations = [] }
    { Inspection =
        { Account = { AccountId = accountId; LedgerId = ledgerAccount; LedgerEpoch = 1
                      Head = head; MaxConcurrent = 2; Tokens = []; Operations = [] }
          Projects = projects |> List.map (fun (p,l) -> project p l) }
      C2Receipt = Unchecked.defaultof<WorkLegacyFencedOfflineReceipt>
      Projection = Unchecked.defaultof<WorkLegacyProjectionEvidence>
      AccountCapacityVersion = 0L
      LedgerCaptures = ledger true None ledgerAccount
                       :: (projects |> List.map (fun (p,l) -> ledger false (Some p) l))
      SourceLineages = []
      OriginalBarrier = Unchecked.defaultof<OriginalGitPublisherBarrierReceipt> }

let private reader first second anchor : IWorkLegacyAuthenticatedSourceReader =
    { new IWorkLegacyAuthenticatedSourceReader with
        member _.ReadAuthenticated() = first
        member _.ReinspectAuthenticated() = second
        member _.ReadEffectAnchor target = anchor target }

let private subject kind =
    { SubjectKind = kind; SubjectProjectId = fst projects.Head; SubjectEntityId = "test-subject" }

let private kinds =
    [ WorkSqliteRecordKind.Project; WorkSqliteRecordKind.Work
      WorkSqliteRecordKind.Stage; WorkSqliteRecordKind.Launch
      WorkSqliteRecordKind.Role; WorkSqliteRecordKind.Review
      WorkSqliteRecordKind.Integration; WorkSqliteRecordKind.ExternalEffect ]

let private isIncomplete = function
    | WorkLegacySourceWitnessGap.Incomplete _ -> true | _ -> false
let private isUnknown = function
    | WorkLegacySourceWitnessGap.Unknown _ -> true | _ -> false
let private isRejected = function
    | WorkLegacySourceWitnessGap.Rejected _ -> true | _ -> false

[<Test>]
let ``every eight-kind source read remains incomplete without independently authenticated project and account`` () =
    let missing =
        reader (Error(WorkLegacySourceReadFailure.NoAuthenticatedBinding "unbound account"))
               (Error(WorkLegacySourceReadFailure.NoAuthenticatedBinding "unbound account"))
               (fun _ -> Error(WorkLegacySourceReadFailure.NoAuthenticatedBinding "no durable anchor"))
    for kind in kinds do
        let result = WorkLegacyImportAuthenticatedReadDiagnostics.probe missing (subject kind)
        Assert.That(isIncomplete result, Is.True, sprintf "Kind %A: %A" kind result)
    Assert.That(isIncomplete (WorkLegacyImportAuthenticatedReadDiagnostics.probe null (subject WorkSqliteRecordKind.Project)), Is.True)

[<Test>]
let ``unknown original read or pending physical operation never means absent effect`` () =
    let missing =
        reader (Error(WorkLegacySourceReadFailure.UncertainEffect "prior push may have landed"))
               (Error(WorkLegacySourceReadFailure.UncertainEffect "prior push may have landed"))
               (fun _ -> Error(WorkLegacySourceReadFailure.UncertainEffect "unknown anchor"))
    Assert.That(
        WorkLegacyImportAuthenticatedReadDiagnostics.probe missing (subject WorkSqliteRecordKind.ExternalEffect)
        |> isUnknown, Is.True)
    let initial = originalCapture ()
    let updated =
        initial.LedgerCaptures
        |> List.map (fun l ->
            if l.Target.ProjectId = Some (fst projects.Head) then
                { l with PendingIntentHighWater = 1L
                         ResolvedIntentHighWater = 0L
                         UnresolvedIntentIds = [Guid.NewGuid()] }
            else l)
    let unresolved = { initial with LedgerCaptures = updated }
    let observed = reader (Ok unresolved) (Ok unresolved) (fun t -> Ok t)
    Assert.That(WorkLegacyImportAuthenticatedReadDiagnostics.probe observed (subject WorkSqliteRecordKind.ExternalEffect)
                |> isUnknown, Is.True)

[<Test>]
let ``missing project ledger binding and conflicting original remote head are typed fail closed`` () =
    let initial = originalCapture ()
    let unbound = { initial with LedgerCaptures = initial.LedgerCaptures.Tail }
    let missing = reader (Ok unbound) (Ok unbound) (fun t -> Ok t)
    Assert.That(WorkLegacyImportAuthenticatedReadDiagnostics.probe missing (subject WorkSqliteRecordKind.Project)
                |> isIncomplete, Is.True)
    let badHeadCaptures =
        initial.LedgerCaptures
        |> List.map (fun l ->
            if l.Target.ProjectId = Some (fst projects.Head) then
                { l with CurrentRemoteHead = String.replicate 40 "c" }
            else l)
    let badHead = { initial with LedgerCaptures = badHeadCaptures }
    let conflict = reader (Ok badHead) (Ok badHead) (fun t -> Ok t)
    Assert.That(WorkLegacyImportAuthenticatedReadDiagnostics.probe conflict (subject WorkSqliteRecordKind.Project)
                |> isUnknown, Is.True)
    let wrongEpochCaptures =
        initial.LedgerCaptures
        |> List.map (fun l ->
            if l.Target.ProjectId = Some (fst projects.Head) then
                { l with Target = { l.Target with LedgerEpoch = 2 } }
            else l)
    let wrongEpoch = { initial with LedgerCaptures = wrongEpochCaptures }
    let forged = reader (Ok wrongEpoch) (Ok wrongEpoch) (fun t -> Ok t)
    Assert.That(WorkLegacyImportAuthenticatedReadDiagnostics.probe forged (subject WorkSqliteRecordKind.Project)
                |> isRejected, Is.True)

[<Test>]
let ``delayed review and integration ref effects after distinct ledger CAS stay unknown`` () =
    let initial = originalCapture ()
    for kind in [ WorkSqliteRecordKind.Review; WorkSqliteRecordKind.Integration ] do
        let lineage =
            { ProjectId = fst projects.Head
              WorkId = "orphan-after-ledger-cas"
              CanonicalRepositoryId = hash
              ApprovedRemoteFingerprintSha256 = hash
              SourceRef = "refs/heads/uncommitted-review-ref"
              SourceCommit = sha
              OriginalOperationId = Guid.NewGuid()
              OriginalRequestSha256 = hash
              ReachabilityReceiptSha256 = hash }
        let late = { initial with SourceLineages = [lineage] }
        let changed = reader (Ok initial) (Ok late) (fun t -> Ok t)
        Assert.That(WorkLegacyImportAuthenticatedReadDiagnostics.probe changed (subject kind)
                    |> isUnknown, Is.True, sprintf "Late %A source ref" kind)

[<Test>]
let ``a caller supplied digest and SHA cannot upgrade missing durable effect anchor`` () =
    let initial = originalCapture ()
    let echo = reader (Ok initial) (Ok initial)
                      (fun _ -> Error(WorkLegacySourceReadFailure.NoAuthenticatedBinding "not remote attested"))
    for kind in kinds do
        Assert.That(WorkLegacyImportAuthenticatedReadDiagnostics.probe echo (subject kind)
                    |> isIncomplete, Is.True, sprintf "Echo %A" kind)

let private git folder args =
    let start = ProcessStartInfo("git")
    start.WorkingDirectory <- folder
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    start.UseShellExecute <- false
    for arg in args do start.ArgumentList.Add arg
    use child = Process.Start start
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    if not (child.WaitForExit 15000) then
        child.Kill true
        Assert.Fail("Disposable original Git ref command timed out")
    if child.ExitCode <> 0 then
        Assert.Fail(sprintf "Disposable Git %A failed: %s" args error)
    output.Trim()

[<TestCase("review")>]
[<TestCase("integration")>]
let ``separately pushed original review or integration ref after captured ledger state remains Unknown`` (domain: string) =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-original-source-effect-" + Guid.NewGuid().ToString("N"))
    let local = Path.Combine(root, "local")
    let remote = Path.Combine(root, "original-remote.git")
    Directory.CreateDirectory local |> ignore
    Directory.CreateDirectory remote |> ignore
    try
        git remote [ "init"; "--bare"; "-q" ] |> ignore
        git local [ "init"; "-q"; "-b"; "main" ] |> ignore
        git local [ "config"; "user.name"; "Offline original ref fixture" ] |> ignore
        git local [ "config"; "user.email"; "fixture@example.invalid" ] |> ignore
        File.WriteAllText(Path.Combine(local, "ledger.txt"), "original-ledger-CAS")
        git local [ "add"; "ledger.txt" ] |> ignore
        git local [ "commit"; "-q"; "-m"; "original ledger before separate source stage" ] |> ignore
        git local [ "remote"; "add"; "origin"; remote ] |> ignore
        git local [ "push"; "-q"; "origin"; "HEAD:refs/heads/legacy-ledger" ] |> ignore
        let ledgerHead = git local [ "rev-parse"; "HEAD" ]
        let refName = "refs/heads/original-" + domain + "-effect"
        Assert.That(git local [ "ls-remote"; "--heads"; "origin"; refName ], Is.Empty)
        let first = originalCapture ()
        let observer =
            { new IWorkLegacyAuthenticatedSourceReader with
                member _.ReadAuthenticated() = Ok first
                member _.ReadEffectAnchor _ =
                    Error(WorkLegacySourceReadFailure.NoAuthenticatedBinding "real original publisher not installed")
                member _.ReinspectAuthenticated() =
                    // Physical original-source ref appears independently in the
                    // disposable bare remote AFTER the ledger snapshot was read.
                    git local [ "push"; "-q"; "origin"; "HEAD:" + refName ] |> ignore
                    let remoteRef = git local [ "ls-remote"; "--heads"; "origin"; refName ]
                    Assert.That(remoteRef, Is.EqualTo(ledgerHead + "\t" + refName))
                    let orphan =
                        { ProjectId = fst projects.Head
                          WorkId = "ref-published-without-ledger-CAS"
                          CanonicalRepositoryId = repoId (Some(fst projects.Head))
                          ApprovedRemoteFingerprintSha256 = hash
                          SourceRef = refName
                          SourceCommit = remoteRef.Split('\t').[0]
                          OriginalOperationId = Guid.NewGuid()
                          OriginalRequestSha256 = hash
                          ReachabilityReceiptSha256 = hash }
                    Ok { first with SourceLineages = [ orphan ] } }
        let kind =
            if domain = "review" then WorkSqliteRecordKind.Review
            else WorkSqliteRecordKind.Integration
        Assert.That(
            WorkLegacyImportAuthenticatedReadDiagnostics.probe observer (subject kind)
            |> isUnknown, Is.True)
    finally
        if Directory.Exists root then Directory.Delete(root, true)

[<Test>]
let ``an asserted original source-effect high-water without remote journal is Unknown`` () =
    let first = originalCapture ()
    let missing =
        first.LedgerCaptures
        |> List.map (fun l ->
            if l.Target.ProjectId = Some (fst projects.Head) then
                { l with Fence = { l.Fence with OriginalSourceEffectHighWater = 1L } }
            else l)
    let initial = { first with LedgerCaptures = missing }
    let unjournaled = reader (Ok initial) (Ok initial) (fun t -> Ok t)
    Assert.That(
        WorkLegacyImportAuthenticatedReadDiagnostics.probe unjournaled (subject WorkSqliteRecordKind.ExternalEffect)
        |> isUnknown, Is.True)

[<Test>]
let ``distinct source-effect anchor cannot be missing substituted or digest-forged`` () =
    let first = originalCapture ()
    let pId, ledgerId = projects.Head
    let operationId = Guid.NewGuid()
    let originalOperation =
        { OperationId = operationId; Sequence = 1L
          RequestSha256 = hash; ResultSha256 = hash
          Kind = "original-source-stage"; TerminalResult = None }
    let effect = target OriginalGitRefPurpose.SourceEffect (Some pId) ledgerId
    let event =
        { Sequence = 1L; OperationId = operationId
          RequestSha256 = hash; ResultSha256 = hash
          Outcome = WorkLegacySourceEffectOutcome.Observed sha }
    let digest = WorkLegacySourceWitness.effectJournalDigest effect 1L [ event ]
    let journal =
        { Target = effect; RemoteHead = sha; Events = [ event ]
          DurableHighWater = 1L; RemoteHighWater = 1L
          DurableDigestSha256 = digest; RemoteDigestSha256 = digest }
    let ledgers =
        first.LedgerCaptures
        |> List.map (fun l ->
            if l.Target.ProjectId = Some pId then
                { l with Operations = [ originalOperation ]
                         Fence = { l.Fence with OriginalSourceEffectHighWater = 1L }
                         EffectJournal = Some journal }
            else l)
    let inspected =
        { first with
            Inspection =
                { first.Inspection with
                    Projects =
                        first.Inspection.Projects |> List.map (fun p ->
                            if p.ProjectId = pId then
                                { p with Operations = [ originalOperation ] }
                            else p) }
            LedgerCaptures = ledgers }
    let missingAnchor = reader (Ok inspected) (Ok inspected)
                               (fun _ -> Error(WorkLegacySourceReadFailure.NoAuthenticatedBinding "no publisher authority"))
    Assert.That(WorkLegacyImportAuthenticatedReadDiagnostics.probe missingAnchor (subject WorkSqliteRecordKind.ExternalEffect)
                |> isIncomplete, Is.True)
    let substitutedAnchor = reader (Ok inspected) (Ok inspected)
                                   (fun _ -> Ok { effect with GitRef = "refs/heads/another-effect" })
    Assert.That(WorkLegacyImportAuthenticatedReadDiagnostics.probe substitutedAnchor (subject WorkSqliteRecordKind.ExternalEffect)
                |> isRejected, Is.True)
    let forgedJournalEntries =
        inspected.LedgerCaptures
        |> List.map (fun l ->
            if l.Target.ProjectId = Some pId then
                { l with EffectJournal = Some { journal with RemoteDigestSha256 = hash } }
            else l)
    let malformed = { inspected with LedgerCaptures = forgedJournalEntries }
    let falseDigest = reader (Ok malformed) (Ok malformed) (fun _ -> Ok effect)
    Assert.That(WorkLegacyImportAuthenticatedReadDiagnostics.probe falseDigest (subject WorkSqliteRecordKind.ExternalEffect)
                |> isUnknown, Is.True)
    // A coherent fixture still never returns a production positive result.
    let selfConsistent = reader (Ok inspected) (Ok inspected) (fun _ -> Ok effect)
    Assert.That(WorkLegacyImportAuthenticatedReadDiagnostics.probe selfConsistent (subject WorkSqliteRecordKind.ExternalEffect)
                |> isIncomplete, Is.True)

[<Test>]
let ``malformed nested original source capture returns typed Unknown instead of throwing`` () =
    let initial = originalCapture ()
    let corrupted = { initial with Inspection = Unchecked.defaultof<WorkLegacyImportInspection> }
    let mock = reader (Ok corrupted) (Ok corrupted) (fun target -> Ok target)
    Assert.That(WorkLegacyImportAuthenticatedReadDiagnostics.probe mock (subject WorkSqliteRecordKind.Project)
                |> isUnknown, Is.True)


// Regression matrix for Reviewer2 sequence/identity/digest/target findings.
// Each malformed receipt is internally digest-consistent and has matching
// original operations, so classification must not rely on a hash mismatch.
let private journalEvent sequence operationId =
    { Sequence = sequence; OperationId = operationId
      RequestSha256 = hash; ResultSha256 = hash
      Outcome = WorkLegacySourceEffectOutcome.Observed sha }

let private journalProbe (events: WorkLegacySourceEffectEvent list)
                         (change: WorkLegacySourceEffectJournal -> WorkLegacySourceEffectJournal) =
    let initial = originalCapture ()
    let projectId, ledgerId = projects.Head
    let effectTarget = target OriginalGitRefPurpose.SourceEffect (Some projectId) ledgerId
    let water = int64 events.Length
    let draft =
        { Target = effectTarget; RemoteHead = sha; Events = events
          DurableHighWater = water; RemoteHighWater = water
          DurableDigestSha256 = hash; RemoteDigestSha256 = hash }
    let altered = change draft
    let digest =
        WorkLegacySourceWitness.effectJournalDigest
            altered.Target altered.DurableHighWater altered.Events
    let journal =
        { altered with DurableDigestSha256 = digest; RemoteDigestSha256 = digest }
    let originalOperations =
        events
        |> List.map (fun event ->
            { OperationId = event.OperationId; Sequence = event.Sequence
              RequestSha256 = event.RequestSha256; ResultSha256 = event.ResultSha256
              Kind = "original-source-stage"; TerminalResult = None })
    let captured =
        { initial with
            Inspection =
                { initial.Inspection with
                    Projects =
                        initial.Inspection.Projects
                        |> List.map (fun p ->
                            if p.ProjectId = projectId then
                                { p with Operations = originalOperations }
                            else p) }
            LedgerCaptures =
                initial.LedgerCaptures
                |> List.map (fun l ->
                    if l.Target.ProjectId = Some projectId then
                        { l with Operations = originalOperations
                                 Fence = { l.Fence with OriginalSourceEffectHighWater = water }
                                 EffectJournal = Some journal }
                    else l) }
    reader (Ok captured) (Ok captured) (fun _ -> Ok journal.Target)
    |> fun original -> WorkLegacyImportAuthenticatedReadDiagnostics.probe
                           original
                           { SubjectKind = WorkSqliteRecordKind.ExternalEffect
                             SubjectProjectId = projectId
                             SubjectEntityId = events.Head.OperationId.ToString("D") }

[<Test>]
let ``duplicate out-of-order and skipped source effect sequences are Rejected despite matching digest`` () =
    let first = journalEvent 1L (Guid.NewGuid())
    let second = journalEvent 2L (Guid.NewGuid())
    for receiptEvents in
        [ [first; { second with Sequence = 1L }]
          [{ first with Sequence = 2L }; { second with Sequence = 1L }]
          [first; { second with Sequence = 3L }] ] do
        let gap = journalProbe receiptEvents id
        Assert.That(isRejected gap, Is.True, sprintf "Malformed ordered sequence: %A" gap)

[<Test>]
let ``reused source effect operation UUID is Rejected even with distinct sequences`` () =
    let first = journalEvent 1L (Guid.NewGuid())
    let second = { journalEvent 2L (Guid.NewGuid()) with OperationId = first.OperationId }
    let gap = journalProbe [first; second] id
    Assert.That(isRejected gap, Is.True, sprintf "Reused physical operation: %A" gap)

[<Test>]
let ``malformed original effect request and result SHA256 are Rejected`` () =
    let validEvent = journalEvent 1L (Guid.NewGuid())
    for malformed in
        [ { validEvent with RequestSha256 = "not-a-sha256" }
          { validEvent with ResultSha256 = "not-a-sha256" } ] do
        let gap = journalProbe [malformed] id
        Assert.That(isRejected gap, Is.True, sprintf "Invalid effect hash: %A" gap)

[<Test>]
let ``invalid observed physical Git OID is Unknown not innocently Incomplete`` () =
    let event = { journalEvent 1L (Guid.NewGuid()) with
                    Outcome = WorkLegacySourceEffectOutcome.Observed "not-a-git-oid" }
    let gap = journalProbe [event] id
    Assert.That(isUnknown gap, Is.True, sprintf "Unobservable original effect: %A" gap)

[<Test>]
let ``invalid original effect remote head and publisher ref are Rejected`` () =
    let events = [journalEvent 1L (Guid.NewGuid())]
    let invalidHead = journalProbe events (fun j -> { j with RemoteHead = "not-a-git-oid" })
    Assert.That(isRejected invalidHead, Is.True, sprintf "Invalid remote OID: %A" invalidHead)
    let invalidTarget =
        journalProbe events (fun j ->
            { j with Target = { j.Target with GitRef = "refs/heads/source-effects.lock" } })
    Assert.That(isRejected invalidTarget, Is.True, sprintf "Invalid publisher ref: %A" invalidTarget)
