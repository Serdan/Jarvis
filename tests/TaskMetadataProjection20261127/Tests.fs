module TaskMetadataProjection20261127

open System
open System.Collections
open System.IO
open System.Text
open System.Text.Json
open NUnit.Framework
open Common
open Client.LedgerStore
open Client.ReviewWorkflow
open Client.OrchestrationApi
open AutomationHealthHostedFixture

// Use the existing two-clone canonical fixture, with no live config or remote.
let withFixture action =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-task-metadata-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(root) |> ignore
    let inheritedGit =
        Environment.GetEnvironmentVariables()
        |> Seq.cast<DictionaryEntry>
        |> Seq.map (fun entry -> string entry.Key)
        |> Seq.filter (fun key -> key.StartsWith("GIT_", StringComparison.Ordinal))
        |> Seq.toList
    let original =
        ("XDG_CONFIG_HOME" :: "GIT_CONFIG_NOSYSTEM" :: "GIT_CONFIG_GLOBAL" :: inheritedGit)
        |> List.distinct
        |> List.map (fun key -> key, Environment.GetEnvironmentVariable(key))
    try
        Assert.That(OperatingSystem.IsLinux(), Is.True, "Linux config isolation is required.")
        for key in inheritedGit do Environment.SetEnvironmentVariable(key, null)
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(root, "config"))
        Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1")
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", Path.Combine(root, "gitconfig"))
        let first, second, _, reader, _ = create root
        action first second reader
    finally
        for key, value in original do Environment.SetEnvironmentVariable(key, value)
        Directory.Delete(root, true)

let bytes (s: string) = Encoding.UTF8.GetByteCount s
let head binding = (LedgerStore.fetch binding |> success).Commit
let specPath taskId = $"tasks/{taskId}/spec.html"
let taskPath taskId = $"tasks/{taskId}/task.json"

let getTask reader taskId =
    let cmd : ReviewReadCommand =
        { ProjectName = "Fixture"; Action = "GetTask"; TaskId = taskId
          RequestId = ""; ReviewerId = ""; DocumentPath = "" }
    ReviewApi.read cmd reader

let taskRead reader taskId action generation cursor pageSize =
    let cmd : TaskReadCommand =
        { ProjectName = "Fixture"; LaunchId = null; AgentId = ""; Action = action
          TaskId = taskId; AssignmentGeneration = generation; CheckpointSequence = 0L
          Cursor = cursor; PageSize = pageSize; BeforeOid = null; AfterOid = null; RelativePath = null }
    TaskApi.read cmd reader |> success

let rawAndOuter value =
    // ReviewApi's actual returned object, serialized exactly like the client
    // SignalR dispatcher; the outer converter is real but envelope is locally
    // constructed, not an observed HTTP/MCP result.
    let raw = JsonSerializer.Serialize(value: obj)
    let envelope : AgentCommandResponse =
        { Result = Some raw; Error = None; Instructions = []
          StateChangeEvidence = StateChangeEvidence.Confirmed []
          InboxActivation = None; PendingJob = None }
    raw, JsonSerializer.Serialize envelope

let prop name (element: JsonElement) = element.GetProperty(name: string)
let oid (element: JsonElement) = (prop "specBlobOid" element).GetString()
let html size =
    // Realistic markup/escaping and Unicode; not arbitrary opaque payload.
    let unit = "<p>Operator intent &amp; exact evidence: æøå. Scope and acceptance criteria.</p>"
    let n = (size - bytes "<article></article>") / bytes unit
    let remaining = size - bytes "<article></article>" - n * bytes unit
    "<article>" + String.replicate n unit + String.replicate remaining "x" + "</article>"

let sample reader taskId (expected: string) =
    let result, reads = LedgerStore.measureGitReads(fun () -> getTask reader taskId)
    let raw, outer = rawAndOuter (success result)
    use doc = JsonDocument.Parse raw
    let root = doc.RootElement
    Assert.That((prop "specHtml" root).GetString(), Is.EqualTo(expected))
    TestContext.Progress.WriteLine(
        $"LEGACY task={taskId} html={bytes expected} raw={bytes raw} outer={bytes outer} gitStarts={reads.ProcessStarts} fetches={reads.RemoteFetches} individualBlobReads={reads.IndividualBlobReads} gitStdout={reads.OutputBytes}")
    raw, outer, reads, oid root

