module Client.Tests.WorktreeRetentionPolicyTests

open System
open Client.WorktreeRetentionPolicy
open NUnit.Framework

let private gib = 1024L * 1024L * 1024L

let private policy = Client.WorktreeRetentionPolicy.suggestedDefaults

let private eligible (id: string) (ageDays: float) (reclaimableBytes: int64) : Candidate =
    { WorktreeId = id
      AttemptTerminal = Known true
      EngineeringJobsTerminal = Known true
      NoActiveLease = Known true
      NoActiveProcess = Known true
      IsClean = Known true
      IsUnlocked = Known true
      IsDetached = Known false
      IsIntegrated = Known true
      TargetIsFresh = Known true
      NoUniqueEvidence = Known true
      AgeSinceLastActivity = Known(TimeSpan.FromDays ageDays)
      EstimatedReclaimableBytes = Known reclaimableBytes }

let private run (state: PressureState) (freeBytes: int64) (candidates: Candidate list) : Plan =
    match Client.WorktreeRetentionPolicy.plan policy state freeBytes candidates with
    | Ok result -> result
    | Error error -> failwithf "Expected valid retention policy, got %A" error

[<Test>]
let normalStateStartsOnlyAtOrBelowTheLowWatermark () =
    let candidate = eligible "old" 30.0 (40L * gib)

    let aboveLow = run Normal (policy.LowFreeBytes + 1L) [ candidate ]
    Assert.That(aboveLow.StopReason, Is.EqualTo PressureNotTriggered)
    Assert.That(aboveLow.SelectedCandidates, Is.Empty)

    let atLow = run Normal policy.LowFreeBytes [ candidate ]
    Assert.That(atLow.StopReason, Is.EqualTo TargetReached)
    Assert.That(atLow.ProjectedFreeBytes, Is.EqualTo policy.HighFreeBytes)
    Assert.That(atLow.PressureStateAfterPlan, Is.EqualTo Normal)

[<Test>]
let reclaimingStateContinuesInsideTheHysteresisBandAndClearsAtHigh () =
    let candidate = eligible "old" 30.0 (policy.HighFreeBytes - policy.LowFreeBytes - 1L)

    let insideBand = run Reclaiming (policy.LowFreeBytes + 1L) [ candidate ]
    Assert.That(insideBand.StopReason, Is.EqualTo TargetReached)
    Assert.That(insideBand.PressureStateAfterPlan, Is.EqualTo Normal)

    let justBelowHigh = run Reclaiming (policy.HighFreeBytes - 1L) []
    Assert.That(justBelowHigh.StopReason, Is.EqualTo NoEligibleCandidates)
    Assert.That(justBelowHigh.PressureStateAfterPlan, Is.EqualTo Reclaiming)

    let atHigh = run Reclaiming policy.HighFreeBytes [ candidate ]
    Assert.That(atHigh.StopReason, Is.EqualTo PressureCleared)
    Assert.That(atHigh.SelectedCandidates, Is.Empty)
    Assert.That(atHigh.PressureStateAfterPlan, Is.EqualTo Normal)

[<Test>]
let selectionUsesOldestFirstAndStopsAtTheHealthyTarget () =
    let candidates =
        [ eligible "newest" 10.0 (40L * gib)
          eligible "oldest" 30.0 (40L * gib)
          eligible "middle" 20.0 (40L * gib) ]

    let result = run Normal 0L candidates

    Assert.That(result.SelectedCandidates |> List.map (fun candidate -> candidate.WorktreeId) = [ "oldest"; "middle" ])
    Assert.That(result.ExpectedReclaimedBytes, Is.EqualTo(80L * gib))
    Assert.That(result.ProjectedFreeBytes, Is.EqualTo policy.HighFreeBytes)
    Assert.That(result.StopReason, Is.EqualTo TargetReached)

[<Test>]
let equalAgeCandidatesUseOrdinalWorktreeIdTieBreaking () =
    let candidates =
        [ eligible "zeta" 20.0 (80L * gib)
          eligible "alpha" 20.0 (80L * gib)
          eligible "beta" 20.0 (80L * gib) ]

    let first = run Normal (40L * gib) candidates
    let second = run Normal (40L * gib) (List.rev candidates)

    Assert.That(first.SelectedCandidates |> List.map (fun candidate -> candidate.WorktreeId) = [ "alpha" ])
    Assert.That(second, Is.EqualTo(first))

