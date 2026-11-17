namespace Jarvis.WorkRecovery

open System
open System.IO
open System.Security.Cryptography
open System.Text

// G0 only: pure, default-off recovery reasoning. This module does not read or
// mutate SQLite, Git, the Scheduler, live launches, or authorization grants.
// The future trusted adapter must verify every "Verified" fact against its
// canonical provenance before supplying it to this boundary.

[<RequireQualifiedAccess>]
type StopFence =
    | Active
    | Unknown
    | Cleared of receiptId: string * version: int * canonicallyVerified: bool

[<RequireQualifiedAccess>]
type ArtifactGoal =
    | Unchanged
    | SourceProduced
    | AcceptedReview
    | Integrated

[<RequireQualifiedAccess>]
type ReviewVerdict =
    | Accepted
    | ChangesRequested
    | Rejected

// The trusted adapter must inspect these fields on the ORIGINAL, immutable
// proof/authorization, not fill them from the current Work. A source SHA alone
// cannot carry a review or approval across a Work/graph/role revision.
type OriginalStageLineage =
    { WorkVersion: int
      Generation: int
      ProjectGraphVersion: int
      RoleAuthoritySha: string }

type SourceProof =
    { ProjectId: string
      WorkId: string
      RepositoryId: string
      SourceRef: string
      SourceSha: string
      OriginalLineage: OriginalStageLineage
      ReceiptId: string
      ReachabilityVerified: bool }

type FrozenReviewProof =
    { ProjectId: string
      WorkId: string
      ReviewId: string
      OriginalLineage: OriginalStageLineage
      SourceRepositoryId: string
      SourceRef: string
      SourceReceiptId: string
      FrozenSourceSha: string
      Verdict: ReviewVerdict
      ReceiptId: string
      CanonicallyVerified: bool }

// This proves the original authorization TO INTEGRATE, separately from the
// later Recovery release. A verified old approval is not a new-stage grant.
type IntegrationAuthorizationProof =
    { ReceiptId: string
      OriginalLineage: OriginalStageLineage
      SourceRepositoryId: string
      SourceRef: string
      SourceReceiptId: string
      SourceSha: string
      AcceptedReviewId: string
      AcceptedReviewReceiptId: string
      TargetRef: string
      CanonicallyVerified: bool }

type IntegrationProof =
    { ProjectId: string
      WorkId: string
      SubmissionId: string
      OriginalLineage: OriginalStageLineage
      SourceRepositoryId: string
      SourceRef: string
      AcceptedReviewId: string
      AcceptedReviewReceiptId: string
      Authorization: IntegrationAuthorizationProof
      AcceptedSourceSha: string
      TargetRef: string
      IntegratedSha: string
      ReceiptId: string
      ContainsSourceVerified: bool
      CanonicallyVerified: bool }

[<RequireQualifiedAccess>]
type EffectKind =
    | Launch
    | Claim
    | SourcePush
    | IntegrationPush

[<RequireQualifiedAccess>]
type EffectOutcome =
    | Unknown
    // A negative result needs proof that the original job is terminal; it is
    // never inferred from a missing local row or missing remote Git ref.
    | DefinitiveNoEffect of receiptId: string * canonicallyVerified: bool * jobsReconciled: bool
    // An observation also needs a reconciled original job. For a launch/claim,
    // a live session is unsafe until its original generation is terminal.
    | Observed of receiptId: string * canonicallyVerified: bool * jobsReconciled: bool * terminal: bool

type ExternalEffect =
    { OperationId: string
      WorkId: string
      Generation: int
      Kind: EffectKind
      SourceSha: string option
      Outcome: EffectOutcome }

// These identities must come from the ORIGINAL captured launch records, not
// from whichever journal entries happen to be visible after restoration.
// Operation IDs are account-global one-shot identities; the project, Work and
// generation bind each captured launch to its original execution scope.
type CapturedLaunchIdentity =
    { ProjectId: string
      WorkId: string
      Generation: int
      OperationId: string }

type SequenceGapSeal =
    { ProjectId: string
      RestoredProjectSequence: int64
      KnownPreviousProjectSequence: int64
      CoveredThroughSequence: int64
      ReceiptId: string
      CanonicallyVerified: bool
      WorkGraphCovered: bool
      ExternalEffectsCovered: bool }

