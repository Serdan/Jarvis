module Client.Tests.WorkQueueReactiveWakeTests

open System
open System.Collections.Generic
open System.Threading.Tasks
open NUnit.Framework
open Client.WorkQueueSchedulerHostPreview
open Client.WorkQueueReactiveWake

[<TestFixture>]
type WorkQueueReactiveWakeTests() =
    let accountHead = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
    let projectHead = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
    let launchId = "11111111-1111-4111-8111-111111111111"
    let browserOperationId = "22222222-2222-4222-8222-222222222222"
    let selected: PreviewCandidate =
        { WorkId = "child-work"; WorkGeneration = 1; QueueId = "implementation"
          RoleId = "Worker"; RoleVersion = 4
          SourceHead = Some projectHead; SourceRef = Some "refs/heads/source"
          SourceCommit = Some "cccccccccccccccccccccccccccccccccccccccc"
          SourceOperationId = Some "source-op"; SourceRequestHash = Some "source-request-hash"
          OriginBlobOid = Some "dddddddddddddddddddddddddddddddddddddddd" }

    let report (): SchedulerHostPreviewReport =
        { Mode = "ReadOnlyDryRun"; CanDispatch = false
          AccountHead = Some accountHead; AccountMigration = Some "Activated"
          MaxConcurrentAgents = Some 1; OccupiedAgents = Some 0; AvailableSlots = Some 1
          ProjectHead = Some projectHead; ProjectVersion = Some 1
          RoleCatalogRevision = Some 1; RoleId = "Worker"; SelectedRoleVersion = Some 4
          EligibleWork = [ selected.WorkId ]; SkippedWork = []
          WouldLaunch = Some selected; ExistingLaunches = []
          Issues = [ { Code = "DispatchDisabled"; Record = projectHead
                       NextAction = "No production scheduler or browser dispatch enabled." } ] }

    let intent (preview: SchedulerHostPreviewReport) : WorkExecutorIntent =
        { WorkId = selected.WorkId; WorkGeneration = selected.WorkGeneration
          RoleId = selected.RoleId; RoleVersion = selected.RoleVersion
          ProjectHead = preview.ProjectHead.Value
          AccountHead = preview.AccountHead.Value
          SourceOperationId = selected.SourceOperationId.Value
          SourceRequestHash = selected.SourceRequestHash.Value
          LaunchId = launchId; OriginalBrowserOperationId = browserOperationId }

    let ports read execute : WorkWakePorts =
        { ReadVerified = read
          Plan = fun preview -> Ok(Some(intent preview))
          Execute = execute }

    [<Test>]
    member _.DefaultIsOffAndDoesNotEvenReadGit() =
        let mutable reads, effects = 0, 0
        let worker =
            WorkQueueReactiveWake.Coordinator(
                ports (fun () -> reads <- reads + 1; report ())
                    (fun _ -> effects <- effects + 1; WorkExecutorResult.Applied))
        Assert.That(worker.Wake [ WorkWakeTrigger.WorkCreated ], Is.EqualTo(WorkWakeResult.Disabled))
        Assert.That(reads, Is.Zero)
        Assert.That(effects, Is.Zero)
        let active =
            WorkQueueReactiveWake.Coordinator(
                ports (fun () -> reads <- reads + 1; report ())
                    (fun _ -> effects <- effects + 1; WorkExecutorResult.Applied), enabled = true)
        Assert.That(active.Wake [], Is.EqualTo(WorkWakeResult.Dormant))
        Assert.That(reads, Is.Zero)
        Assert.That(effects, Is.Zero)

    [<Test>]
    member _.TwoHardDependenciesAndNewChildNeverConsumeCapacityEarly() =
        let accepted = HashSet<string>()
        let mutable exists = false
        let mutable callbacks = 0
        let read () =
            let ready = exists && accepted.Contains "review" && accepted.Contains "integration"
            let current = report ()
            { current with
                ProjectHead = Some("project-head-" + string accepted.Count + "-" + string exists)
                WouldLaunch = if ready then Some selected else None
                EligibleWork = if ready then [selected.WorkId] else [] }
        let worker =
            WorkQueueReactiveWake.Coordinator(
                ports read (fun _ -> callbacks <- callbacks + 1; WorkExecutorResult.Applied),
                enabled = true)
        Assert.That(worker.Wake [ WorkWakeTrigger.WorkCreated ], Is.EqualTo(WorkWakeResult.Dormant))
        exists <- true
        Assert.That(worker.Wake [ WorkWakeTrigger.WorkCreated ], Is.EqualTo(WorkWakeResult.Dormant))
        accepted.Add "review" |> ignore
        Assert.That(worker.Wake [ WorkWakeTrigger.ReviewAccepted ], Is.EqualTo(WorkWakeResult.Dormant))
        Assert.That(callbacks, Is.Zero, "A single satisfied prerequisite must not start a model.")
        accepted.Add "integration" |> ignore
        let wake () = worker.Wake [WorkWakeTrigger.IntegrationCommitted; WorkWakeTrigger.WorkTransitionCommitted]
        let first = Task.Run(Func<WorkWakeResult>(wake))
        let second = Task.Run(Func<WorkWakeResult>(wake))
        Task.WaitAll([| first :> Task; second :> Task |])
        Assert.That(callbacks, Is.EqualTo(1), "Coalesced callbacks must be one-shot.")
        let outcomes = [ first.Result; second.Result ]
        Assert.That(outcomes |> List.exists (function WorkWakeResult.Reconciled _ -> true | _ -> false), Is.True)
        Assert.That(outcomes |> List.exists (function WorkWakeResult.Coalesced _ -> true | _ -> false), Is.True)

    [<Test>]
    member _.UnknownOrZeroCapacityCannotAllocateUntilVerifiedRelease() =
        let mutable capacity = Some 0
        let mutable callbacks = 0
        let read () = { report () with AvailableSlots = capacity }
        let worker =
            WorkQueueReactiveWake.Coordinator(
                ports read (fun _ -> callbacks <- callbacks + 1; WorkExecutorResult.Applied),
                enabled = true)
        Assert.That(worker.Wake [WorkWakeTrigger.WorkCreated], Is.EqualTo(WorkWakeResult.NoCapacity))
        capacity <- None
        match worker.Wake [WorkWakeTrigger.CapacityReleased] with
        | WorkWakeResult.Unknown("CapacityProofUnavailable", _) -> ()
        | other -> Assert.Fail(sprintf "Missing capacity proof must fail closed: %A" other)
        capacity <- Some 1
        match worker.Wake [WorkWakeTrigger.CapacityReleased] with
        | WorkWakeResult.Reconciled _ -> ()
        | other -> Assert.Fail(sprintf "Expected a verified capacity wake: %A" other)
        Assert.That(callbacks, Is.EqualTo(1))

    [<Test>]
    member _.UnavailableGitAndIncompleteSourceAreActionableUnknowns() =
        let mutable current = { report () with AccountHead = None }
        let mutable effects = 0
        let worker =
            WorkQueueReactiveWake.Coordinator(
                ports (fun () -> current) (fun _ -> effects <- effects + 1; WorkExecutorResult.Applied),
                enabled = true)
        match worker.Wake [WorkWakeTrigger.ManualReconcile] with
        | WorkWakeResult.Unknown("CanonicalHeadsUnavailable", _) -> ()
        | other -> Assert.Fail(sprintf "Expected missing-head failure: %A" other)
        current <- { report () with
                        Issues = [ { Code = "SourceReceiptMissing"; Record = selected.WorkId
                                     NextAction = "Recover immutable source receipt." } ] }
        match worker.Wake [WorkWakeTrigger.ReviewAccepted] with
        | WorkWakeResult.NeedsAttention("SourceReceiptMissing", _) -> ()
        | other -> Assert.Fail(sprintf "Expected source-provenance stop: %A" other)
        Assert.That(effects, Is.Zero)

    [<Test>]
    member _.ChangingGitHeadsRetryWithBoundedAttemptsAndNoStaleEffects() =
        let mutable reads, calls = 0, 0
        let read () =
            reads <- reads + 1
            let head = if reads = 1 then "stale-head" else "current-head"
            { report () with ProjectHead = Some head }
        let worker =
            WorkQueueReactiveWake.Coordinator(
                ports read (fun _ -> calls <- calls + 1; WorkExecutorResult.Applied),
                enabled = true, maxReadRetries = 3)
        match worker.Wake [WorkWakeTrigger.WorkTransitionCommitted] with
        | WorkWakeResult.Reconciled executed ->
            Assert.That(executed.ProjectHead, Is.EqualTo("current-head"))
        | other -> Assert.Fail(sprintf "Expected retried canonical head: %A" other)
        Assert.That(reads, Is.EqualTo(4))
        Assert.That(calls, Is.EqualTo(1))

    [<Test>]
    member _.UnboundedConcurrentGitMovementStopsAndDoesNotExecute() =
        let mutable reads, calls = 0, 0
        let read () =
            reads <- reads + 1
            { report () with ProjectHead = Some("head-" + string reads) }
        let worker =
            WorkQueueReactiveWake.Coordinator(
                ports read (fun _ -> calls <- calls + 1; WorkExecutorResult.Applied),
                enabled = true, maxReadRetries = 2)
        match worker.Wake [WorkWakeTrigger.CapacityReleased] with
        | WorkWakeResult.NeedsAttention("ConcurrentGitChanges", _) -> ()
        | other -> Assert.Fail(sprintf "Expected bounded retry: %A" other)
        Assert.That(reads, Is.EqualTo(4))
        Assert.That(calls, Is.Zero)

    [<Test>]
    member _.UncertainOriginalOperationIsNotBlindlyResubmitted() =
        let mutable calls = 0
        let worker =
            WorkQueueReactiveWake.Coordinator(
                ports report (fun _ ->
                    calls <- calls + 1
                    WorkExecutorResult.OutcomeUnknown "Provider response lost."),
                enabled = true)
        for _ in 1 .. 2 do
            match worker.Wake [WorkWakeTrigger.WorkTransitionCommitted] with
            | WorkWakeResult.NeedsAttention("OriginalOperationUnknown", action) ->
                Assert.That(action, Does.Contain(browserOperationId))
            | other -> Assert.Fail(sprintf "Uncertain one-shot must remain unresolved: %A" other)
        Assert.That(calls, Is.EqualTo(1), "Unknown must not be retried within the same process.")
        // A fresh coordinator has no authoritative local cache. The executor
        // must recover the same durable operation and report semantic replay.
        let mutable replayed = ""
        let recovered =
            WorkQueueReactiveWake.Coordinator(
                ports report (fun original ->
                    replayed <- original.OriginalBrowserOperationId
                    WorkExecutorResult.AlreadyApplied),
                enabled = true)
        match recovered.Wake [WorkWakeTrigger.ManualReconcile] with
        | WorkWakeResult.Reconciled _ -> ()
        | other -> Assert.Fail(sprintf "Restart must reuse the original intent: %A" other)
        Assert.That(replayed, Is.EqualTo(browserOperationId))

    [<Test>]
    member _.ExistingUnresolvedLaunchPreventsAnotherDispatch() =
        let launch: PreviewLaunch =
            { LaunchId = launchId; RoleId = "Worker"; RoleVersion = 4
              Credit = Some selected.WorkId; State = "SubmittedUnregistered(OutcomeUnknown)"
              AccountTokenStatus = "Held"; AdmissionOperationId = Some "reserved"
              AdmissionKind = Some "Admitted"; OriginalBrowserOperationId = Some browserOperationId
              OriginalBrowserOutcome = "Unknown"; OriginalBrowserReceipt = None }
        let mutable effects = 0
        let worker =
            WorkQueueReactiveWake.Coordinator(
                ports (fun () -> { report () with ExistingLaunches = [launch] })
                    (fun _ -> effects <- effects + 1; WorkExecutorResult.Applied),
                enabled = true)
        match worker.Wake [WorkWakeTrigger.WorkTransitionCommitted] with
        | WorkWakeResult.NeedsAttention("OriginalLaunchExists", _) -> ()
        | other -> Assert.Fail(sprintf "Original launch must be recovered: %A" other)
        Assert.That(effects, Is.Zero)