[<Test; NonParallelizable>]
let legacy_large_and_no_attempt_controls () =
    withFixture (fun first second reader ->
        let small = html 8192
        let skew = html (512 * 1024)
        for id, body in [ "lead-size", small; "skew-size", skew ] do
            ReviewWorkflow.createTaskWithSpec first (Guid.NewGuid()) id "Task state fixture" body |> success |> ignore
        let before = head second
        let rawSmall, _, smallReads, smallOid = sample reader "lead-size" small
        let rawLarge, _, largeReads, largeOid = sample reader "skew-size" skew
        Assert.That(smallOid, Is.Not.EqualTo(largeOid))
        Assert.That(bytes rawLarge - bytes rawSmall, Is.GreaterThan(500000))
        Assert.That(largeReads.OutputBytes - smallReads.OutputBytes, Is.GreaterThan(500000L))
        for id in ["lead-size"; "skew-size"] do
            use doc = JsonDocument.Parse(JsonSerializer.Serialize(getTask reader id |> success))
            Assert.That((prop "attempt" doc.RootElement).ValueKind, Is.EqualTo(JsonValueKind.Null))
            Assert.That((prop "Lifecycle" (prop "task" doc.RootElement)).GetString(), Is.EqualTo("Open"))
        Assert.That(head first, Is.EqualTo(before))
        Assert.That(head second, Is.EqualTo(before)))

[<Test; NonParallelizable>]
let pinned_revision_and_concurrent_page_continuation () =
    withFixture (fun first second reader ->
        let id = "health-hosted-cas"
        let snapshot, task, attempt, original, originalOid = ReviewWorkflow.getTaskWithSpec first id |> success
        Assert.That(attempt.Value.SpecBlobOid, Is.EqualTo(originalOid))
        let page1 = ReviewWorkflow.getTaskSpecPage second id "" 8 |> success
        Assert.That(page1.HasMore, Is.True)
        let updated = html 8192
        ReviewWorkflow.reviseTaskSpecPinned first (Guid.NewGuid()) id task.Version originalOid "Updated" updated
        |> success |> ignore
        let newer = head first
        Assert.That(newer, Is.Not.EqualTo(snapshot.Commit))
        // Writer advances after first page and before second page. Pinned old
        // cursor must never silently switch to the new body.
        let rest = ReviewWorkflow.getTaskSpecPage second id page1.NextCursor 32768 |> success
        Assert.That(rest.SnapshotCommit, Is.EqualTo(page1.SnapshotCommit))
        Assert.That(rest.SpecBlobOid, Is.EqualTo(originalOid))
        Assert.That(page1.Content + rest.Content, Is.EqualTo(original))
        let now, _, currentAttempt, currentBody, currentOid = ReviewWorkflow.getTaskWithSpec second id |> success
        Assert.That(now.Commit, Is.EqualTo(newer))
        Assert.That(currentBody, Is.EqualTo(updated))
        Assert.That(currentOid, Is.Not.EqualTo(originalOid))
        Assert.That(currentAttempt.Value.SpecBlobOid, Is.EqualTo(originalOid))
        let issuedAt, _, _, issuance, _ = ReviewWorkflow.getAssignment second id 1 |> success
        Assert.That(issuance.SpecBlobOid, Is.EqualTo(originalOid))
        Assert.That(LedgerStore.readHistoricalTaskSpec second issuedAt id issuance.SpecBlobOid |> success, Is.EqualTo(original))
        let assignmentJson = JsonSerializer.Serialize(taskRead reader id "GetAssignment" 1 "" 0)
        Assert.That(assignmentJson, Does.Contain(originalOid))
        let latestPage = ReviewWorkflow.getTaskSpecPage second id "" 32768 |> success
        Assert.That(latestPage.SpecBlobOid, Is.EqualTo(currentOid))
        Assert.That(latestPage.Content, Is.EqualTo(updated))
        let _, _, _, measuredOid = sample reader id updated
        Assert.That(measuredOid, Is.EqualTo(currentOid))
        // Metadata domain reader exists; it deliberately does not establish
        // current-spec presence. It must not be used alone as the new route.
        let oldTask, oldAttempt = ReviewWorkflow.getTaskAt second snapshot id |> success
        Assert.That(oldTask.Version, Is.EqualTo(task.Version))
        Assert.That(oldAttempt.Value.SpecBlobOid, Is.EqualTo(originalOid))
        let otherId = "other-task"
        ReviewWorkflow.createTaskWithSpec first (Guid.NewGuid()) otherId "Other" "<p>Unrelated</p>" |> success |> ignore
        let final = LedgerStore.fetch second |> success
        match LedgerStore.readHistoricalTaskSpec second final otherId originalOid with
        | Error(InvalidBinding _) -> ()
        | result -> Assert.Fail(sprintf "Cross-task spec OID must fail: %A" result)
        Assert.That(head first, Is.EqualTo(final.Commit)))