[<Test>]
let unknownAmbiguousAndUnsafeFactsRejectCandidateWithSpecificReasons () =
    let unsafe =
        { eligible "unsafe" 30.0 (80L * gib) with
            AttemptTerminal = Unknown
            EngineeringJobsTerminal = Ambiguous
            NoActiveLease = Known false
            NoActiveProcess = Unknown
            IsClean = Known false
            IsUnlocked = Ambiguous
            IsDetached = Known true
            IsIntegrated = Known false
            TargetIsFresh = Unknown
            NoUniqueEvidence = Known false
            AgeSinceLastActivity = Unknown
            EstimatedReclaimableBytes = Ambiguous }

    let result = run Normal 0L [ unsafe ]
    let reasons = result.SkippedCandidates |> List.exactlyOne |> fun candidate -> candidate.Reasons

    Assert.That(result.SelectedCandidates, Is.Empty)
    Assert.That(result.StopReason, Is.EqualTo NoEligibleCandidates)
    Assert.That(
        reasons,
        Is.EquivalentTo
            [ AttemptStatusUnknown
              EngineeringJobsStatusAmbiguous
              ActiveLease
              ProcessStatusUnknown
              WorktreeDirty
              LockStatusAmbiguous
              DetachedWorktree
              NotIntegrated
              TargetFreshnessUnknown
              UniqueEvidencePresent
              AgeUnknown
              ReclaimableBytesAmbiguous ])

    Assert.That(result.SkippedByReason[AttemptStatusUnknown], Is.EqualTo 1)
    Assert.That(result.SkippedByReason[UniqueEvidencePresent], Is.EqualTo 1)

[<Test>]
let missingReclaimableCapacityIsNotSelected () =
    let noCapacity = eligible "empty" 30.0 0L

    let result = run Normal 0L [ noCapacity ]

    Assert.That(result.StopReason, Is.EqualTo NoEligibleCandidates)
    Assert.That(result.SkippedCandidates |> List.exactlyOne |> fun candidate -> List.contains NoReclaimableBytes candidate.Reasons)
    Assert.That(result.PressureStateAfterPlan, Is.EqualTo Reclaiming)

[<Test>]
let duplicateWorktreeIdsAreAllRejected () =
    let duplicates =
        [ eligible "same" 30.0 (80L * gib)
          eligible "same" 20.0 (80L * gib) ]

    let result = run Normal 0L duplicates

    Assert.That(result.SelectedCandidates, Is.Empty)
    Assert.That(result.SkippedCandidates.Length, Is.EqualTo 2)
    Assert.That(result.SkippedByReason[DuplicateWorktreeId], Is.EqualTo 2)

[<Test>]
let planningIsIdempotentForIdenticalObservedFacts () =
    let candidates =
        [ eligible "older" 30.0 (20L * gib)
          eligible "younger" 15.0 (30L * gib) ]

    let first = run Normal (40L * gib) candidates
    let second = run Normal (40L * gib) candidates

    Assert.That(second, Is.EqualTo(first))

[<Test>]
let invalidThresholdsAndNegativeFreeSpaceAreRejected () =
    let invalidOrder = { policy with HighFreeBytes = policy.LowFreeBytes }
    let negativeLow = { policy with LowFreeBytes = -1L }
    let negativeAge = { policy with MinimumAge = TimeSpan.FromDays -1.0 }

    Assert.That(Client.WorktreeRetentionPolicy.plan invalidOrder Normal 0L [] = Error HighWatermarkMustExceedLow)
    Assert.That(Client.WorktreeRetentionPolicy.plan negativeLow Normal 0L [] = Error NegativeLowWatermark)
    Assert.That(Client.WorktreeRetentionPolicy.plan negativeAge Normal 0L [] = Error NegativeMinimumAge)
    Assert.That(Client.WorktreeRetentionPolicy.plan policy Normal -1L [] = Error NegativeFreeSpace)


[<Test>]
let unknownAndAmbiguousEvidenceAlwaysHasSpecificSkipReasons () =
    let candidate = eligible "candidate" 30.0 (10L * gib)

    let cases =
        [ { candidate with AttemptTerminal = Unknown }, AttemptStatusUnknown
          { candidate with AttemptTerminal = Ambiguous }, AttemptStatusAmbiguous
          { candidate with EngineeringJobsTerminal = Unknown }, EngineeringJobsStatusUnknown
          { candidate with EngineeringJobsTerminal = Ambiguous }, EngineeringJobsStatusAmbiguous
          { candidate with NoActiveLease = Unknown }, LeaseStatusUnknown
          { candidate with NoActiveLease = Ambiguous }, LeaseStatusAmbiguous
          { candidate with NoActiveProcess = Unknown }, ProcessStatusUnknown
          { candidate with NoActiveProcess = Ambiguous }, ProcessStatusAmbiguous
          { candidate with IsClean = Unknown }, CleanlinessUnknown
          { candidate with IsClean = Ambiguous }, CleanlinessAmbiguous
          { candidate with IsUnlocked = Unknown }, LockStatusUnknown
          { candidate with IsUnlocked = Ambiguous }, LockStatusAmbiguous
          { candidate with IsDetached = Unknown }, DetachedStatusUnknown
          { candidate with IsDetached = Ambiguous }, DetachedStatusAmbiguous
          { candidate with IsIntegrated = Unknown }, IntegrationStatusUnknown
          { candidate with IsIntegrated = Ambiguous }, IntegrationStatusAmbiguous
          { candidate with TargetIsFresh = Unknown }, TargetFreshnessUnknown
          { candidate with TargetIsFresh = Ambiguous }, TargetFreshnessAmbiguous
          { candidate with NoUniqueEvidence = Unknown }, EvidenceScanUnknown
          { candidate with NoUniqueEvidence = Ambiguous }, EvidenceScanAmbiguous
          { candidate with AgeSinceLastActivity = Unknown }, AgeUnknown
          { candidate with AgeSinceLastActivity = Ambiguous }, AgeAmbiguous
          { candidate with EstimatedReclaimableBytes = Unknown }, ReclaimableBytesUnknown
          { candidate with EstimatedReclaimableBytes = Ambiguous }, ReclaimableBytesAmbiguous ]

    for candidate, expectedReason in cases do
        let result = run Normal 0L [ candidate ]
        let skipped = result.SkippedCandidates |> List.exactlyOne

        Assert.That(result.SelectedCandidates, Is.Empty)
        Assert.That(skipped.Reasons, Does.Contain expectedReason)

