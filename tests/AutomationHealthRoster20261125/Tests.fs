module AutomationHealthRoster20261125

open System
open System.IO
open System.Threading.Tasks
open NUnit.Framework
open Client
open Client.LedgerStore
open Client.ReviewWorkflow
open Client.NativeHealth
open Common.SignalR
open AutomationHealthHostedFixture

let withFixture run =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-health-roster-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(root) |> ignore
    try
        let first, second, rt, _, candidate = create root
        run first second rt candidate
    finally
        Directory.Delete(root, true)

let settings () = { Enabled = true; InactivitySeconds = 600 }
// Test-only activity seam; authenticated ingress is covered by the existing hosted suite.
let deps rt =
    let now = DateTimeOffset.Parse("2026-11-25T00:00:00Z")
    { Enumerate = fun () -> NativeHealth.enumerate rt
      Current = NativeHealth.current rt
      Activity = fun _ _ -> Task.FromResult(Ok { LastSeenAtUtc = Some(now.AddSeconds(-600.)); ServerNowUtc = now })
      Recover = NativeHealth.recover rt }

let runCheck rt = NativeHealth.checkOnce (settings ()) (deps rt) |> fun t -> t.GetAwaiter().GetResult()
let snapshot binding = LedgerStore.fetch binding |> success
let subscribe binding id enabled =
    ChallengeWorkflow.ChallengeWorkflow.setSubscription binding (Guid.NewGuid()) id enabled |> success |> ignore

[<TestCase("control")>]
[<TestCase("enabled")>]
[<TestCase("disabled")>]
[<TestCase("unrelated")>]
let recoveryWithCanonicalSubscription scenario =
    withFixture (fun first second rt candidate ->
        match scenario with
        | "control" -> ()
        | "enabled" -> subscribe first "Worker" true
        | "disabled" ->
            subscribe first "Worker" true
            subscribe first "Worker" false
        | "unrelated" ->
            ReviewWorkflow.registerAgent first (Guid.NewGuid()) "Other" |> success |> ignore
            subscribe first "Other" true
        | _ -> failwith "Unknown scenario"
        let before = snapshot first
        if scenario <> "control" then
            let paths = LedgerStore.listAt first before "agents" |> success
            Assert.That(paths |> List.exists (fun p -> p.EndsWith(".challenge-subscription.json", StringComparison.Ordinal)), Is.True)
        let roster = NativeHealth.enumerate rt |> success
        Assert.That(roster |> List.map (fun c -> c.AgentId), Is.EqualTo(["Worker"] :> obj))
        let expected = List.exactlyOne roster
        let summary = runCheck rt
        Assert.That(summary.Errors, Is.Empty)
        Assert.That(summary.Recoveries, Is.EqualTo(1))
        let _, receipt = LedgerStore.getOperationStatus second (NativeHealth.recoveryOperationId expected) |> success
        Assert.That(receipt.IsSome, Is.True)
        Assert.That(receipt.Value.Type, Is.EqualTo("RecoverAgentAssignment"))
        let _, _, attempt = ReviewWorkflow.getTask second candidate.TaskId |> success
        Assert.That(attempt.Value.AssignmentGeneration, Is.EqualTo(candidate.Generation + 1))
        Assert.That(attempt.Value.SpecBlobOid, Is.EqualTo(candidate.SpecBlobOid))
        Assert.That(attempt.Value.SourceRef, Is.EqualTo(candidate.SourceRef))
        Assert.That(NativeHealth.enumerate rt |> success, Is.Empty))

let corrupt binding paths =
    let intent = { OperationId = Guid.NewGuid(); Actor = "fixture"; Kind = "Fixture.Corrupt"; RequestJson = "{}" }
    LedgerStore.transact binding intent (fun snap ->
        paths |> List.map (fun path ->
            let _, oid = LedgerStore.readAt binding snap path |> success
            { Path = path; ExpectedBlobOid = oid; Content = "{" }) |> Ok)
    |> success |> ignore