// The account-scoped proof must cover projects created or changed *after*
// the snapshot. A restored list of projects cannot attest its own completeness.
type AccountGapSeal =
    { SnapshotDatabaseSequence: int64
      KnownPreviousDatabaseSequence: int64
      CoveredThroughDatabaseSequence: int64
      ReceiptId: string
      CanonicallyVerified: bool
      ProjectSetCovered: bool
      ExternalEffectsCovered: bool }

type ReleaseAuthorization =
    { ProjectId: string
      WorkId: string
      ReceiptId: string
      AuthorizationRevision: int64
      CanonicallyVerified: bool
      SnapshotDatabaseSequence: int64
      ReconciledThroughProjectSequence: int64
      ProjectGraphVersion: int
      WorkVersion: int
      Generation: int
      RoleAuthoritySha: string
      // Receipt IDs are informational. The digest binds their ORIGINAL proof
      // payloads, lineage, all effect outcomes and STOP/gap-seal contents.
      EvidenceReceipts: Set<string>
      EvidenceDigest: string }

type RecoveryWorkSnapshot =
    { WorkId: string
      WorkVersion: int
      Generation: int
      RoleAuthoritySha: string
      Goal: ArtifactGoal
      // True with no identities means the original launch identity was lost:
      // the Recovery role must investigate; no other launch may clear it.
      CapturedInFlightLaunch: bool
      CapturedLaunches: CapturedLaunchIdentity list
      Source: SourceProof option
      Review: FrozenReviewProof option
      Integration: IntegrationProof option
      Effects: ExternalEffect list
      Release: ReleaseAuthorization option }

type RecoveryProjectSnapshot =
    { ProjectId: string
      GraphVersion: int
      RestoredSequence: int64
      KnownPreviousSequence: int64 option
      Stop: StopFence
      GapSeal: SequenceGapSeal option
      Works: RecoveryWorkSnapshot list }

type RecoveryInput =
    { SchemaVersion: int
      SnapshotDatabaseSequence: int64
      KnownPreviousDatabaseSequence: int64 option
      CompleteProjectSetVerified: bool
      AccountGapSeal: AccountGapSeal option
      GlobalStop: StopFence
      Projects: RecoveryProjectSnapshot list }

[<RequireQualifiedAccess>]
type Requirement =
    | UnsupportedSchema
    | UnverifiedProjectSet
    | UnknownDatabaseHighWater
    | InvalidDatabaseSequence
    | AccountSequenceGapSealRequired
    | GlobalStopClearance
    | ProjectStopClearance
    | UnknownProjectHighWater
    | InvalidProjectSequence
    | SequenceGapSealRequired
    | DuplicateProjectIdentity
    | DuplicateWorkIdentity
    | InvalidWorkVersion
    | InvalidRoleAuthority
    | MissingSourceProof
    | UnverifiedSourceProof
    | UnexpectedSourceArtifact
    | FreshIndependentReviewRequired
    | InvalidFrozenReview
    | VerifiedIntegrationRequired
    | InvalidIntegration
    | InFlightLaunchReconciliation
    | InvalidEffectJournal
    | UnknownExternalEffect of operationId: string
    | UnreconciledExternalEffect of operationId: string
    | ConflictingExternalEffect of operationId: string
    | RecoveryAuthorizationRequired

type RecoveryQueueItem =
    { Id: string
      ProjectId: string
      WorkId: string option
      Queue: string
      Requirements: Requirement list }

type RecoveryWorkDecision =
    { WorkId: string
      Requirements: Requirement list
      EvidenceReceipts: Set<string>
      EvidenceDigest: string
      ReleaseAuthorized: bool
      NormalDispatchAllowed: bool }

type RecoveryProjectDecision =
    { ProjectId: string
      Requirements: Requirement list
      Works: RecoveryWorkDecision list
      NormalDispatchAllowed: bool }

type RecoveryPlan =
    { GlobalRequirements: Requirement list
      Projects: RecoveryProjectDecision list
      RecoveryQueue: RecoveryQueueItem list
      NormalDispatchAllowed: bool }

