namespace Jarvis.WorkQueues

open System
open System.Text.RegularExpressions
open System.Text
open System.IO
open System.Security.Cryptography
open Server

// C -> B source boundary. The original Git publisher contract is not yet
// implemented or hosted. These DTOs are NOT permission grants; only a future
// authenticated original-source reader may construct authoritative evidence.
// No production reader is registered here and no scheduler/API calls this code.

[<RequireQualifiedAccess>]
type WorkLegacySourceEffectOutcome =
    | NoExternalEffect
    | Observed of string
    | Unknown

type WorkLegacySourceEffectEvent =
    { Sequence: int64
      OperationId: Guid
      RequestSha256: string
      ResultSha256: string
      Outcome: WorkLegacySourceEffectOutcome }

type WorkLegacySourceEffectJournal =
    { Target: OriginalGitPublisherTarget
      RemoteHead: string
      Events: WorkLegacySourceEffectEvent list
      DurableHighWater: int64
      RemoteHighWater: int64
      DurableDigestSha256: string
      RemoteDigestSha256: string }

// A source effect journal MUST be anchored by an independent, durable remote
// audit, rather than the local SQLite journal alone. Pre-STOP SQLite rollback
// can erase an UNKNOWN external push, including its local pending intent.
type WorkLegacySourceLedgerCapture =
    { Target: OriginalGitPublisherTarget
      CurrentRemoteHead: string
      Operations: WorkLegacyImportOperation list
      PendingIntentHighWater: int64
      ResolvedIntentHighWater: int64
      UnresolvedIntentIds: Guid list
      Fence: WorkLegacyGitFenceReceipt
      EffectJournal: WorkLegacySourceEffectJournal option }

type WorkLegacySourceLineageCapture =
    { ProjectId: Guid
      WorkId: string
      CanonicalRepositoryId: string
      ApprovedRemoteFingerprintSha256: string
      SourceRef: string
      SourceCommit: string
      OriginalOperationId: Guid
      OriginalRequestSha256: string
      ReachabilityReceiptSha256: string }

// Reader must derive all fields independently from authenticated original
// ledger/ref inventory, original transaction receipts, durable STOP admission
// authority, source Git, and externally anchored effect journal. It must NOT
// just echo a caller-supplied C0 inspection or fixture names. Both reads must
// fence stale writers and use the same canonical account/project identity.
// There is deliberately no production implementation of this interface yet.
type WorkLegacyAuthenticatedSourceCapture =
    { Inspection: WorkLegacyImportInspection
      C2Receipt: WorkLegacyFencedOfflineReceipt
      Projection: WorkLegacyProjectionEvidence
      AccountCapacityVersion: int64
      LedgerCaptures: WorkLegacySourceLedgerCapture list
      SourceLineages: WorkLegacySourceLineageCapture list
      OriginalBarrier: OriginalGitPublisherBarrierReceipt }

[<RequireQualifiedAccess>]
type WorkLegacySourceReadFailure =
    | Unavailable of string
    | NoAuthenticatedBinding of string
    | UncertainEffect of string

type IWorkLegacyAuthenticatedSourceReader =
    abstract member ReadAuthenticated: unit -> Result<WorkLegacyAuthenticatedSourceCapture, WorkLegacySourceReadFailure>
    // Reinspection is a NEW authenticated read (not a cached echo). It must
    // detect delayed original pushes and mutations after the STOP barrier.
    abstract member ReinspectAuthenticated: unit -> Result<WorkLegacyAuthenticatedSourceCapture, WorkLegacySourceReadFailure>

[<RequireQualifiedAccess>]
type WorkLegacySourceWitnessGap =
    | Incomplete of string
    | Unknown of string
    | Rejected of string