[<Test>]
let knownUnsafeFactsHaveSpecificSkipReasons () =
    let candidate = eligible "candidate" 30.0 (10L * gib)

    let cases =
        [ { candidate with AttemptTerminal = Known false }, AttemptNotTerminal
          { candidate with EngineeringJobsTerminal = Known false }, EngineeringJobsNotTerminal
          { candidate with NoActiveLease = Known false }, ActiveLease
          { candidate with NoActiveProcess = Known false }, ActiveProcess
          { candidate with IsClean = Known false }, WorktreeDirty
          { candidate with IsUnlocked = Known false }, WorktreeLocked
          { candidate with IsDetached = Known true }, DetachedWorktree
          { candidate with IsIntegrated = Known false }, NotIntegrated
          { candidate with TargetIsFresh = Known false }, TargetNotFresh
          { candidate with NoUniqueEvidence = Known false }, UniqueEvidencePresent ]

    for candidate, expectedReason in cases do
        let result = run Normal 0L [ candidate ]
        let skipped = result.SkippedCandidates |> List.exactlyOne

        Assert.That(result.SelectedCandidates, Is.Empty)
        Assert.That(skipped.Reasons, Does.Contain expectedReason)

[<Test>]
let invalidIdentifiersAgeAndReclaimableBytesAreRejected () =
    let invalidIdentifier = eligible "" 30.0 (10L * gib)
    let invalidAge = { eligible "negative-age" 30.0 (10L * gib) with AgeSinceLastActivity = Known(TimeSpan.FromDays -1.0) }
    let recent = eligible "recent" 6.0 (10L * gib)
    let invalidBytes = { eligible "negative-bytes" 30.0 (10L * gib) with EstimatedReclaimableBytes = Known -1L }
    let exactMinimumAge = eligible "minimum-age" 7.0 (10L * gib)

    let result = run Normal 0L [ invalidIdentifier; invalidAge; recent; invalidBytes; exactMinimumAge ]

    Assert.That(result.SelectedCandidates |> List.map (fun candidate -> candidate.WorktreeId) = [ "minimum-age" ])
    Assert.That(result.SkippedByReason[InvalidWorktreeId], Is.EqualTo 1)
    Assert.That(result.SkippedByReason[InvalidAge], Is.EqualTo 1)
    Assert.That(result.SkippedByReason[TooRecent], Is.EqualTo 1)
    Assert.That(result.SkippedByReason[InvalidReclaimableBytes], Is.EqualTo 1)

[<Test>]
let exhaustedEligibleCapacityKeepsPressureActive () =
    let candidate = eligible "small" 30.0 (10L * gib)

    let result = run Normal policy.LowFreeBytes [ candidate ]

    Assert.That(result.SelectedCandidates |> List.map (fun item -> item.WorktreeId) = [ "small" ])
    Assert.That(result.ExpectedReclaimedBytes, Is.EqualTo(10L * gib))
    Assert.That(result.ProjectedFreeBytes, Is.EqualTo(policy.LowFreeBytes + 10L * gib))
    Assert.That(result.StopReason, Is.EqualTo EligibleCapacityExhausted)
    Assert.That(result.PressureStateAfterPlan, Is.EqualTo Reclaiming)

[<Test>]
let suggestedDefaultsReflectTheIncidentAndKeepASevenDayAgeFloor () =
    Assert.That(policy.LowFreeBytes, Is.EqualTo(40L * gib))
    Assert.That(policy.HighFreeBytes, Is.EqualTo(80L * gib))
    Assert.That(policy.MinimumAge, Is.EqualTo(TimeSpan.FromDays 7.0))
