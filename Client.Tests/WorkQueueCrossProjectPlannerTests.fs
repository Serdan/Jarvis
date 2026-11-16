module Client.Tests.WorkQueueCrossProjectPlannerTests

open System
open NUnit.Framework
open Client.AccountCapacityLedger
open Client.WorkQueueCrossProjectPlanner

[<TestFixture>]
type WorkQueueCrossProjectPlannerTests() =
    let projectA = "11111111-1111-4111-8111-111111111111"
    let projectB = "22222222-2222-4222-8222-222222222222"
    let launchA = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"
    let launchB = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"
    let browserA = "cccccccc-cccc-4ccc-8ccc-cccccccccccc"
    let browserB = "dddddddd-dddd-4ddd-8ddd-dddddddddddd"
    let admissionId = "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee"

    let project id =
        { ProjectId = id
          LedgerId = "ledger-" + id
          LedgerEpoch = 1
          AuthoritySha256 = "sha-" + id
          Head = "head-" + id
          Version = 8
          CatalogRevision = 4
          CatalogBlobOid = "catalog-" + id
          CoverageComplete = true }

    let role (p: PlannerProject) name version =
        { ProjectId = p.ProjectId
          ProjectHead = p.Head
          CatalogBlobOid = p.CatalogBlobOid
          RoleId = name
          Version = version
          Enabled = true
          Queues = set [ "implementation"; "review" ] }

    let candidate (p: PlannerProject) roleName workId launchId browserId at sequence blocking queue =
        { ProjectId = p.ProjectId
          ProjectHead = p.Head
          ProjectVersion = p.Version
          WorkId = workId
          WorkGeneration = 1
          QueueId = queue
          AvailableAt = at
          AvailableSequence = sequence
          BlocksOtherWork = blocking
          RoleId = roleName
          RoleVersion = 4
          CatalogBlobOid = p.CatalogBlobOid
          Source = { Head = "source-" + p.ProjectId
                     OperationId = "source-op-" + workId
                     RequestHash = "request-" + workId
                     SourceRef = Some("refs/heads/" + workId)
                     SourceCommit = Some "1234567890abcdef1234567890abcdef12345678" }
          LaunchId = launchId
          OriginalBrowserOperationId = browserId }

    let account tokens =
        { Head = "account-head-1"
          State =
            { SchemaVersion = 1
              AccountId = "44444444-4444-4444-8444-444444444444"
              LedgerId = "55555555-5555-4555-8555-555555555555"
              Epoch = 1
              MigrationPhase = "Activated"
              StateVersion = 17L
              ConfigVersion = 3L
              MaxConcurrentAgents = 1
              NextSequence = 3L
              Tokens = tokens } }

    let token (p: PlannerProject) launchId status =
        { LaunchId = launchId
          ProjectId = p.ProjectId
          ProjectLedgerId = p.LedgerId
          ProjectLedgerEpoch = p.LedgerEpoch
          ProjectAuthoritySha256 = p.AuthoritySha256
          RoleId = "Worker"
          RoleConfigVersion = 4L
          AdmissionOperationId = admissionId
          Status = status
          ReleaseProof = Unchecked.defaultof<_> }

    let existing (p: PlannerProject) launchId original phase outcome =
        { LaunchId = launchId
          ProjectId = p.ProjectId
          RoleId = "Worker"
          RoleVersion = 4
          AccountAdmissionOperationId = admissionId
          Phase = phase
          OriginalBrowserOperationId = original
          OriginalBrowser = outcome }

    let pA = project projectA
    let pB = project projectB
    let a = candidate pA "Worker" "work-a" launchA browserA 100L 2L false "implementation"
    let b = candidate pB "Worker" "work-b" launchB browserB 200L 1L false "implementation"
    let roles = [role pA "Worker" 4; role pB "Worker" 4]

    let ports acct projects rs candidates launches =
        { ReadAccount = fun () -> Ok acct
          ReadProjects = fun () -> Ok { CompleteProjectSet = true; Projects = projects }
          ReadRoles = fun () -> Ok rs
          ReadCandidates = fun () -> Ok candidates
          ReadExistingLaunches = fun () -> Ok launches }

    let report acct projects rs candidates launches =
        WorkQueueCrossProjectPlanner.planFakeOnly (ports acct projects rs candidates launches)

    let chosen = function
        | PlannerDecision.WouldLaunch candidate -> Some candidate
        | _ -> None

    [<Test>]
    member _.TwoProjectsOneAccountChooseGlobalOldestAndPreserveAllPinnedIdentity() =
        let result = report (account [||]) [pB; pA] (List.rev roles) [b; a] []
        Assert.That(result.CanDispatch, Is.False)
        Assert.That(result.Mode, Is.EqualTo("FakeOnlyReadOnly"))
        Assert.That(result.MaxConcurrentAgents, Is.EqualTo(Some 1))
        Assert.That(result.OccupiedAgents, Is.EqualTo(Some 0))
        Assert.That(result.Issues, Is.Empty)
        let work = result.Decision |> chosen |> Option.get
        Assert.That(work, Is.EqualTo(a))
        Assert.That(work.ProjectHead, Is.EqualTo(pA.Head))
        Assert.That(work.ProjectVersion, Is.EqualTo(pA.Version))
        Assert.That(work.RoleVersion, Is.EqualTo(4))
        Assert.That(work.CatalogBlobOid, Is.EqualTo(pA.CatalogBlobOid))
        Assert.That(work.Source.OperationId, Is.EqualTo("source-op-work-a"))
        Assert.That(work.LaunchId, Is.EqualTo(launchA))
        Assert.That(work.OriginalBrowserOperationId, Is.EqualTo(browserA))

    [<Test>]
    member _.BlockingReviewWinsOverOlderFifoImplementation() =
        let review = { b with QueueId = "review"; BlocksOtherWork = true }
        let result = report (account [||]) [pA; pB] roles [a; review] []
        Assert.That(result.Decision |> chosen, Is.EqualTo(Some review))

    [<Test>]
    member _.EqualTimestampUsesProjectThenWorkSequenceThenWorkIdTieBreak() =
        let sameTimeA = { a with AvailableAt = 100L; AvailableSequence = 8L }
        let sameTimeB = { b with AvailableAt = 100L; AvailableSequence = 1L }
        let result = report (account [||]) [pB; pA] roles [sameTimeB; sameTimeA] []
        Assert.That(result.Decision |> chosen, Is.EqualTo(Some sameTimeA))

    [<Test>]
    member _.GlobalHeldSlotAllowsOnlyOneWinnerEvenWithOtherProjectDemand() =
        let occupied = existing pA launchA (Some browserA)
                           PlannerLaunchPhase.Registered (PlannerBrowserEvidence.Accepted "receipt")
        let result = report (account [|token pA launchA "Held"|]) [pA; pB] roles [b] [occupied]
        Assert.That(result.Decision, Is.EqualTo(PlannerDecision.NoCapacity))
        Assert.That(result.VisibleLaunches.Length, Is.EqualTo(1))
        Assert.That(result.VisibleLaunches.Head, Is.EqualTo(occupied))
        Assert.That(result.OccupiedAgents, Is.EqualTo(Some 1))
        Assert.That(result.Issues |> List.exists (fun e ->
            e.Code = "CapacityFull" && e.Record = launchA
            && e.NextAction.Contains("verified terminal release")), Is.True)
        Assert.That(result.CanDispatch, Is.False)

    [<Test>]
    member _.UnknownOriginalBrowserResultCannotBecomeNoCapacityOrNewDispatch() =
        let unregistered = existing pA launchA (Some browserA)
                               PlannerLaunchPhase.SubmittedUnregistered
                               (PlannerBrowserEvidence.Unknown "transport closed after sending")
        let result = report (account [|token pA launchA "Held"|]) [pA; pB] roles [b] [unregistered]
        Assert.That(result.Decision, Is.EqualTo(PlannerDecision.Unknown))
        Assert.That(result.VisibleLaunches.Head.OriginalBrowserOperationId, Is.EqualTo(Some browserA))
        Assert.That(result.Issues |> List.exists (fun e ->
            e.Code = "OriginalBrowserOutcomeUnknown"
            && e.Record = browserA
            && e.NextAction.Contains("never resend")), Is.True)

    [<Test>]
    member _.ReservedUnregisteredWithAbsentJournalRemainsUnknownAndVisible() =
        let reserved = existing pA launchA None
                           PlannerLaunchPhase.ReservedUnregistered PlannerBrowserEvidence.NotRecorded
        let result = report (account [|token pA launchA "Held"|]) [pA; pB] roles [b] [reserved]
        Assert.That(result.Decision, Is.EqualTo(PlannerDecision.Unknown))
        Assert.That(result.VisibleLaunches.Length, Is.EqualTo(1))
        Assert.That(result.VisibleLaunches.Head, Is.EqualTo(reserved))
        Assert.That(result.Issues |> List.exists (fun e -> e.Code = "OriginalBrowserJournalMissing"), Is.True)

    [<Test>]
    member _.RoleVersionMismatchNeedsRepairInsteadOfSilentRebinding() =
        let stale = { b with RoleVersion = 3 }
        let result = report (account [||]) [pB] [role pB "Worker" 4] [stale] []
        Assert.That(result.Decision, Is.EqualTo(PlannerDecision.NeedsAttention))
        Assert.That(result.Issues |> List.exists (fun e -> e.Code = "RoleVersionMismatch"), Is.True)

    [<Test>]
    member _.ManagerCannotBeChosenForModelDrivenLaunch() =
        let manager = { a with RoleId = "Manager" }
        let result = report (account [||]) [pA] [role pA "Manager" 4] [manager] []
        Assert.That(result.Decision, Is.EqualTo(PlannerDecision.NeedsAttention))
        Assert.That(result.CanDispatch, Is.False)
        Assert.That(result.Issues |> List.exists (fun e -> e.Code = "RoleNotEligible"), Is.True)

    [<Test>]
    member _.IncompleteAccountProjectSetIsUnknownNotAFreeSlot() =
        let original = ports (account [||]) [pA] [role pA "Worker" 4] [a] []
        let incomplete =
            { original with
                ReadProjects = fun () -> Ok { CompleteProjectSet = false; Projects = [pA] } }
        let result = WorkQueueCrossProjectPlanner.planFakeOnly incomplete
        Assert.That(result.Decision, Is.EqualTo(PlannerDecision.Unknown))

    [<Test>]
    member _.SourceReceiptMissingIsUnknownAndNeverSynthesized() =
        let incompleteSource = { a with Source = { a.Source with RequestHash = "" } }
        let result = report (account [||]) [pA] [role pA "Worker" 4] [incompleteSource] []
        Assert.That(result.Decision, Is.EqualTo(PlannerDecision.Unknown))
        Assert.That(result.Issues |> List.exists (fun e -> e.Code = "SourceReceiptUnknown"), Is.True)

    [<Test>]
    member _.TwoClientsProduceIdenticalAnswerWithReversedEnumeration() =
        let first = report (account [||]) [pA; pB] roles [a; b] []
        let second = report (account [||]) [pB; pA] (List.rev roles) [b; a] []
        Assert.That(second, Is.EqualTo(first))

    [<Test>]
    member _.MissingSourcePortFailsClosedAndReadsEachSourceOnlyOnce() =
        let mutable reads = 0
        let original = ports (account [||]) [pA; pB] roles [a; b] []
        let failed =
            { original with
                ReadCandidates = fun () -> reads <- reads + 1; Error "Git source unavailable" }
        let result = WorkQueueCrossProjectPlanner.planFakeOnly failed
        Assert.That(reads, Is.EqualTo(1))
        Assert.That(result.Decision, Is.EqualTo(PlannerDecision.Unknown))
        Assert.That(result.Issues |> List.exists (fun e -> e.Code = "CandidateReadUnknown"), Is.True)

    [<Test>]
    member _.PreexistingLaunchIdCannotBeReusedEvenAfterRelease() =
        let terminal = existing pA launchA (Some browserA)
                           PlannerLaunchPhase.TerminalPendingRelease
                           (PlannerBrowserEvidence.Accepted "historical")
        let result = report (account [|token pA launchA "Released"|]) [pA] [role pA "Worker" 4] [a] [terminal]
        Assert.That(result.Decision, Is.EqualTo(PlannerDecision.NeedsAttention))
        Assert.That(result.Issues |> List.exists (fun e -> e.Code = "LaunchIdAlreadyUsed"), Is.True)

    [<Test>]
    member _.DistinctProjectTokenMismatchNeedsAttentionBeforeGlobalSelection() =
        let admitted = existing pA launchA (Some browserA)
                           PlannerLaunchPhase.Registered
                           (PlannerBrowserEvidence.Accepted "receipt")
        let conflict = { token pA launchA "Held" with RoleConfigVersion = 3L }
        let result = report (account [|conflict|]) [pA; pB] roles [b] [admitted]
        Assert.That(result.Decision, Is.EqualTo(PlannerDecision.NeedsAttention))
        Assert.That(result.Issues |> List.exists (fun e -> e.Code = "AccountProjectIdentityMismatch"), Is.True)


    [<Test>]
    member _.OriginalBrowserOperationCannotBeAssignedToAnotherLaunch() =
        let registered = existing pA launchA (Some browserA)
                             PlannerLaunchPhase.Registered
                             (PlannerBrowserEvidence.Accepted "receipt")
        let reused = { b with OriginalBrowserOperationId = browserA }
        let result = report (account [|token pA launchA "Held"|]) [pA; pB] roles [reused] [registered]
        Assert.That(result.Decision, Is.EqualTo(PlannerDecision.NeedsAttention))
        Assert.That(result.Issues |> List.exists (fun e ->
            e.Code = "BrowserOperationIdAlreadyUsed" && e.Record = browserA), Is.True)

    [<Test>]
    member _.MalformedFakeEvidenceFailsClosedWithoutEscapingException() =
        let original = ports (account [||]) [pA; pB] roles [a; b] []
        let invalid = { original with ReadProjects = fun () -> Ok Unchecked.defaultof<PlannerProjectSet> }
        let result = WorkQueueCrossProjectPlanner.planFakeOnly invalid
        Assert.That(result.Decision, Is.EqualTo(PlannerDecision.Unknown))
        Assert.That(result.CanDispatch, Is.False)
        Assert.That(result.Issues |> List.exists (fun e -> e.Code = "MalformedPlannerEvidence"), Is.True)

    [<Test>]
    member _.TwoClientsReportIdenticalConflictOrderWithDifferentInputOrder() =
        let staleA = { a with RoleVersion = 3 }
        let staleB = { b with RoleVersion = 2 }
        let first = report (account [||]) [pA; pB] roles [staleA; staleB] []
        let second = report (account [||]) [pB; pA] (List.rev roles) [staleB; staleA] []
        Assert.That(first.Decision, Is.EqualTo(PlannerDecision.NeedsAttention))
        Assert.That(second, Is.EqualTo(first))

    [<Test>]
    member _.ReleasedTerminalLaunchCannotBeReusedWithUppercaseGuidAlias() =
        let terminal = existing pA launchA (Some browserA)
                           PlannerLaunchPhase.TerminalPendingRelease
                           (PlannerBrowserEvidence.Accepted "historical")
        let alias = { a with LaunchId = launchA.ToUpperInvariant(); OriginalBrowserOperationId = browserB }
        let result = report (account [|token pA launchA "Released"|])
                            [pA] [role pA "Worker" 4] [alias] [terminal]
        Assert.That(result.Decision, Is.EqualTo(PlannerDecision.NeedsAttention))
        Assert.That(result.Issues |> List.exists (fun e ->
            e.Code = "LaunchIdAlreadyUsed" && e.Record = alias.LaunchId), Is.True)
        Assert.That(result.VisibleLaunches.Head.LaunchId, Is.EqualTo(launchA))
        Assert.That(result.CanDispatch, Is.False)

    [<Test>]
    member _.ReleasedBrowserOperationCannotBeReusedWithBracedGuidAlias() =
        let terminal = existing pA launchA (Some browserA)
                           PlannerLaunchPhase.TerminalPendingRelease
                           (PlannerBrowserEvidence.Accepted "historical")
        let alias = { b with OriginalBrowserOperationId = "{" + browserA.ToUpperInvariant() + "}" }
        let result = report (account [|token pA launchA "Released"|])
                            [pA; pB] roles [alias] [terminal]
        Assert.That(result.Decision, Is.EqualTo(PlannerDecision.NeedsAttention))
        Assert.That(result.Issues |> List.exists (fun e ->
            e.Code = "BrowserOperationIdAlreadyUsed"
            && e.Record = alias.OriginalBrowserOperationId), Is.True)
        Assert.That(result.VisibleLaunches.Head.OriginalBrowserOperationId, Is.EqualTo(Some browserA))

    [<Test>]
    member _.TwoCandidateLaunchAliasesConflictAndKeepOriginalSpellings() =
        let alias = { b with LaunchId = Guid.Parse(launchA).ToString("N").ToUpperInvariant() }
        let result = report (account [||]) [pA; pB] roles [a; alias] []
        Assert.That(result.Decision, Is.EqualTo(PlannerDecision.NeedsAttention))
        Assert.That(result.Issues |> List.exists (fun e ->
            e.Code = "DuplicateLaunchIntent"
            && e.Record.Contains(launchA) && e.Record.Contains(alias.LaunchId)), Is.True)

    [<Test>]
    member _.TwoCandidateBrowserAliasesConflictAndKeepOriginalSpellings() =
        let alias = { b with OriginalBrowserOperationId = Guid.Parse(browserA).ToString("N").ToUpperInvariant() }
        let result = report (account [||]) [pA; pB] roles [a; alias] []
        Assert.That(result.Decision, Is.EqualTo(PlannerDecision.NeedsAttention))
        Assert.That(result.Issues |> List.exists (fun e ->
            e.Code = "DuplicateBrowserIntent"
            && e.Record.Contains(browserA) && e.Record.Contains(alias.OriginalBrowserOperationId)), Is.True)

    [<Test>]
    member _.ReleasedAccountTokenAliasStillMatchesHistoricalLaunch() =
        let terminal = existing pA launchA (Some browserA)
                           PlannerLaunchPhase.TerminalPendingRelease
                           (PlannerBrowserEvidence.Accepted "historical")
        let upperToken = token pA (launchA.ToUpperInvariant()) "Released"
        let result = report (account [|upperToken|]) [pA; pB] roles [b] [terminal]
        Assert.That(result.Decision |> chosen, Is.EqualTo(Some b))
        Assert.That(result.Issues, Is.Empty)
        Assert.That(result.CanDispatch, Is.False)

    [<Test>]
    member _.InvalidReleasedAccountTokenUuidCannotPassAsCleanEvidence() =
        let malformed = token pA "not-a-uuid" "Released"
        let result = report (account [|malformed|]) [pA] [role pA "Worker" 4] [a] []
        Assert.That(result.Decision, Is.EqualTo(PlannerDecision.NeedsAttention))
        Assert.That(result.Issues |> List.exists (fun e ->
            e.Code = "AccountTokenIdentityInvalid"), Is.True)

    [<Test>]
    member _.SettledHistoryFromRetiredProjectDoesNotOccupyOrRequireCurrentPin() =
        let retired = project "99999999-9999-4999-8999-999999999999"
        let oldToken = token retired "99999999-aaaa-4aaa-8aaa-999999999999" "Released"
        let active = existing pA launchA (Some browserA)
                         PlannerLaunchPhase.Registered
                         (PlannerBrowserEvidence.Accepted "accepted")
        let result = report (account [|oldToken; token pA launchA "Held"|])
                            [pA; pB] roles [b] [active]
        Assert.That(result.Decision, Is.EqualTo(PlannerDecision.NoCapacity))
        Assert.That(result.OccupiedAgents, Is.EqualTo(Some 1))
        Assert.That(result.Issues |> List.exists (fun entry ->
            entry.Code = "AccountTokenProjectUnknown"), Is.False)

    [<Test>]
    member _.UnsettledTokenForUnapprovedProjectRemainsUnknown() =
        let missing = project "99999999-9999-4999-8999-999999999999"
        let unsettled = token missing "99999999-aaaa-4aaa-8aaa-999999999999" "Held"
        let result = report (account [|unsettled|]) [pA; pB] roles [b] []
        Assert.That(result.Decision, Is.EqualTo(PlannerDecision.Unknown))
        Assert.That(result.Issues |> List.exists (fun entry ->
            entry.Code = "AccountTokenProjectUnknown"), Is.True)
