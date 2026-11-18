namespace Jarvis.WorkQueues

open System
open System.Text.RegularExpressions
open Server

// Diagnostic-only C preparation; not an original-Git reader registration.
// A source SHA, a caller's capture and a test-double ReadAuthenticated result
// never prove remote identity, the physical source-ref push, or all-writer STOP.
// The production C1/C2/SourceWitness path remains independently gated.
type internal WorkLegacyImportReadSubject =
    { SubjectKind: WorkSqliteRecordKind
      SubjectProjectId: Guid
      SubjectEntityId: string }

[<RequireQualifiedAccess>]
module internal WorkLegacyImportAuthenticatedReadDiagnostics =
    let private incomplete text = Error(WorkLegacySourceWitnessGap.Incomplete text)
    let private unknown text = Error(WorkLegacySourceWitnessGap.Unknown text)
    let private rejected text = Error(WorkLegacySourceWitnessGap.Rejected text)
    let private require condition gap = if condition then Ok () else Error gap

    // Match WorkLegacySourceWitness.checkEffectJournal. Its validator is private
    // to the Worker17-owned witness; this C-only negative probe cannot weaken
    // its receipt classification or infer authority from a matching digest.
    let private validGitOid (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && Regex.IsMatch(value, "^[0-9a-f]{40}([0-9a-f]{24})?$")
    let private validSha256 (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && Regex.IsMatch(value, "^[0-9a-f]{64}$")

    type private ResultBuilder() =
        member _.Bind(value, next) = Result.bind next value
        member _.Return(value) = Ok value
        member _.ReturnFrom(value) = value
    let private result = ResultBuilder()

    let private read phase (action: unit -> Result<'T, WorkLegacySourceReadFailure>) =
        try
            match action() with
            | Ok value when not (isNull (box value)) -> Ok value
            | Ok _ -> incomplete (phase + ": authenticated source returned no capture")
            | Error(WorkLegacySourceReadFailure.NoAuthenticatedBinding reason) ->
                incomplete (phase + ": " + reason)
            | Error(WorkLegacySourceReadFailure.Unavailable reason)
            | Error(WorkLegacySourceReadFailure.UncertainEffect reason) ->
                unknown (phase + ": " + reason)
        with _ ->
            unknown (phase + ": original-source read threw; external outcome is uncertain")

    let private expectedLedgers (capture: WorkLegacyAuthenticatedSourceCapture) =
        let account = capture.Inspection.Account
        [ OriginalGitRefPurpose.AccountLedger, None, account.LedgerId,
          account.LedgerEpoch, account.Head.PinnedCommit ]
        @ (capture.Inspection.Projects
           |> List.map (fun p ->
               OriginalGitRefPurpose.ProjectLedger, Some p.ProjectId, p.LedgerId,
               p.LedgerEpoch, p.Head.PinnedCommit))

    let private checkJournal (reader: IWorkLegacyAuthenticatedSourceReader)
                             (ledger: WorkLegacySourceLedgerCapture) =
        match ledger.EffectJournal with
        | None -> Ok ()
        | Some journal ->
            result {
                let target = journal.Target
                let! () = require (
                     Result.isOk (OriginalGitPublisherAdmission.validateTarget target)
                     && target.Purpose = OriginalGitRefPurpose.SourceEffect
                     && target.AccountId = ledger.Target.AccountId
                     && target.ProjectId = ledger.Target.ProjectId
                     && target.LedgerId = ledger.Target.LedgerId
                     && target.LedgerEpoch = ledger.Target.LedgerEpoch
                     && target.CanonicalRepositoryId = ledger.Target.CanonicalRepositoryId
                     && target.ApprovedRemoteFingerprintSha256 = ledger.Target.ApprovedRemoteFingerprintSha256
                     && validGitOid journal.RemoteHead) (WorkLegacySourceWitnessGap.Rejected "Untrusted or wrong-scope source-effect remote/ref.")
                // This call must be a separate original-publisher-domain read;
                // echoing the capture is only fixture evidence, never authority.
                let! anchor = read "independent source-effect anchor"
                                   (fun () -> reader.ReadEffectAnchor target)
                let! () = require (anchor = target) (WorkLegacySourceWitnessGap.Rejected "Independent effect remote/ref does not match the claimed stage.")
                let! () = require (journal.DurableHighWater = ledger.Fence.OriginalSourceEffectHighWater) (WorkLegacySourceWitnessGap.Unknown "Original source-effect high-water differs from STOP receipt.")
                let! () = require (journal.DurableHighWater >= 0L
                     && journal.RemoteHighWater = journal.DurableHighWater
                     && validSha256 journal.DurableDigestSha256
                     && journal.DurableDigestSha256 = journal.RemoteDigestSha256
                     && int64 journal.Events.Length = journal.DurableHighWater) (WorkLegacySourceWitnessGap.Unknown "Original physical source-effect high-water, receipt or pending push is uncertain.")
                let! () = require (
                     journal.Events
                     |> List.mapi (fun index effect ->
                         effect.Sequence = int64 index + 1L
                         && effect.OperationId <> Guid.Empty
                         && validSha256 effect.RequestSha256
                         && validSha256 effect.ResultSha256)
                     |> List.forall id) (WorkLegacySourceWitnessGap.Rejected "Missing, out-of-order or invalid original effect receipt.")
                let uniqueOperationIds = journal.Events |> List.map (fun effect -> effect.OperationId) |> Set.ofList
                let! () = require (uniqueOperationIds.Count = journal.Events.Length)
                            (WorkLegacySourceWitnessGap.Rejected "Duplicate one-shot original source-effect operation.")
                let! () = require (journal.Events |> List.forall (fun effect ->
                    ledger.Operations |> List.exists (fun op ->
                        op.OperationId = effect.OperationId
                        && op.RequestSha256 = effect.RequestSha256
                        && op.ResultSha256 = effect.ResultSha256))) (WorkLegacySourceWitnessGap.Unknown "Physical effect is not covered by a durable original operation receipt.")
                let! () = require (
                     journal.Events |> List.forall (fun effect ->
                         match effect.Outcome with
                         | WorkLegacySourceEffectOutcome.NoExternalEffect -> true
                         | WorkLegacySourceEffectOutcome.Observed commit -> validGitOid commit
                         | WorkLegacySourceEffectOutcome.Unknown -> false)) (WorkLegacySourceWitnessGap.Unknown "Unresolved or unobserved original external effect.")
                let! () = require (WorkLegacySourceWitness.effectJournalDigest
                        target journal.DurableHighWater journal.Events = journal.RemoteDigestSha256) (WorkLegacySourceWitnessGap.Unknown "Original effect receipts do not match the remote journal digest.")
                return ()
            }

    let private checkLedger (reader: IWorkLegacyAuthenticatedSourceReader)
                            (captured: WorkLegacyAuthenticatedSourceCapture)
                            (purpose, projectId, ledgerId, epoch, sourceHead) =
        result {
            let matches =
                captured.LedgerCaptures
                |> List.filter (fun l ->
                    l.Target.Purpose = purpose && l.Target.ProjectId = projectId)
            let! ledger =
                match matches with
                | [ only ] -> Ok only
                | [] -> incomplete (sprintf "No independently authenticated original %A/%A ledger binding." purpose projectId)
                | _ -> rejected "Duplicate original project/account source authorities."
            let target = ledger.Target
            let! () = require (target.AccountId = captured.Inspection.Account.AccountId
                 && target.LedgerId = ledgerId
                 && target.LedgerEpoch = epoch && epoch > 0
                 && not (String.IsNullOrWhiteSpace target.CanonicalRepositoryId)
                 && not (String.IsNullOrWhiteSpace target.ApprovedRemoteFingerprintSha256)
                 && not (String.IsNullOrWhiteSpace target.GitRef)
                 && ledger.Fence.Binding.LedgerId = ledgerId
                 && ledger.Fence.Binding.LedgerEpoch = epoch
                 && ledger.Fence.Binding.PinnedHead = sourceHead
                 && ledger.Fence.Binding.EntityId = defaultArg projectId captured.Inspection.Account.AccountId) (WorkLegacySourceWitnessGap.Rejected "Original account/project remote, epoch, ledger or STOP binding is inconsistent.")
            let! () = require (ledger.CurrentRemoteHead = ledger.Fence.FencedHead
                 && ledger.Fence.OriginalHead = sourceHead
                 && ledger.Fence.NoNewOriginalMutations
                 && ledger.Fence.AllOriginalClientsQuiesced
                 && ledger.Fence.DurableUntilExplicitCutover) (WorkLegacySourceWitnessGap.Unknown "Conflicting remote head or unverifiable complete original-writer STOP.")
            let! () = require (ledger.PendingIntentHighWater = ledger.ResolvedIntentHighWater
                 && ledger.PendingIntentHighWater = ledger.Fence.OriginalPendingIntentHighWater
                 && ledger.UnresolvedIntentIds.IsEmpty) (WorkLegacySourceWitnessGap.Unknown "Pre-journal/pending original intent or unknown physical effect remains.")
            let canonicalOperations =
                match projectId with
                | None -> captured.Inspection.Account.Operations
                | Some id ->
                    captured.Inspection.Projects
                    |> List.find (fun p -> p.ProjectId = id)
                    |> fun p -> p.Operations
            let! () = require (ledger.Operations = canonicalOperations) (WorkLegacySourceWitnessGap.Rejected "Original operation history differs from authenticated C0 ledger.")
            let! () = require (ledger.Fence.OriginalSourceEffectHighWater = 0L || ledger.EffectJournal.IsSome) (WorkLegacySourceWitnessGap.Unknown "STOP references source effects without independently readable remote journal.")
            let! () = checkJournal reader ledger
            return ()
        }

    let private checkScope reader (capture: WorkLegacyAuthenticatedSourceCapture) =
        result {
            let account = capture.Inspection.Account
            let projects = capture.Inspection.Projects
            let! () = require (account.AccountId <> Guid.Empty && account.LedgerId <> Guid.Empty
                 && not projects.IsEmpty
                 && (projects |> List.map (fun p -> p.ProjectId) |> Set.ofList).Count = projects.Length
                 && (projects |> List.map (fun p -> p.LedgerId) |> Set.ofList).Count = projects.Length
                 && (projects |> List.forall (fun p ->
                     p.ProjectId <> Guid.Empty && p.LedgerId <> Guid.Empty
                     && p.LedgerId <> account.LedgerId))) (WorkLegacySourceWitnessGap.Incomplete "Original account or complete nonaliased project scope is unproved.")
            let expected = expectedLedgers capture
            let! () = require (capture.LedgerCaptures.Length = expected.Length) (WorkLegacySourceWitnessGap.Incomplete "Missing or extra original source authorities.")
            let physicalRefs =
                capture.LedgerCaptures
                |> List.map (fun l -> l.Target.CanonicalRepositoryId, l.Target.GitRef)
            let! () = require ((physicalRefs |> Set.ofList).Count = physicalRefs.Length) (WorkLegacySourceWitnessGap.Rejected "Two original Git ledgers alias one physical remote/ref.")
            let! () =
                expected
                |> List.fold (fun state item ->
                    state |> Result.bind (fun () -> checkLedger reader capture item)) (Ok ())
            let orphan =
                capture.SourceLineages
                |> List.exists (fun l ->
                    match projects |> List.tryFind (fun p -> p.ProjectId = l.ProjectId) with
                    | None -> true
                    | Some p ->
                        not (p.Origins |> List.exists (fun o ->
                            o.WorkId = l.WorkId
                            && o.SourceRef = l.SourceRef
                            && o.SourceCommit = l.SourceCommit
                            && o.RepositoryId = l.CanonicalRepositoryId)))
            let! () = require (not orphan) (WorkLegacySourceWitnessGap.Unknown "Orphaned/changed source-ref effect exists without matching original ledger CAS lineage.")
            return ()
        }

    let private checkSubject (capture: WorkLegacyAuthenticatedSourceCapture)
                             (subject: WorkLegacyImportReadSubject) =
        let p = capture.Inspection.Projects |> List.tryFind (fun p -> p.ProjectId = subject.SubjectProjectId)
        let exists =
            p |> Option.exists (fun project ->
                match subject.SubjectKind with
                | WorkSqliteRecordKind.Project -> true
                | WorkSqliteRecordKind.Work
                | WorkSqliteRecordKind.Stage -> project.Queue.Works.ContainsKey subject.SubjectEntityId
                | WorkSqliteRecordKind.Launch -> project.Queue.Launches.ContainsKey subject.SubjectEntityId
                | WorkSqliteRecordKind.Role -> project.Queue.Subscriptions.ContainsKey subject.SubjectEntityId
                | WorkSqliteRecordKind.Review ->
                    project.Reviews |> List.exists (fun r -> r.WorkId = subject.SubjectEntityId)
                | WorkSqliteRecordKind.Integration ->
                    project.Integrations |> List.exists (fun i -> i.WorkId = subject.SubjectEntityId)
                | WorkSqliteRecordKind.ExternalEffect ->
                    capture.LedgerCaptures |> List.exists (fun l ->
                        l.Target.ProjectId = Some project.ProjectId
                        && (l.EffectJournal |> Option.exists (fun j ->
                            j.Events |> List.exists (fun e -> e.OperationId.ToString("D") = subject.SubjectEntityId)))))
        require exists
            (WorkLegacySourceWitnessGap.Incomplete(sprintf "No exact original %A subject or physical receipt %s." subject.SubjectKind subject.SubjectEntityId))

    // Compare every source-affecting field. Malformed fixture evidence must
    // become typed Unknown, not a thrown exception or accepted snapshot.
    let private unchanged (first: WorkLegacyAuthenticatedSourceCapture)
                          (again: WorkLegacyAuthenticatedSourceCapture) =
        try
            first.Inspection = again.Inspection
            && Object.Equals(box first.C2Receipt, box again.C2Receipt)
            && Object.Equals(box first.Projection, box again.Projection)
            && first.AccountCapacityVersion = again.AccountCapacityVersion
            && first.LedgerCaptures = again.LedgerCaptures
            && first.SourceLineages = again.SourceLineages
            && Object.Equals(box first.OriginalBarrier, box again.OriginalBarrier)
        with _ -> false

    // This is strictly an offline negative diagnostic. In particular Ok of a
    // caller-created/mock reader is NEVER a production Verified source or cutover.
    // Prerequisites belong to original Client source-ref staging, Server publisher
    // admission, authenticated account/project reader, complete STOP/high-water,
    // durable UUID/request/result/ref/OID lineage and separately attested remote
    // physical effect; those other owners are deliberately not implemented here.
    let probe (reader: IWorkLegacyAuthenticatedSourceReader)
              (subject: WorkLegacyImportReadSubject) : WorkLegacySourceWitnessGap =
        if isNull (box reader) then
            WorkLegacySourceWitnessGap.Incomplete "No authenticated original-Git reader registered."
        elif subject.SubjectProjectId = Guid.Empty || String.IsNullOrWhiteSpace subject.SubjectEntityId then
            WorkLegacySourceWitnessGap.Rejected "Missing exact kind/project/subject identity."
        else
            let inspected =
                try
                    result {
                        let! first = read "original source capture" reader.ReadAuthenticated
                        let! () = checkScope reader first
                        // Post-C1 is implemented by WorkLegacySourceWitness.capture.
                        // This earlier probe requires a fresh independent read too.
                        let! again = read "fresh original source reinspection" reader.ReinspectAuthenticated
                        let! () = require (unchanged first again) (WorkLegacySourceWitnessGap.Unknown "Original source/ref, ledger CAS or physical effect changed after capture.")
                        let! () = checkSubject first subject
                        return ()
                    }
                with _ ->
                    unknown "Malformed authenticated source fields or original effects cannot be trusted."
            match inspected with
            | Error gap -> gap
            | Ok () ->
                WorkLegacySourceWitnessGap.Incomplete
                    "Offline negative diagnostics cannot authenticate production Git binding, remote physical-effect receipt or live original-writer STOP."