[<Test; NonParallelizable>]
let missing_and_invalid_spec_controls () =
    withFixture (fun first second reader ->
        let id = "control"
        ReviewWorkflow.createTask first (Guid.NewGuid()) id "Control" |> success |> ignore
        let initial = LedgerStore.fetch first |> success
        let task, _ = LedgerStore.readAt first initial (taskPath id) |> success
        let malformed = task.Value.Replace("\"lifecycle\":\"Open\"", "\"lifecycle\":\"Invalid\"")
        Assert.That(malformed, Is.Not.EqualTo(task.Value))
        // Introduce malformed entities only in disposable ledger. Use the
        // canonical transaction primitive, not a second reader.
        let intent =
            { OperationId = Guid.NewGuid(); Actor = "fixture"; Kind = "TestCorruption"; RequestJson = "{}" }
        LedgerStore.transact first intent (fun _ ->
            Ok [ { Path = taskPath "missing-spec"; ExpectedBlobOid = None
                   Content = task.Value.Replace("\"control\"", "\"missing-spec\"") }
                 { Path = taskPath "invalid-metadata"; ExpectedBlobOid = None
                   Content = malformed.Replace("\"control\"", "\"invalid-metadata\"") }
                 { Path = specPath "invalid-metadata"; ExpectedBlobOid = None; Content = "<p>Valid body</p>" }
                 { Path = taskPath "invalid-spec"; ExpectedBlobOid = None
                   Content = task.Value.Replace("\"control\"", "\"invalid-spec\"") }
                 { Path = specPath "invalid-spec"; ExpectedBlobOid = None; Content = "<article onclick='alert(1)'>unsafe</article>" } ])
        |> success |> ignore
        let before = head first
        for id in ["missing-spec"; "invalid-metadata"] do
            match ReviewWorkflow.getTaskWithSpec second id with
            | Error(LedgerIntegrityError _) -> ()
            | result -> Assert.Fail(sprintf "Malformed canonical task %s: %A" id result)
            match getTask reader id with
            | Error _ -> ()
            | Ok _ -> Assert.Fail($"Producer accepted malformed canonical task {id}")
        match ReviewWorkflow.getTaskSpecPage second "missing-spec" "" 1024 with
        | Error(LedgerIntegrityError _) -> ()
        | result -> Assert.Fail(sprintf "Missing spec page: %A" result)
        match ReviewWorkflow.createTaskWithSpec first (Guid.NewGuid()) "rejected" "Unsafe" "<article onclick='alert(1)'>unsafe</article>" with
        | Error(InvalidBinding _) -> ()
        | result -> Assert.Fail(sprintf "Invalid spec must fail creation: %A" result)
        // The legacy reader is not an HTML validator; explicitly document the
        // boundary rather than claiming metadata can validate body content.
        let _, _, _, body, invalidOid = ReviewWorkflow.getTaskWithSpec second "invalid-spec" |> success
        Assert.That(body, Does.Contain("onclick"))
        Assert.That(invalidOid, Is.Not.Empty)
        Assert.That(head first, Is.EqualTo(before))
        Assert.That(head second, Is.EqualTo(before)))

// Explicit red contract against today's default API. Excluded from the green
// control run; must fail by the asserted body/size/IO contract, not setup.
[<Test; NonParallelizable; Category("ExpectedRed")>]
let metadata_default_contract () =
    withFixture (fun first second reader ->
        let body = html (512 * 1024)
        let id = "metadata-red"
        ReviewWorkflow.createTaskWithSpec first (Guid.NewGuid()) id "Large task" body |> success |> ignore
        let before = head second
        let result, reads = LedgerStore.measureGitReads(fun () -> getTask reader id)
        let raw, outer = rawAndOuter (success result)
        use doc = JsonDocument.Parse raw
        let root = doc.RootElement
        Assert.That(oid root, Is.Not.Empty)
        Assert.That(head first, Is.EqualTo(before))
        TestContext.Progress.WriteLine(
            $"EXPECTED RED html={bytes body} raw={bytes raw} outer={bytes outer} gitStarts={reads.ProcessStarts} fetches={reads.RemoteFetches} individualBlobReads={reads.IndividualBlobReads} gitStdout={reads.OutputBytes}")
        Assert.Multiple(fun () ->
            Assert.That(root.TryGetProperty("specHtml") |> fst, Is.False, "Default metadata must omit specHtml")
            Assert.That(bytes raw, Is.LessThan(4096), "Bound metadata independent of spec size")
            Assert.That(reads.OutputBytes, Is.LessThan(65536L), "Metadata must not read the full body")))
