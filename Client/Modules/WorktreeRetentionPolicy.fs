namespace Client

open System

module WorktreeRetentionPolicy =
    /// Safety evidence is explicit: missing and ambiguous observations are not safe defaults.
    type Evidence<'T> =
        | Known of 'T
        | Unknown
        | Ambiguous

    type PressureState =
        | Normal
        | Reclaiming

    /// Low starts reclamation; high is both the healthy target and the hysteresis reset threshold.
    type Policy =
        { LowFreeBytes: int64
          HighFreeBytes: int64
          MinimumAge: TimeSpan }

    type Candidate =
        { WorktreeId: string
          AttemptTerminal: Evidence<bool>
          EngineeringJobsTerminal: Evidence<bool>
          NoActiveLease: Evidence<bool>
          NoActiveProcess: Evidence<bool>
          IsClean: Evidence<bool>
          IsUnlocked: Evidence<bool>
          IsDetached: Evidence<bool>
          IsIntegrated: Evidence<bool>
          TargetIsFresh: Evidence<bool>
          NoUniqueEvidence: Evidence<bool>
          AgeSinceLastActivity: Evidence<TimeSpan>
          EstimatedReclaimableBytes: Evidence<int64> }

    type SkipReason =
        | InvalidWorktreeId
        | DuplicateWorktreeId
        | AttemptNotTerminal
        | AttemptStatusUnknown
        | AttemptStatusAmbiguous
        | EngineeringJobsNotTerminal
        | EngineeringJobsStatusUnknown
        | EngineeringJobsStatusAmbiguous
        | ActiveLease
        | LeaseStatusUnknown
        | LeaseStatusAmbiguous
        | ActiveProcess
        | ProcessStatusUnknown
        | ProcessStatusAmbiguous
        | WorktreeDirty
        | CleanlinessUnknown
        | CleanlinessAmbiguous
        | WorktreeLocked
        | LockStatusUnknown
        | LockStatusAmbiguous
        | DetachedWorktree
        | DetachedStatusUnknown
        | DetachedStatusAmbiguous
        | NotIntegrated
        | IntegrationStatusUnknown
        | IntegrationStatusAmbiguous
        | TargetNotFresh
        | TargetFreshnessUnknown
        | TargetFreshnessAmbiguous
        | UniqueEvidencePresent
        | EvidenceScanUnknown
        | EvidenceScanAmbiguous
        | TooRecent
        | AgeUnknown
        | AgeAmbiguous
        | InvalidAge
        | ReclaimableBytesUnknown
        | ReclaimableBytesAmbiguous
        | NoReclaimableBytes
        | InvalidReclaimableBytes

    type PolicyError =
        | NegativeLowWatermark
        | HighWatermarkMustExceedLow
        | NegativeMinimumAge
        | NegativeFreeSpace

    type StopReason =
        | PressureNotTriggered
        | PressureCleared
        | TargetReached
        | NoEligibleCandidates
        | EligibleCapacityExhausted

    /// Names worktrees only; branch refs and committed history are outside this policy's authority.
    type PlannedCandidate =
        { WorktreeId: string
          AgeSinceLastActivity: TimeSpan
          ReclaimableBytes: int64 }

    type SkippedCandidate =
        { WorktreeId: string
          Reasons: SkipReason list }

    type Plan =
        { SelectedCandidates: PlannedCandidate list
          ExpectedReclaimedBytes: int64
          ProjectedFreeBytes: int64
          StopReason: StopReason
          PressureStateAfterPlan: PressureState
          SkippedCandidates: SkippedCandidate list
          SkippedByReason: Map<SkipReason, int> }

    let private gib = 1024L * 1024L * 1024L

    /// Proposal only; this policy is not wired to runtime cleanup or the Scheduler.
    ///
    /// The 2026-11-28 incident had about 6.6 GB free before cleanup and 71-75 GB
    /// afterwards on a 1.8 TB filesystem. A 40 GiB low watermark (~43 GB) leaves
    /// substantial room above the incident's critical free space. An 80 GiB high
    /// watermark (~86 GB) is modestly above the recovered headroom, while the
    /// 40 GiB hysteresis band avoids repeatedly starting and stopping cleanup.
    /// The trade-off is that reaching the high watermark can reclaim more storage
    /// than the incident cleanup did, increasing worktree setup cost. Seven days
    /// is a conservative age floor, not a value measured by the incident; validate
    /// it against actual engineering-work lifetimes before authorizing activation.
    let suggestedDefaults =
        { LowFreeBytes = 40L * gib
          HighFreeBytes = 80L * gib
          MinimumAge = TimeSpan.FromDays 7.0 }

    let private requireTrue negative unknown ambiguous (fact: Evidence<bool>) =
        match fact with
        | Known true -> []
        | Known false -> [ negative ]
        | Unknown -> [ unknown ]
        | Ambiguous -> [ ambiguous ]

    let private requireFalse positive unknown ambiguous (fact: Evidence<bool>) =
        match fact with
        | Known false -> []
        | Known true -> [ positive ]
        | Unknown -> [ unknown ]
        | Ambiguous -> [ ambiguous ]

    let private candidateSkipReasons policy duplicateIds (candidate: Candidate) =
        let ageReasons =
            match candidate.AgeSinceLastActivity with
            | Known age when age < TimeSpan.Zero -> [ InvalidAge ]
            | Known age when age < policy.MinimumAge -> [ TooRecent ]
            | Known _ -> []
            | Unknown -> [ AgeUnknown ]
            | Ambiguous -> [ AgeAmbiguous ]

        let reclaimableBytesReasons =
            match candidate.EstimatedReclaimableBytes with
            | Known bytes when bytes < 0L -> [ InvalidReclaimableBytes ]
            | Known 0L -> [ NoReclaimableBytes ]
            | Known _ -> []
            | Unknown -> [ ReclaimableBytesUnknown ]
            | Ambiguous -> [ ReclaimableBytesAmbiguous ]

        [ if String.IsNullOrWhiteSpace candidate.WorktreeId then
              yield InvalidWorktreeId
          elif duplicateIds |> List.exists (fun id -> StringComparer.Ordinal.Equals(id, candidate.WorktreeId)) then
              yield DuplicateWorktreeId

          yield!
              requireTrue AttemptNotTerminal AttemptStatusUnknown AttemptStatusAmbiguous
                  candidate.AttemptTerminal
          yield!
              requireTrue EngineeringJobsNotTerminal EngineeringJobsStatusUnknown EngineeringJobsStatusAmbiguous
                  candidate.EngineeringJobsTerminal
          yield! requireTrue ActiveLease LeaseStatusUnknown LeaseStatusAmbiguous candidate.NoActiveLease
          yield! requireTrue ActiveProcess ProcessStatusUnknown ProcessStatusAmbiguous candidate.NoActiveProcess
          yield! requireTrue WorktreeDirty CleanlinessUnknown CleanlinessAmbiguous candidate.IsClean
          yield! requireTrue WorktreeLocked LockStatusUnknown LockStatusAmbiguous candidate.IsUnlocked
          yield! requireFalse DetachedWorktree DetachedStatusUnknown DetachedStatusAmbiguous candidate.IsDetached
          yield! requireTrue NotIntegrated IntegrationStatusUnknown IntegrationStatusAmbiguous candidate.IsIntegrated
          yield! requireTrue TargetNotFresh TargetFreshnessUnknown TargetFreshnessAmbiguous candidate.TargetIsFresh
          yield! requireTrue UniqueEvidencePresent EvidenceScanUnknown EvidenceScanAmbiguous candidate.NoUniqueEvidence
          yield! ageReasons
          yield! reclaimableBytesReasons ]

    let private comparePlannedCandidates left right =
        let byAge = compare right.AgeSinceLastActivity left.AgeSinceLastActivity

        if byAge <> 0 then
            byAge
        else
            StringComparer.Ordinal.Compare(left.WorktreeId, right.WorktreeId)

    let private compareSkippedCandidates left right =
        let byId = StringComparer.Ordinal.Compare(left.WorktreeId, right.WorktreeId)

        if byId <> 0 then
            byId
        else
            compare left.Reasons right.Reasons

    let private saturatingAdd left right =
        if right > Int64.MaxValue - left then Int64.MaxValue
        else left + right

    let private validatePolicy policy freeBytes =
        if policy.LowFreeBytes < 0L then
            Error NegativeLowWatermark
        elif policy.HighFreeBytes <= policy.LowFreeBytes then
            Error HighWatermarkMustExceedLow
        elif policy.MinimumAge < TimeSpan.Zero then
            Error NegativeMinimumAge
        elif freeBytes < 0L then
            Error NegativeFreeSpace
        else
            Ok ()

    /// Produce a deterministic dry-run plan from already-observed facts.
    /// This function performs no Git, filesystem, process, lease, or job operations.
    let plan (policy: Policy) (pressureState: PressureState) (freeBytes: int64) (candidates: Candidate list) =
        match validatePolicy policy freeBytes with
        | Error error -> Error error
        | Ok () ->
            let duplicateIds =
                candidates
                |> List.choose (fun candidate ->
                    if String.IsNullOrWhiteSpace candidate.WorktreeId then None
                    else Some candidate.WorktreeId)
                |> List.groupBy id
                |> List.choose (fun (id, matches) ->
                    if List.length matches > 1 then Some id else None)

            let evaluated =
                candidates
                |> List.map (fun candidate ->
                    candidate, candidateSkipReasons policy duplicateIds candidate)

            let skipped =
                evaluated
                |> List.choose (fun (candidate, reasons) ->
                    if List.isEmpty reasons then None
                    else Some { WorktreeId = candidate.WorktreeId; Reasons = reasons })
                |> List.sortWith compareSkippedCandidates

            let skippedByReason =
                skipped
                |> List.collect (fun candidate -> candidate.Reasons)
                |> List.countBy id
                |> Map.ofList

            let eligible =
                evaluated
                |> List.choose (fun (candidate, reasons) ->
                    if not (List.isEmpty reasons) then
                        None
                    else
                        match candidate.AgeSinceLastActivity, candidate.EstimatedReclaimableBytes with
                        | Known age, Known bytes ->
                            Some
                                { WorktreeId = candidate.WorktreeId
                                  AgeSinceLastActivity = age
                                  ReclaimableBytes = bytes }
                        | _ -> None)
                |> List.sortWith comparePlannedCandidates

            let inactiveStopReason =
                match pressureState with
                | Normal when freeBytes <= policy.LowFreeBytes -> None
                | Normal -> Some PressureNotTriggered
                | Reclaiming when freeBytes >= policy.HighFreeBytes -> Some PressureCleared
                | Reclaiming -> None

            let selected, expectedReclaimedBytes, projectedFreeBytes, stopReason, stateAfter =
                match inactiveStopReason with
                | Some reason ->
                    [], 0L, freeBytes, reason, Normal
                | None when List.isEmpty eligible ->
                    [], 0L, freeBytes, NoEligibleCandidates, Reclaiming
                | None ->
                    let rec select remaining selectedSoFar expected projected =
                        match remaining with
                        | [] ->
                            List.rev selectedSoFar, expected, projected, EligibleCapacityExhausted
                        | _ when projected >= policy.HighFreeBytes ->
                            List.rev selectedSoFar, expected, projected, TargetReached
                        | candidate :: rest ->
                            let nextExpected = saturatingAdd expected candidate.ReclaimableBytes
                            let nextProjected = saturatingAdd projected candidate.ReclaimableBytes
                            let nextSelected = candidate :: selectedSoFar

                            if nextProjected >= policy.HighFreeBytes then
                                List.rev nextSelected, nextExpected, nextProjected, TargetReached
                            else
                                select rest nextSelected nextExpected nextProjected

                    let selected, expected, projected, reason =
                        select eligible [] 0L freeBytes

                    let nextState =
                        if reason = TargetReached then Normal
                        else Reclaiming

                    selected, expected, projected, reason, nextState

            Ok
                { SelectedCandidates = selected
                  ExpectedReclaimedBytes = expectedReclaimedBytes
                  ProjectedFreeBytes = projectedFreeBytes
                  StopReason = stopReason
                  PressureStateAfterPlan = stateAfter
                  SkippedCandidates = skipped
                  SkippedByReason = skippedByReason }