[<TestCase("agents/V29ya2Vy.json")>]
[<TestCase("agents/T3RoZXI.json")>]
let malformedActualRecordStillHolds path =
    withFixture (fun first second rt candidate ->
        subscribe first "Worker" true
        corrupt first [path]
        let before = snapshot first
        let summary = runCheck rt
        Assert.That(summary.Recoveries, Is.Zero)
        Assert.That(summary.Errors, Has.Some.Contains("Malformed registered agent"))
        Assert.That((snapshot second).Commit, Is.EqualTo(before.Commit))
        let _, receipt = LedgerStore.getOperationStatus second (NativeHealth.recoveryOperationId candidate) |> success
        Assert.That(receipt.IsNone, Is.True))

[<TestCase(511, "Malformed registered agent")>]
[<TestCase(512, "Registered agent enumeration exceeds bound")>]
let actualAgentBoundStillHolds extra expectedError =
    withFixture (fun first second rt _ ->
        subscribe first "Worker" true
        // At 512 agent paths the sidecar must not exhaust the bound;
        // at 513 the pre-parse bound must still win over malformed bodies.
        corrupt first [for i in 1..extra -> sprintf "agents/fixture-%04d.json" i]
        let before = snapshot first
        let summary = runCheck rt
        Assert.That(summary.Recoveries, Is.Zero)
        Assert.That(summary.Errors, Has.Some.Contains(expectedError))
        Assert.That((snapshot second).Commit, Is.EqualTo(before.Commit)))

[<Test>]
let ineligibleSubscribedAgentDoesNotRecover () =
    withFixture (fun first second rt candidate ->
        subscribe first "Worker" true
        // Explicit prior recovery makes the task Ready, not Working.
        ReviewWorkflow.recoverAgentAssignmentWithTaskVersionAs first "AuthenticatedUser" (Guid.NewGuid())
            candidate.TaskId candidate.AgentId candidate.TaskVersion candidate.AttemptVersion candidate.AgentVersion
            candidate.Generation "Fixture generation change" |> success |> ignore
        let before = snapshot first
        let summary = runCheck rt
        Assert.That(summary.Candidates, Is.Zero)
        Assert.That(summary.Recoveries, Is.Zero)
        Assert.That(summary.Errors, Is.Empty)
        Assert.That((snapshot second).Commit, Is.EqualTo(before.Commit)))

[<Test>]
let registeredAutomationHealthPortsRequireFreshProjectOptIn () =
    withFixture (fun first second rt original ->
        let gated =
            { deps rt with
                Enumerate = fun () -> NativeHealth.enumerateAutomation rt
                Current = NativeHealth.currentAutomation rt
                Recover = NativeHealth.recoverAutomation rt }
        let check ports = NativeHealth.checkOnce (settings ()) ports |> fun job -> job.GetAwaiter().GetResult()
        Assert.That(NativeHealth.enumerateAutomation rt |> success, Is.Empty)
        Assert.That(check gated |> _.Recoveries, Is.Zero)
        ProjectCapacityPolicy.updateAutomation first "fixture" (Guid.NewGuid()) 0 true None Map.empty |> success |> ignore
        let candidate = NativeHealth.enumerateAutomation rt |> success |> List.exactlyOne
        Assert.That(candidate.AgentId, Is.EqualTo(original.AgentId))
        let mutable reads = 0
        let race =
            { gated with
                Activity = fun project agent ->
                    reads <- reads + 1
                    if reads = 2 then
                        ProjectCapacityPolicy.updateAutomation second "fixture" (Guid.NewGuid()) 1 false None Map.empty |> success |> ignore
                    gated.Activity project agent }
        let held = check race
        Assert.That(held.Recoveries, Is.Zero)
        Assert.That(reads, Is.EqualTo(2))
        Assert.That(NativeHealth.currentAutomation rt candidate |> success, Is.EqualTo(None))
        let _, receipt = LedgerStore.getOperationStatus first (NativeHealth.recoveryOperationId candidate) |> success
        Assert.That(receipt.IsNone, Is.True)
        ProjectCapacityPolicy.updateAutomation first "fixture" (Guid.NewGuid()) 2 true None Map.empty |> success |> ignore
        Assert.That(check gated |> _.Recoveries, Is.EqualTo(1))
        let _, receipt = LedgerStore.getOperationStatus second (NativeHealth.recoveryOperationId candidate) |> success
        Assert.That(receipt.IsSome, Is.True))