module WorkRecovery =
    let private ordered requirements = requirements |> List.distinct |> List.sort

    let private validId (value: string) = not (String.IsNullOrWhiteSpace value)

    let private validSha (value: string) =
        not (isNull value)
        && (value.Length = 40 || value.Length = 64)
        && (value |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))

    let private stopRequirements isGlobal status =
        match status with
        | StopFence.Cleared (receipt, version, true) when validId receipt && version > 0 -> []
        | _ -> [ if isGlobal then Requirement.GlobalStopClearance else Requirement.ProjectStopClearance ]

    let private stopReceipts scope status =
        match status with
        | StopFence.Cleared (receipt, version, true) when validId receipt && version > 0 ->
            Set.singleton (sprintf "stop:%s:%d:%s" scope version receipt)
        | _ -> Set.empty

    let private accountGapProof (input: RecoveryInput) =
        match input.KnownPreviousDatabaseSequence, input.AccountGapSeal with
        | Some last, Some seal
            when last > input.SnapshotDatabaseSequence
                 && seal.SnapshotDatabaseSequence = input.SnapshotDatabaseSequence
                 && seal.KnownPreviousDatabaseSequence = last
                 && seal.CoveredThroughDatabaseSequence >= last
                 && seal.CanonicallyVerified
                 && seal.ProjectSetCovered
                 && seal.ExternalEffectsCovered
                 && validId seal.ReceiptId -> Some ("account-gap:" + seal.ReceiptId)
        | _ -> None

    let private accountGapRequirements (input: RecoveryInput) =
        match input.KnownPreviousDatabaseSequence with
        | Some last when last > input.SnapshotDatabaseSequence && (accountGapProof input |> Option.isNone) ->
            [ Requirement.AccountSequenceGapSealRequired ]
        | _ -> []

    let private stableId (parts: string list) =
        parts |> List.map (fun part -> sprintf "%d:%s" part.Length part) |> String.concat "/"

    let private queueItem project work requirements =
        { Id =
            match work with
            | None -> stableId [ "recovery"; project; "project" ]
            | Some workId -> stableId [ "recovery"; project; "work"; workId ]
          ProjectId = project
          WorkId = work
          Queue = "Recovery"
          Requirements = ordered requirements }

    let private gapRequirements (project: RecoveryProjectSnapshot) =
        match project.KnownPreviousSequence with
        | None -> [ Requirement.UnknownProjectHighWater ]
        | Some last when last < project.RestoredSequence -> [ Requirement.InvalidProjectSequence ]
        | Some last when last = project.RestoredSequence -> []
        | Some last ->
            match project.GapSeal with
            | Some seal
                when seal.ProjectId = project.ProjectId
                     && seal.RestoredProjectSequence = project.RestoredSequence
                     && seal.KnownPreviousProjectSequence = last
                     && seal.CoveredThroughSequence >= last
                     && seal.CanonicallyVerified
                     && seal.WorkGraphCovered
                     && seal.ExternalEffectsCovered
                     && validId seal.ReceiptId -> []
            | _ -> [ Requirement.SequenceGapSealRequired ]

    let private gapReceipts (project: RecoveryProjectSnapshot) =
        match project.KnownPreviousSequence, project.GapSeal with
        | Some last, Some seal
            when last > project.RestoredSequence
                 && seal.ProjectId = project.ProjectId
                 && seal.RestoredProjectSequence = project.RestoredSequence
                 && seal.KnownPreviousProjectSequence = last
                 && seal.CoveredThroughSequence >= last
                 && seal.CanonicallyVerified
                 && seal.WorkGraphCovered
                 && seal.ExternalEffectsCovered
                 && validId seal.ReceiptId ->
            Set.singleton ("gap:" + seal.ReceiptId)
        | _ -> Set.empty

    let private lineageMatches (project: RecoveryProjectSnapshot) (work: RecoveryWorkSnapshot) lineage =
        validSha work.RoleAuthoritySha
        && lineage =
            { WorkVersion = work.WorkVersion
              Generation = work.Generation
              ProjectGraphVersion = project.GraphVersion
              RoleAuthoritySha = work.RoleAuthoritySha }

    let private sourceReceipt (project: RecoveryProjectSnapshot) (work: RecoveryWorkSnapshot) =
        match work.Source with
        | Some source
            when source.ProjectId = project.ProjectId
                 && source.WorkId = work.WorkId
                 && lineageMatches project work source.OriginalLineage
                 && validId source.RepositoryId
                 && not (isNull source.SourceRef)
                 && source.SourceRef.StartsWith("refs/heads/", StringComparison.Ordinal)
                 && validSha source.SourceSha
                 && source.ReachabilityVerified
                 && validId source.ReceiptId -> Some ("source:" + source.ReceiptId)
        | _ -> None

    let private reviewReceipt (project: RecoveryProjectSnapshot) (work: RecoveryWorkSnapshot) =
        match work.Source, work.Review with
        | Some source, Some review
            when (sourceReceipt project work |> Option.isSome)
                 && review.ProjectId = project.ProjectId
                 && review.WorkId = work.WorkId
                 && lineageMatches project work review.OriginalLineage
                 && review.CanonicallyVerified
                 && review.Verdict = ReviewVerdict.Accepted
                 && validId review.ReviewId
                 && validId review.ReceiptId
                 && review.SourceRepositoryId = source.RepositoryId
                 && review.SourceRef = source.SourceRef
                 && review.SourceReceiptId = source.ReceiptId
                 && review.FrozenSourceSha = source.SourceSha ->
            Some ("review:" + review.ReceiptId)
        | _ -> None

    let private integrationReceipt (project: RecoveryProjectSnapshot) (work: RecoveryWorkSnapshot) =
        match work.Source, work.Review, work.Integration with
        | Some source, Some review, Some integration
            when (reviewReceipt project work |> Option.isSome)
                 && integration.ProjectId = project.ProjectId
                 && integration.WorkId = work.WorkId
                 && lineageMatches project work integration.OriginalLineage
                 && integration.CanonicallyVerified
                 && integration.ContainsSourceVerified
                 && not (isNull (box integration.Authorization))
                 && integration.Authorization.CanonicallyVerified
                 && lineageMatches project work integration.Authorization.OriginalLineage
                 && validId integration.Authorization.ReceiptId
                 && integration.Authorization.SourceRepositoryId = source.RepositoryId
                 && integration.Authorization.SourceRef = source.SourceRef
                 && integration.Authorization.SourceReceiptId = source.ReceiptId
                 && integration.Authorization.SourceSha = source.SourceSha
                 && integration.Authorization.AcceptedReviewId = review.ReviewId
                 && integration.Authorization.AcceptedReviewReceiptId = review.ReceiptId
                 && integration.Authorization.TargetRef = integration.TargetRef
                 && validId integration.SubmissionId
                 && validId integration.ReceiptId
                 && not (isNull integration.TargetRef)
                 && integration.TargetRef.StartsWith("refs/heads/", StringComparison.Ordinal)
                 && validSha integration.IntegratedSha
                 && integration.SourceRepositoryId = source.RepositoryId
                 && integration.SourceRef = source.SourceRef
                 && integration.AcceptedReviewId = review.ReviewId
                 && integration.AcceptedReviewReceiptId = review.ReceiptId
                 && integration.AcceptedSourceSha = source.SourceSha ->
            Some ("integration:" + integration.ReceiptId)
        | _ -> None

    let private artifactRequirements (project: RecoveryProjectSnapshot) (work: RecoveryWorkSnapshot) =
        let sourceOk = sourceReceipt project work |> Option.isSome
        let reviewOk = reviewReceipt project work |> Option.isSome
        let integrationOk = integrationReceipt project work |> Option.isSome
        let sourceRequired = work.Goal <> ArtifactGoal.Unchanged
        let reviewRequired = work.Goal = ArtifactGoal.AcceptedReview || work.Goal = ArtifactGoal.Integrated
        let integrationRequired = work.Goal = ArtifactGoal.Integrated
        [ if not sourceRequired && (work.Source.IsSome || work.Review.IsSome || work.Integration.IsSome) then
              Requirement.UnexpectedSourceArtifact
          if sourceRequired && work.Source.IsNone then Requirement.MissingSourceProof
          if sourceRequired && work.Source.IsSome && not sourceOk then Requirement.UnverifiedSourceProof
          if reviewRequired && work.Review.IsNone then Requirement.FreshIndependentReviewRequired
          if reviewRequired && work.Review.IsSome && not reviewOk then Requirement.InvalidFrozenReview
          if integrationRequired && work.Integration.IsNone then Requirement.VerifiedIntegrationRequired
          if integrationRequired && work.Integration.IsSome && not integrationOk then Requirement.InvalidIntegration ]

    let private effectAssessment (project: RecoveryProjectSnapshot) (work: RecoveryWorkSnapshot) =
        let badDuplicates =
            work.Effects
            |> List.groupBy (fun effect -> effect.OperationId)
            |> List.choose (fun (id, effects) ->
                if not (validId id) || (effects |> List.distinct |> List.length > 1) then
                    Some id
                else None)

        let effects = work.Effects |> List.distinct |> List.sortBy (fun effect -> effect.OperationId)
        let mutable requirements = []
        let mutable receipts = Set.empty

        let capturedDuplicate =
            work.CapturedLaunches
            |> List.countBy id
            |> List.exists (fun (_, count) -> count > 1)

        let capturedLaunchSettled (captured: CapturedLaunchIdentity) =
            validId captured.OperationId
            && captured.ProjectId = project.ProjectId
            && captured.WorkId = work.WorkId
            && captured.Generation = work.Generation
            && (effects |> List.exists (fun effect ->
                effect.OperationId = captured.OperationId
                && effect.WorkId = captured.WorkId
                && effect.Generation = captured.Generation
                && effect.Kind = EffectKind.Launch
                && effect.SourceSha.IsNone
                && (match effect.Outcome with
                    | EffectOutcome.DefinitiveNoEffect (receipt, true, true) -> validId receipt
                    | EffectOutcome.Observed (receipt, true, true, true) -> validId receipt
                    | _ -> false)))

        // A captured but unidentified launch is unknown, even if an unrelated
        // launch from the same Work/generation has a verified terminal result.
        // An inconsistent "not captured" flag and nonempty identity set also
        // fails closed instead of silently ignoring original launch evidence.
        if work.CapturedInFlightLaunch <> (not work.CapturedLaunches.IsEmpty)
           || capturedDuplicate
           || not (work.CapturedLaunches |> List.forall capturedLaunchSettled) then
            requirements <- Requirement.InFlightLaunchReconciliation :: requirements

        for id in badDuplicates do
            requirements <- Requirement.ConflictingExternalEffect id :: requirements

        for effect in effects do
            if not (validId effect.OperationId)
               || effect.WorkId <> work.WorkId
               || effect.Generation <> work.Generation then
                requirements <- Requirement.InvalidEffectJournal :: requirements
            else
                let result =
                    match effect.Outcome with
                    | EffectOutcome.Unknown -> Some (Requirement.UnknownExternalEffect effect.OperationId), None
                    | EffectOutcome.DefinitiveNoEffect (receipt, verified, jobs)
                        when verified && jobs && validId receipt ->
                        None, Some ("effect:" + effect.OperationId + ":none:" + receipt)
                    | EffectOutcome.Observed (receipt, verified, jobs, terminal)
                        when verified && jobs && validId receipt ->
                        let terminalRequired =
                            effect.Kind = EffectKind.Launch || effect.Kind = EffectKind.Claim
                        if terminalRequired && not terminal then
                            Some (Requirement.UnreconciledExternalEffect effect.OperationId), None
                        else
                            let matchesSource =
                                match effect.Kind, effect.SourceSha, work.Source, work.Integration with
                                | EffectKind.SourcePush, Some sha, Some source, _ -> sha = source.SourceSha && (sourceReceipt project work |> Option.isSome)
                                | EffectKind.IntegrationPush, Some sha, _, Some integration ->
                                    sha = integration.IntegratedSha && (integrationReceipt project work |> Option.isSome)
                                | EffectKind.Launch, None, _, _
                                | EffectKind.Claim, None, _, _ -> true
                                | _ -> false
                            if matchesSource then
                                None, Some ("effect:" + effect.OperationId + ":observed:" + receipt)
                            else Some (Requirement.UnreconciledExternalEffect effect.OperationId), None
                    | _ -> Some (Requirement.UnreconciledExternalEffect effect.OperationId), None
                match result with
                | Some requirement, _ -> requirements <- requirement :: requirements
                | _, Some receipt -> receipts <- Set.add receipt receipts
                | _ -> ()

        ordered requirements, receipts

    // Deterministic, length-prefixed binary transcript of the exact typed
    // recovery evidence. Never use caller-defined receipt IDs as the identity
    // of a proof: the same ID may accompany changed Git/ref/role/job payloads.
    // Sort effect rows to preserve set semantics without losing duplicates.
    // Exclude only Release itself to avoid a circular authorization digest.
    let private evidenceFingerprint
        (input: RecoveryInput)
        (project: RecoveryProjectSnapshot)
        (work: RecoveryWorkSnapshot)
        (receipts: Set<string>) =
        use stream = new MemoryStream()
        use writer = new BinaryWriter(stream, Encoding.UTF8, true)
        let writeString (value: string) =
            writer.Write(not (isNull value))
            if not (isNull value) then writer.Write(value)
        let writeInt (value: int) = writer.Write(value)
        let writeLong (value: int64) = writer.Write(value)
        let writeBool (value: bool) = writer.Write(value)
        let writeOption writeValue value =
            match value with
            | None -> writeBool false
            | Some entry -> writeBool true; writeValue entry
        let writeList writeValue (entries: 'a list) =
            writeInt entries.Length
            List.iter writeValue entries
        let writeLineage (lineage: OriginalStageLineage) =
            writeInt lineage.WorkVersion
            writeInt lineage.Generation
            writeInt lineage.ProjectGraphVersion
            writeString lineage.RoleAuthoritySha
        let writeStop status =
            match status with
            | StopFence.Active -> writeInt 0
            | StopFence.Unknown -> writeInt 1
            | StopFence.Cleared (receipt, version, verified) ->
                writeInt 2
                writeString receipt
                writeInt version
                writeBool verified
        let writeGap (seal: SequenceGapSeal) =
            writeString seal.ProjectId
            writeLong seal.RestoredProjectSequence
            writeLong seal.KnownPreviousProjectSequence
            writeLong seal.CoveredThroughSequence
            writeString seal.ReceiptId
            writeBool seal.CanonicallyVerified
            writeBool seal.WorkGraphCovered
            writeBool seal.ExternalEffectsCovered
        let writeAccountGap (seal: AccountGapSeal) =
            writeLong seal.SnapshotDatabaseSequence
            writeLong seal.KnownPreviousDatabaseSequence
            writeLong seal.CoveredThroughDatabaseSequence
            writeString seal.ReceiptId
            writeBool seal.CanonicallyVerified
            writeBool seal.ProjectSetCovered
            writeBool seal.ExternalEffectsCovered
        let writeSource (source: SourceProof) =
            writeString source.ProjectId
            writeString source.WorkId
            writeString source.RepositoryId
            writeString source.SourceRef
            writeString source.SourceSha
            writeLineage source.OriginalLineage
            writeString source.ReceiptId
            writeBool source.ReachabilityVerified
        let writeReview (review: FrozenReviewProof) =
            writeString review.ProjectId
            writeString review.WorkId
            writeString review.ReviewId
            writeLineage review.OriginalLineage
            writeString review.SourceRepositoryId
            writeString review.SourceRef
            writeString review.SourceReceiptId
            writeString review.FrozenSourceSha
            writeInt (match review.Verdict with
                      | ReviewVerdict.Accepted -> 0
                      | ReviewVerdict.ChangesRequested -> 1
                      | ReviewVerdict.Rejected -> 2)
            writeString review.ReceiptId
            writeBool review.CanonicallyVerified
        let writeIntegrationAuthorization (authorization: IntegrationAuthorizationProof) =
            writeString authorization.ReceiptId
            writeLineage authorization.OriginalLineage
            writeString authorization.SourceRepositoryId
            writeString authorization.SourceRef
            writeString authorization.SourceReceiptId
            writeString authorization.SourceSha
            writeString authorization.AcceptedReviewId
            writeString authorization.AcceptedReviewReceiptId
            writeString authorization.TargetRef
            writeBool authorization.CanonicallyVerified
        let writeIntegration (integration: IntegrationProof) =
            writeString integration.ProjectId
            writeString integration.WorkId
            writeString integration.SubmissionId
            writeLineage integration.OriginalLineage
            writeString integration.SourceRepositoryId
            writeString integration.SourceRef
            writeString integration.AcceptedReviewId
            writeString integration.AcceptedReviewReceiptId
            writeIntegrationAuthorization integration.Authorization
            writeString integration.AcceptedSourceSha
            writeString integration.TargetRef
            writeString integration.IntegratedSha
            writeString integration.ReceiptId
            writeBool integration.ContainsSourceVerified
            writeBool integration.CanonicallyVerified
        let writeEffect (effect: ExternalEffect) =
            writeString effect.OperationId
            writeString effect.WorkId
            writeInt effect.Generation
            writeInt (match effect.Kind with
                      | EffectKind.Launch -> 0
                      | EffectKind.Claim -> 1
                      | EffectKind.SourcePush -> 2
                      | EffectKind.IntegrationPush -> 3)
            writeOption writeString effect.SourceSha
            match effect.Outcome with
            | EffectOutcome.Unknown -> writeInt 0
            | EffectOutcome.DefinitiveNoEffect (receipt, verified, reconciled) ->
                writeInt 1
                writeString receipt
                writeBool verified
                writeBool reconciled
            | EffectOutcome.Observed (receipt, verified, reconciled, terminal) ->
                writeInt 2
                writeString receipt
                writeBool verified
                writeBool reconciled
                writeBool terminal

        let writeCapturedLaunch (captured: CapturedLaunchIdentity) =
            writeString captured.ProjectId
            writeString captured.WorkId
            writeInt captured.Generation
            writeString captured.OperationId

        // Version this encoding before adding or reordering any fields.
        writeInt 2
        writeInt input.SchemaVersion
        writeLong input.SnapshotDatabaseSequence
        writeOption writeLong input.KnownPreviousDatabaseSequence
        writeBool input.CompleteProjectSetVerified
        writeOption writeAccountGap input.AccountGapSeal
        writeStop input.GlobalStop
        writeString project.ProjectId
        writeInt project.GraphVersion
        writeLong project.RestoredSequence
        writeOption writeLong project.KnownPreviousSequence
        writeStop project.Stop
        writeOption writeGap project.GapSeal
        writeString work.WorkId
        writeInt work.WorkVersion
        writeInt work.Generation
        writeString work.RoleAuthoritySha
        writeInt (match work.Goal with
                  | ArtifactGoal.Unchanged -> 0
                  | ArtifactGoal.SourceProduced -> 1
                  | ArtifactGoal.AcceptedReview -> 2
                  | ArtifactGoal.Integrated -> 3)
        writeBool work.CapturedInFlightLaunch
        writeList writeCapturedLaunch (work.CapturedLaunches |> List.sort)
        writeOption writeSource work.Source
        writeOption writeReview work.Review
        writeOption writeIntegration work.Integration
        writeList writeEffect (work.Effects |> List.sort)
        writeList writeString (receipts |> Set.toList)
        writer.Flush()
        SHA256.HashData(stream.ToArray()) |> Convert.ToHexString |> fun h -> h.ToLowerInvariant()

    let private assessWork
        (input: RecoveryInput)
        (globalRequirements: Requirement list)
        (project: RecoveryProjectSnapshot)
        (projectRequirements: Requirement list)
        (work: RecoveryWorkSnapshot) =
        let effectRequirements, effectReceipts = effectAssessment project work
        let evidence =
            [ sourceReceipt project work; reviewReceipt project work; integrationReceipt project work ]
            |> List.choose id
            |> Set.ofList
            |> Set.union effectReceipts
            |> Set.union (gapReceipts project)
            |> Set.union (accountGapProof input |> Option.toList |> Set.ofList)
            |> Set.union (stopReceipts "account" input.GlobalStop)
            |> Set.union (stopReceipts ("project:" + project.ProjectId) project.Stop)
        let evidenceDigest = evidenceFingerprint input project work evidence
        let prerequisites =
            globalRequirements @ projectRequirements @ artifactRequirements project work @ effectRequirements
            @ [ if work.WorkVersion <= 0 || work.Generation <= 0 then Requirement.InvalidWorkVersion
                if not (validSha work.RoleAuthoritySha) then Requirement.InvalidRoleAuthority ]
            |> ordered
        let authorized =
            match project.KnownPreviousSequence, work.Release with
            | Some last, Some release
                when prerequisites.IsEmpty
                     && release.ProjectId = project.ProjectId
                     && release.WorkId = work.WorkId
                     && validId release.ReceiptId
                     && release.AuthorizationRevision > 0L
                     && release.CanonicallyVerified
                     && release.SnapshotDatabaseSequence = input.SnapshotDatabaseSequence
                     && release.ReconciledThroughProjectSequence = last
                     && release.ProjectGraphVersion = project.GraphVersion
                     && release.WorkVersion = work.WorkVersion
                     && release.Generation = work.Generation
                     && release.RoleAuthoritySha = work.RoleAuthoritySha
                     && release.EvidenceReceipts = evidence
                     && release.EvidenceDigest = evidenceDigest -> true
            | _ -> false
        let requirements =
            if prerequisites.IsEmpty && not authorized then
                [ Requirement.RecoveryAuthorizationRequired ]
            else prerequisites
        { WorkId = work.WorkId
          Requirements = requirements
          EvidenceReceipts = evidence
          EvidenceDigest = evidenceDigest
          ReleaseAuthorized = authorized
          NormalDispatchAllowed = authorized && work.Goal = ArtifactGoal.Unchanged }

    /// Pure output only: the Scheduler must separately verify current DB
    /// versions, authorization and STOP within its own single CAS transaction.
    /// Never run this directly on untrusted caller-provided "Verified" booleans.
    let plan (input: RecoveryInput) : RecoveryPlan =
        // An operation ID is one-shot across the entire authoritative account,
        // not merely within a stage or project. Reused IDs must not produce
        // two independent "settled" effects after a stale restore.
        let crossScopeOperations =
            input.Projects
            |> List.collect (fun project ->
                project.Works
                |> List.collect (fun work ->
                    work.Effects |> List.map (fun effect -> effect.OperationId, project.ProjectId, work.WorkId)))
            |> List.groupBy (fun (operationId, _, _) -> operationId)
            |> List.choose (fun (operationId, entries) ->
                if (entries |> List.map (fun (_, project, work) -> project, work) |> List.distinct |> List.length) > 1 then
                    Some operationId
                else None)
        let globalRequirements =
            [ if input.SchemaVersion <> 1 then Requirement.UnsupportedSchema
              if not input.CompleteProjectSetVerified then Requirement.UnverifiedProjectSet
              if input.SnapshotDatabaseSequence < 0L then Requirement.InvalidDatabaseSequence
              match input.KnownPreviousDatabaseSequence with
              | None -> Requirement.UnknownDatabaseHighWater
              | Some before when before < input.SnapshotDatabaseSequence -> Requirement.InvalidDatabaseSequence
              | _ -> ()
              yield! accountGapRequirements input
              yield! stopRequirements true input.GlobalStop
              for operationId in crossScopeOperations do
                  Requirement.ConflictingExternalEffect operationId
              for _, duplicate in input.Projects |> List.countBy (fun p -> p.ProjectId) do
                  if duplicate > 1 then Requirement.DuplicateProjectIdentity ]
            |> ordered
        let projects =
            input.Projects
            |> List.sortBy (fun project -> project.ProjectId)
            |> List.map (fun project ->
                let duplicateWork = project.Works |> List.countBy (fun work -> work.WorkId) |> List.exists (fun (_, count) -> count > 1)
                let projectRequirements =
                    [ if not (validId project.ProjectId)
                         || project.GraphVersion <= 0
                         || project.RestoredSequence < 0L
                         || project.RestoredSequence > input.SnapshotDatabaseSequence then
                          Requirement.InvalidProjectSequence
                      match input.KnownPreviousDatabaseSequence, project.KnownPreviousSequence with
                      | Some db, Some previous when previous > db -> Requirement.InvalidProjectSequence
                      | _ -> ()
                      if duplicateWork then Requirement.DuplicateWorkIdentity
                      yield! stopRequirements false project.Stop
                      yield! gapRequirements project ]
                    |> ordered
                let works =
                    project.Works
                    |> List.sortBy (fun work -> work.WorkId)
                    |> List.map (assessWork input globalRequirements project projectRequirements)
                { ProjectId = project.ProjectId
                  Requirements = projectRequirements
                  Works = works
                  NormalDispatchAllowed = globalRequirements.IsEmpty
                                          && projectRequirements.IsEmpty
                                          && (works |> List.exists (fun work -> work.NormalDispatchAllowed)) })
        let queue =
            [ if not globalRequirements.IsEmpty then
                  yield queueItem "(global)" None globalRequirements
              for project in projects do
                  if not project.Requirements.IsEmpty then
                      yield queueItem project.ProjectId None project.Requirements
                  for work in project.Works do
                      if not work.Requirements.IsEmpty then
                          yield queueItem project.ProjectId (Some work.WorkId) work.Requirements ]
        { GlobalRequirements = globalRequirements
          Projects = projects
          RecoveryQueue = queue
          NormalDispatchAllowed = projects |> List.exists (fun project -> project.NormalDispatchAllowed) }
