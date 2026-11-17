namespace Jarvis.WorkRecovery

open System

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

type SourceProof =
    { ProjectId: string
      WorkId: string
      RepositoryId: string
      SourceRef: string
      SourceSha: string
      Generation: int
      ReceiptId: string
      ReachabilityVerified: bool }

type FrozenReviewProof =
    { ProjectId: string
      WorkId: string
      ReviewId: string
      FrozenSourceSha: string
      Verdict: ReviewVerdict
      ReceiptId: string
      CanonicallyVerified: bool }

type IntegrationProof =
    { ProjectId: string
      WorkId: string
      SubmissionId: string
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
      // Exact evidence binding rejects a stale approval after any proof,
      // journal, STOP or gap-seal changes.
      EvidenceReceipts: Set<string> }

type RecoveryWorkSnapshot =
    { WorkId: string
      WorkVersion: int
      Generation: int
      Goal: ArtifactGoal
      CapturedInFlightLaunch: bool
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

    let private sourceReceipt projectId (work: RecoveryWorkSnapshot) =
        match work.Source with
        | Some source
            when source.ProjectId = projectId
                 && source.WorkId = work.WorkId
                 && source.Generation = work.Generation
                 && validId source.RepositoryId
                 && source.SourceRef.StartsWith("refs/heads/", StringComparison.Ordinal)
                 && validSha source.SourceSha
                 && source.ReachabilityVerified
                 && validId source.ReceiptId -> Some ("source:" + source.ReceiptId)
        | _ -> None

    let private reviewReceipt projectId (work: RecoveryWorkSnapshot) =
        match work.Source, work.Review with
        | Some source, Some review
            when (sourceReceipt projectId work |> Option.isSome)
                 && review.ProjectId = projectId
                 && review.WorkId = work.WorkId
                 && review.CanonicallyVerified
                 && review.Verdict = ReviewVerdict.Accepted
                 && validId review.ReviewId
                 && validId review.ReceiptId
                 && review.FrozenSourceSha = source.SourceSha ->
            Some ("review:" + review.ReceiptId)
        | _ -> None

    let private integrationReceipt projectId (work: RecoveryWorkSnapshot) =
        match work.Source, work.Integration with
        | Some source, Some integration
            when (reviewReceipt projectId work |> Option.isSome)
                 && integration.ProjectId = projectId
                 && integration.WorkId = work.WorkId
                 && integration.CanonicallyVerified
                 && integration.ContainsSourceVerified
                 && validId integration.SubmissionId
                 && validId integration.ReceiptId
                 && integration.TargetRef.StartsWith("refs/heads/", StringComparison.Ordinal)
                 && validSha integration.IntegratedSha
                 && integration.AcceptedSourceSha = source.SourceSha ->
            Some ("integration:" + integration.ReceiptId)
        | _ -> None

    let private artifactRequirements projectId (work: RecoveryWorkSnapshot) =
        let sourceOk = sourceReceipt projectId work |> Option.isSome
        let reviewOk = reviewReceipt projectId work |> Option.isSome
        let integrationOk = integrationReceipt projectId work |> Option.isSome
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

    let private effectAssessment projectId (work: RecoveryWorkSnapshot) =
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

        if work.CapturedInFlightLaunch
           && not (effects |> List.exists (fun effect ->
               effect.Kind = EffectKind.Launch && effect.WorkId = work.WorkId && effect.Generation = work.Generation)) then
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
                                | EffectKind.SourcePush, Some sha, Some source, _ -> sha = source.SourceSha && (sourceReceipt projectId work |> Option.isSome)
                                | EffectKind.IntegrationPush, Some sha, _, Some integration ->
                                    sha = integration.IntegratedSha && (integrationReceipt projectId work |> Option.isSome)
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

    let private assessWork
        (input: RecoveryInput)
        (globalRequirements: Requirement list)
        (project: RecoveryProjectSnapshot)
        (projectRequirements: Requirement list)
        (work: RecoveryWorkSnapshot) =
        let effectRequirements, effectReceipts = effectAssessment project.ProjectId work
        let evidence =
            [ sourceReceipt project.ProjectId work; reviewReceipt project.ProjectId work; integrationReceipt project.ProjectId work ]
            |> List.choose id
            |> Set.ofList
            |> Set.union effectReceipts
            |> Set.union (gapReceipts project)
            |> Set.union (accountGapProof input |> Option.toList |> Set.ofList)
            |> Set.union (stopReceipts "account" input.GlobalStop)
            |> Set.union (stopReceipts ("project:" + project.ProjectId) project.Stop)
        let prerequisites =
            globalRequirements @ projectRequirements @ artifactRequirements project.ProjectId work @ effectRequirements
            @ [ if work.WorkVersion <= 0 || work.Generation <= 0 then Requirement.InvalidWorkVersion ]
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
                     && release.EvidenceReceipts = evidence -> true
            | _ -> false
        let requirements =
            if prerequisites.IsEmpty && not authorized then
                [ Requirement.RecoveryAuthorizationRequired ]
            else prerequisites
        { WorkId = work.WorkId
          Requirements = requirements
          EvidenceReceipts = evidence
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