type WorkLegacySourceWitness =
    { AccountId: Guid
      AccountLedgerId: Guid
      AccountCapacityVersion: int64
      AccountHead: string
      AccountReserved: int
      OperatorDecisionId: Guid
      ImportOperationId: Guid
      OriginalBarrierEpoch: int64
      SourceRefs: (OriginalGitPublisherTarget * string * int64) list
      StopGenerations: (Guid * int64 * int64) list
      EffectHighWaters: (Guid * int64 * int64) list
      SourceLineages: WorkLegacySourceLineageCapture list
      Roles: (Guid * WorkLegacyProjectionRole) list
      Claims: (Guid * string * string * int) list
      Stages: (Guid * string * int * int) list
      FrozenReviews: (Guid * string * string * bool) list
      Integrations: (Guid * string * string * string) list
      Launches: (Guid * string * string * int64) list
      OperationIds: (Guid * Guid) list }

// Pure, fail-closed validation plus read-only checks against a disposable C1
// SQLite receipt. A successful *fixture* report never activates or migrates
// live Work, retires Git writers, or asserts that a production source exists.
[<RequireQualifiedAccess>]
module internal WorkLegacySourceWitness =
    let private oid = Regex("^[0-9a-f]{40}([0-9a-f]{24})?$", RegexOptions.Compiled)
    let private sha = Regex("^[0-9a-f]{64}$", RegexOptions.Compiled)
    let private gitRef = Regex("^refs/heads/[A-Za-z0-9][A-Za-z0-9._/-]*$", RegexOptions.Compiled)

    let private valid (re: Regex) (value: string) =
        not (String.IsNullOrWhiteSpace value) && re.IsMatch value

    let private rejected why = Error(WorkLegacySourceWitnessGap.Rejected why)
    let private unknown why = Error(WorkLegacySourceWitnessGap.Unknown why)
    let private incomplete why = Error(WorkLegacySourceWitnessGap.Incomplete why)
    let private require condition gap = if condition then Ok () else Error gap

    type private ResultBuilder() =
        member _.Bind(value, next) = Result.bind next value
        member _.Return(value) = Ok value
        member _.ReturnFrom(value) = value
    let private result = ResultBuilder()

    let private readerCall phase (read: unit -> Result<_, WorkLegacySourceReadFailure>) =
        try
            match read() with
            | Ok value -> Ok value
            | Error(WorkLegacySourceReadFailure.NoAuthenticatedBinding why) ->
                incomplete (phase + ": " + why)
            | Error(WorkLegacySourceReadFailure.Unavailable why)
            | Error(WorkLegacySourceReadFailure.UncertainEffect why) ->
                unknown (phase + ": " + why)
        with _ -> unknown (phase + ": original-source query threw or outcome is unknown")

    let private capturesMatch (expected: WorkLegacyImportInspection)
                              (observed: WorkLegacyImportInspection) =
        // F# structural equality is safe for the full typed C0 snapshot: it
        // includes both ledger identities, every operation and all queue,
        // graph, role, claim, review, integration and launch fields.
        expected = observed

    // Versioned, length-prefixed UTF-8 and fixed-width little-endian integers.
    // The remote reader must independently authenticate the anchor containing
    // this digest; hashing alone does not establish remote authority.
    let effectJournalDigest (target: OriginalGitPublisherTarget) (highWater: int64)
                            (events: WorkLegacySourceEffectEvent list) =
        use stream = new MemoryStream()
        use writer = new BinaryWriter(stream, Encoding.UTF8, true)
        let writeString (value: string) = writer.Write(value)
        writeString "jarvis.work.legacy-source-effect-journal.v1"
        writeString (match target.Purpose with
                     | OriginalGitRefPurpose.ProjectLedger -> "project-ledger"
                     | OriginalGitRefPurpose.AccountLedger -> "account-ledger"
                     | OriginalGitRefPurpose.SourceEffect -> "source-effect")
        writeString (target.AccountId.ToString("D"))
        match target.ProjectId with
        | Some id -> writer.Write(true); writeString (id.ToString("D"))
        | None -> writer.Write(false)
        writeString (target.LedgerId.ToString("D"))
        writer.Write(target.LedgerEpoch)
        writeString target.CanonicalRepositoryId
        writeString target.ApprovedRemoteFingerprintSha256
        writeString target.GitRef
        writer.Write(highWater)
        writer.Write(events.Length)
        for event in events do
            writer.Write(event.Sequence)
            writeString (event.OperationId.ToString("D"))
            writeString event.RequestSha256
            writeString event.ResultSha256
            match event.Outcome with
            | WorkLegacySourceEffectOutcome.NoExternalEffect -> writeString "none"
            | WorkLegacySourceEffectOutcome.Unknown -> writeString "unknown"
            | WorkLegacySourceEffectOutcome.Observed commit ->
                writeString "observed"
                writeString commit
        writer.Flush()
        SHA256.HashData(stream.ToArray()) |> Convert.ToHexStringLower

    let private checkEffectJournal (project: WorkLegacyImportProject)
                                   (ledger: WorkLegacySourceLedgerCapture) =
        match ledger.EffectJournal with
        | None -> incomplete "Original source-effect journal/remote anchor is not bound."
        | Some journal ->
            result {
                let! () = require (
                    Result.isOk (OriginalGitPublisherAdmission.validateTarget journal.Target)
                     && journal.Target.Purpose = OriginalGitRefPurpose.SourceEffect
                     && journal.Target.AccountId = ledger.Target.AccountId
                     && journal.Target.ProjectId = Some project.ProjectId
                     && journal.Target.CanonicalRepositoryId = ledger.Target.CanonicalRepositoryId
                     && valid oid journal.RemoteHead) (WorkLegacySourceWitnessGap.Rejected "Untrusted or wrong-scope source-effect remote/ref.")
                let! () = require (
                    journal.DurableHighWater >= 0L
                     && journal.DurableHighWater = journal.RemoteHighWater
                     && int64 journal.Events.Length = journal.DurableHighWater
                     && journal.DurableHighWater = ledger.Fence.OriginalSourceEffectHighWater
                     && valid sha journal.DurableDigestSha256
                     && journal.DurableDigestSha256 = journal.RemoteDigestSha256) (WorkLegacySourceWitnessGap.Unknown "Original effect audit high-water/digest diverges; pre-STOP journal rollback or lost acknowledgement is possible.")
                let! () = require (
                    journal.Events
                     |> List.mapi (fun index effect ->
                         effect.Sequence = int64 index + 1L
                         && effect.OperationId <> Guid.Empty
                         && valid sha effect.RequestSha256
                         && valid sha effect.ResultSha256)
                     |> List.forall id) (WorkLegacySourceWitnessGap.Rejected "Missing, out-of-order or invalid original effect receipt.")
                let! () = require (
                    (journal.Events |> List.map (fun effect -> effect.OperationId) |> Set.ofList).Count = journal.Events.Length) (WorkLegacySourceWitnessGap.Rejected "Duplicate one-shot original source-effect operation.")
                let! () = require (
                    journal.Events |> List.forall (fun effect ->
                        project.Operations |> List.exists (fun operation ->
                            operation.OperationId = effect.OperationId
                            && operation.RequestSha256 = effect.RequestSha256
                            && operation.ResultSha256 = effect.ResultSha256))) (WorkLegacySourceWitnessGap.Unknown "External effect is not covered by an immutable original operation receipt.")
                let! () = require (
                    journal.Events |> List.forall (fun effect ->
                        match effect.Outcome with
                        | WorkLegacySourceEffectOutcome.NoExternalEffect -> true
                        | WorkLegacySourceEffectOutcome.Observed commit -> valid oid commit
                        | WorkLegacySourceEffectOutcome.Unknown -> false)) (WorkLegacySourceWitnessGap.Unknown "Unresolved or unobserved original external effect.")
                let! () = require (
                    effectJournalDigest journal.Target journal.DurableHighWater journal.Events = journal.RemoteDigestSha256) (WorkLegacySourceWitnessGap.Unknown "Original effect audit digest does not bind target, high-water and events.")
                return journal.Target
            }

    let private checkLedger (inspection: WorkLegacyImportInspection)
                            (receipt: WorkLegacyFencedOfflineReceipt)
                            (ledger: WorkLegacySourceLedgerCapture) =
        result {
            let account = inspection.Account
            let isAccount = ledger.Target.Purpose = OriginalGitRefPurpose.AccountLedger
            let project =
                if isAccount then None
                else inspection.Projects |> List.tryFind (fun p ->
                    ledger.Target.ProjectId = Some p.ProjectId)
            let! () = require (
                Result.isOk (OriginalGitPublisherAdmission.validateTarget ledger.Target)
                 && ledger.Target.AccountId = account.AccountId
                 && (isAccount || (ledger.Target.Purpose = OriginalGitRefPurpose.ProjectLedger && project.IsSome))) (WorkLegacySourceWitnessGap.Rejected "Original Git authority/ref is not canonical or independently scoped.")
            let expectedLedgerId, expectedEpoch, expectedHead, operations, expectedWater =
                match project with
                | Some p -> p.LedgerId, p.LedgerEpoch, p.Head.PinnedCommit, p.Operations, p.Head.SourceHighWater
                | None -> account.LedgerId, account.LedgerEpoch, account.Head.PinnedCommit, account.Operations, account.Head.SourceHighWater
            let fence = ledger.Fence
            let! () = require (
                ledger.Target.LedgerId = expectedLedgerId
                 && ledger.Target.LedgerEpoch = expectedEpoch
                 && ledger.Operations = operations
                 && fence.Binding.LedgerId = expectedLedgerId
                 && fence.Binding.LedgerEpoch = expectedEpoch
                 && fence.Binding.EntityId =
                    (match project with Some p -> p.ProjectId | None -> account.AccountId)
                 && fence.Binding.Scope =
                    (if isAccount then WorkLegacyGitScope.Account else WorkLegacyGitScope.Project)
                 && fence.OriginalHead = expectedHead
                 && fence.FencedHead = ledger.CurrentRemoteHead
                 && valid oid ledger.CurrentRemoteHead
                 && fence.FenceHighWater = expectedWater + 1L
                 && fence.OriginalHighWater = expectedWater
                 && receipt.OriginalWriterFence |> List.contains fence) (WorkLegacySourceWitnessGap.Rejected "Original ledger/ref/head/journal mismatches authenticated C0 and STOP receipt.")
            let! () = require (
                fence.DecisionId = receipt.ApprovalId
                 && fence.FenceOperationId <> Guid.Empty
                 && fence.WriterGeneration > 0L
                 && fence.NoNewOriginalMutations
                 && fence.DurableUntilExplicitCutover
                 && fence.AllOriginalClientsQuiesced
                 && fence.MinimumClientProtocolVersion > 0
                 && valid sha fence.AllClientQuiescenceProofSha256
                 && valid sha fence.AuditSha256) (WorkLegacySourceWitnessGap.Unknown "Original STOP/barrier does not prove all-client durable quiescence.")
            let! () = require (
                ledger.PendingIntentHighWater >= 0L
                 && ledger.PendingIntentHighWater = fence.OriginalPendingIntentHighWater
                 && ledger.ResolvedIntentHighWater = ledger.PendingIntentHighWater
                 && ledger.UnresolvedIntentIds.IsEmpty) (WorkLegacySourceWitnessGap.Unknown "Pre-STOP intent/UNKNOWN outcome may have been rolled back or not reconciled.")
            let! effect =
                match project with
                | Some p -> checkEffectJournal p ledger |> Result.map Some
                | None ->
                    if ledger.EffectJournal.IsNone && fence.OriginalSourceEffectHighWater = 0L then Ok None
                    else unknown "Account-side source-effect history is not independently reconciled."
            return effect
        }

    let private checkLineage (inspection: WorkLegacyImportInspection)
                             (projection: WorkLegacyProjectionEvidence)
                             (lineages: WorkLegacySourceLineageCapture list) =
        let origins =
            inspection.Projects
            |> List.collect (fun (project: WorkLegacyImportProject) ->
                project.Origins |> List.map (fun origin -> project, origin))
        result {
            let! () = require (
                lineages.Length = origins.Length
                 && (lineages |> List.map (fun l -> l.ProjectId, l.WorkId) |> Set.ofList).Count = lineages.Length) (WorkLegacySourceWitnessGap.Incomplete "Authenticated source/ref/operation lineage set is incomplete or duplicated.")
            let verified (project: WorkLegacyImportProject) (origin: WorkLegacyImportOrigin) =
                let matching =
                    lineages |> List.filter (fun l ->
                        l.ProjectId = project.ProjectId && l.WorkId = origin.WorkId)
                match matching with
                | [ l ] ->
                    let role =
                        projection.Projects
                        |> List.tryFind (fun p -> p.ProjectId = project.ProjectId)
                        |> Option.bind (fun p ->
                            p.Roles |> List.tryFind (fun r -> r.RoleId = origin.RoleId))
                    valid sha l.CanonicalRepositoryId
                    && valid sha l.ApprovedRemoteFingerprintSha256
                    && valid sha l.ReachabilityReceiptSha256
                    && valid gitRef l.SourceRef
                    && valid oid l.SourceCommit
                    && l.CanonicalRepositoryId = origin.RepositoryId
                    && l.SourceRef = origin.SourceRef
                    && l.SourceCommit = origin.SourceCommit
                    && origin.SourceHead = project.Head.PinnedCommit
                    && (project.Operations |> List.exists (fun operation ->
                        operation.OperationId = l.OriginalOperationId
                        && operation.RequestSha256 = l.OriginalRequestSha256))
                    && (role |> Option.exists (fun r ->
                        r.Version = origin.RoleVersion
                        && r.SourceHead = project.Head.PinnedCommit
                        && valid sha r.InstructionsDigest
                        && r.EvidenceReceiptId <> Guid.Empty))
                | _ -> false
            let! () = require (
                origins |> List.forall (fun (project, origin) -> verified project origin)) (WorkLegacySourceWitnessGap.Incomplete "Authenticated original source operation/remote/ref/commit, role version or reachability proof is missing or stale.")
            return ()
        }

    // C1 project identity only; the accepted projection validates all
    // imported payload bytes separately. Never promote a SHA label alone.
    let private checkC1Project (store: WorkSqliteAuthority) (p: WorkLegacyImportProject) =
        let read name =
            let key =
                { ProjectId = p.ProjectId.ToString("D")
                  Kind = WorkSqliteRecordKind.Project
                  EntityId = name }
            match store.ReadRecord key with
            | Ok(Some row) when row.Version = 1L
                               && row.Value.SourceSha = Some p.Head.PinnedCommit
                               && row.Value.GraphVersion = int64 p.Graph.Version -> Ok ()
            | _ -> incomplete ("Missing or changed C1 imported project " + p.ProjectId.ToString("D") + "/" + name)
        result {
            let! () = read "legacy-queue"
            let! () = read "legacy-composition"
            return ()
        }

    let capture (reader: IWorkLegacyAuthenticatedSourceReader)
                (databasePath: string)
                (claimed: WorkLegacyImportInspection)
                (c2: WorkLegacyFencedOfflineReceipt)
                : Result<WorkLegacySourceWitness, WorkLegacySourceWitnessGap> =
        if isNull (box reader) then
            incomplete "No authenticated original-source reader is installed."
        elif not (WorkLegacyImportSqlite.isDisposableFixturePath databasePath) then
            rejected "Only a disposable offline C1 SQLite fixture may be examined."
        elif isNull (box claimed) || isNull (box c2) then
            rejected "Missing C0/C2 handover input."
        else
            result {
                let! index =
                    WorkLegacyImportPreflight.inspect claimed
                    |> Result.mapError (fun gaps ->
                        WorkLegacySourceWitnessGap.Rejected(sprintf "C0 preflight rejected: %A" gaps))
                let! first = readerCall "initial authenticated original capture" reader.ReadAuthenticated
                let! () = require (
                    capturesMatch claimed first.Inspection && first.C2Receipt = c2) (WorkLegacySourceWitnessGap.Rejected "Caller-provided source identity or C2 receipt differs from authenticated source.")
                let! () = require (
                    first.Projection.AccountId = claimed.Account.AccountId
                     && first.Projection.AccountLedgerId = claimed.Account.LedgerId
                     && first.Projection.AccountHead = claimed.Account.Head.PinnedCommit
                     && first.Projection.ApprovalId = c2.ApprovalId
                     && first.Projection.ImportOperationId = c2.Import.ImportOperationId
                     && first.Projection.Maximum = claimed.Account.MaxConcurrent
                     && first.Projection.Reserved = index.ReservedCapacity
                     && first.AccountCapacityVersion > 0L
                     && (first.Projection.Projects |> List.map (fun p -> p.ProjectId) |> List.sort) =
                        (claimed.Projects |> List.map (fun p -> p.ProjectId) |> List.sort)) (WorkLegacySourceWitnessGap.Incomplete "Original account capacity version, C1 projection or all-project scope is unproved.")
                let! () = require (
                    c2.Import.AccountId = index.AccountId
                     && c2.Import.SourceAccountHead = index.AccountHead
                     && c2.Import.ImportOperationId <> Guid.Empty
                     && (c2.Import.ProjectHeads |> List.sort) =
                        (claimed.Projects |> List.map (fun p -> p.ProjectId, p.Head.PinnedCommit) |> List.sort)) (WorkLegacySourceWitnessGap.Rejected "C1 import scope is inconsistent with canonical original heads.")
                let store = WorkSqliteAuthority databasePath
                let! imported =
                    store.ReadReceipt(c2.Import.ImportOperationId)
                    |> Result.mapError (fun _ ->
                        WorkLegacySourceWitnessGap.Incomplete "C1 SQLite receipt is unreadable.")
                let! () = require (
                    imported |> Option.exists (fun receipt ->
                        receipt.Sequence = c2.Import.SqliteSequence)) (WorkLegacySourceWitnessGap.Incomplete "Exact C1 import transaction receipt is not present.")
                let! accountRow =
                    store.ReadRecord
                        { ProjectId = index.AccountId.ToString("D")
                          Kind = WorkSqliteRecordKind.Project
                          EntityId = "legacy-account" }
                    |> Result.mapError (fun _ ->
                        WorkLegacySourceWitnessGap.Incomplete "C1 imported account record is unreadable.")
                let! () = require (
                    accountRow |> Option.exists (fun row ->
                        row.Value.SourceSha = Some index.AccountHead)) (WorkLegacySourceWitnessGap.Incomplete "Exact C1 account source-head record is absent.")
                let! () =
                    claimed.Projects
                    |> List.fold (fun acc p ->
                        acc |> Result.bind (fun () -> checkC1Project store p)) (Ok ())
                let targets = first.LedgerCaptures |> List.map (fun ledger -> ledger.Target)
                let expectedIds =
                    Set.ofList (claimed.Account.AccountId :: (claimed.Projects |> List.map (fun p -> p.ProjectId)))
                let! () = require (
                    first.LedgerCaptures.Length = expectedIds.Count
                     && (first.LedgerCaptures |> List.map (fun l -> l.Fence.Binding.EntityId) |> Set.ofList) = expectedIds
                     && (targets |> List.map (fun t -> t.CanonicalRepositoryId, t.GitRef) |> Set.ofList).Count = targets.Length
                     && (first.LedgerCaptures |> List.filter (fun l ->
                         l.Target.Purpose = OriginalGitRefPurpose.AccountLedger)).Length = 1) (WorkLegacySourceWitnessGap.Rejected "Incomplete or aliased original account/project Git authorities.")
                let! effects =
                    let rec loop acc = function
                        | [] -> Ok(List.rev acc)
                        | ledger :: rest ->
                            match checkLedger claimed c2 ledger with
                            | Error gap -> Error gap
                            | Ok journalTarget -> loop (journalTarget :: acc) rest
                    loop [] first.LedgerCaptures
                let allTargets = targets @ (effects |> List.choose id)
                let barrier = first.OriginalBarrier
                let! () = require (
                    barrier.FenceOperationId <> Guid.Empty
                     && barrier.FenceEpoch > 0L
                     && valid sha barrier.RemoteWitnessSha256
                     && (barrier.ExactTargets |> List.sort) = (allTargets |> List.sort)
                     && (first.LedgerCaptures |> List.forall (fun l ->
                         l.Fence.FenceOperationId = barrier.FenceOperationId
                         && l.Fence.OriginalPendingIntentHighWater <= barrier.OriginalPendingIntentHighWater
                         && l.Fence.OriginalSourceEffectHighWater <= barrier.OriginalSourceEffectHighWater))) (WorkLegacySourceWitnessGap.Unknown "Remote original STOP witness omits a ledger/effect target or original high-water.")
                let! () = checkLineage claimed first.Projection first.SourceLineages
                // STOP must be observed again AFTER reading C1; a stale first
                // read or a delayed original push is UNKNOWN, not permission.
                let! again = readerCall "post-C1 STOP/source reinspection" reader.ReinspectAuthenticated
                let! () = require (
                    again = first) (WorkLegacySourceWitnessGap.Unknown "Original source, pending effects, capacity or STOP barrier changed on reinspection.")
                let claims =
                    claimed.Projects
                    |> List.collect (fun p ->
                        p.Queue.Works
                        |> Map.toList
                        |> List.choose (fun (workId, stage) ->
                            match stage.State with
                            | WorkState.Claimed launchId ->
                                Some(p.ProjectId, workId, launchId, stage.ClaimGeneration)
                            | _ -> None))
                let launches =
                    claimed.Projects
                    |> List.collect (fun p ->
                        p.Queue.Launches
                        |> Map.toList
                        |> List.map (fun (_, launch) ->
                            p.ProjectId, launch.Id, launch.AccountTokenId, int64 launch.Version))
                return
                    { AccountId = index.AccountId
                      AccountLedgerId = claimed.Account.LedgerId
                      AccountCapacityVersion = first.AccountCapacityVersion
                      AccountHead = index.AccountHead
                      AccountReserved = index.ReservedCapacity
                      OperatorDecisionId = first.Projection.ApprovalId
                      ImportOperationId = c2.Import.ImportOperationId
                      OriginalBarrierEpoch = barrier.FenceEpoch
                      SourceRefs =
                        first.LedgerCaptures
                        |> List.map (fun l -> l.Target, l.CurrentRemoteHead, l.Fence.FenceHighWater)
                      StopGenerations =
                        first.LedgerCaptures
                        |> List.map (fun ledger ->
                            let id = ledger.Fence.Binding.EntityId
                            let projectionStop =
                                first.Projection.Projects
                                |> List.tryFind (fun p -> p.ProjectId = id)
                                |> Option.map (fun p -> p.StopGeneration)
                                |> Option.defaultValue 0L
                            id, projectionStop, ledger.Fence.WriterGeneration)
                      EffectHighWaters =
                        first.LedgerCaptures
                        |> List.map (fun ledger ->
                            ledger.Target.LedgerId,
                            ledger.Fence.OriginalPendingIntentHighWater,
                            ledger.Fence.OriginalSourceEffectHighWater)
                      SourceLineages = first.SourceLineages
                      Roles =
                        first.Projection.Projects
                        |> List.collect (fun p -> p.Roles |> List.map (fun role -> p.ProjectId, role))
                      Claims = claims
                      Stages = index.Stages
                      FrozenReviews = index.FrozenReviews
                      Integrations = index.Integrations
                      Launches = launches
                      OperationIds = index.OperationIds }
            }
