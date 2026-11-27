module PromptSnapshotAssembly20261127

open System
open System.Collections
open System.IO
open System.Text
open System.Text.Json
open NUnit.Framework
open Client
open Client.LedgerStore
open Client.ReviewWorkflow
open Client.RoleInstructions
open AutomationHealthHostedFixture

let private stable () =
    "You are an autonomous Jarvis executor. Your primary output is completed work through canonical Jarvis tools, not conversational progress reports.\n\n"
    + "Call agent_inbox for your own agent ID, then call get_agent_instructions for the applicable workspace/project/directory AGENTS.md hierarchy. The inbox supplies the focused assignment and resolved role/agent layers; the explicit instruction read supplies AGENTS.md and repeats those layers. Read the pinned task specification, current assignment generation, latest checkpoint and review feedback before acting.\n\n"
let private template () = stable () + "Your project is {{projectName}}. Your agent ID is {{agentId}}."
let private expected agent = stable () + "Your project is Fixture. Your agent ID is " + agent + "."
[<Literal>]
let private pinnedHtml = "<h1>Source preserved</h1>"
[<Literal>]
let private latestHtml = "<h1>LATEST-NOT-ISSUED</h1>"
[<Literal>]
let private roleA = "ROLE-ORDER-FIRST: å"
[<Literal>]
let private roleB = "ROLE-ORDER-SECOND: 界"
[<Literal>]
let private agentBody = "AGENT-SPECIFIC"
[<Literal>]
let private checkpoint2 = "CURRENT-GENERATION-CHECKPOINT"
[<Literal>]
let private revision = "ROLE-REVISED"
let private bytes (s: string) = Encoding.UTF8.GetByteCount s
let private json x = JsonSerializer.Serialize(x)
let private checkpointPath task attempt sequence = $"tasks/{task}/attempts/{attempt}/checkpoints/{sequence}.json"

let private withFixture action =
    Assert.That(OperatingSystem.IsLinux(), Is.True, "Linux disposable configuration required.")
    let root = Path.Combine(Path.GetTempPath(), "jarvis-prompt-snapshot-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(root) |> ignore
    let inheritedGit =
        Environment.GetEnvironmentVariables()
        |> Seq.cast<DictionaryEntry>
        |> Seq.map (fun entry -> string entry.Key)
        |> Seq.filter (fun key -> key.StartsWith("GIT_", StringComparison.Ordinal))
        |> Seq.toList
    let original =
        ("XDG_CONFIG_HOME" :: "GIT_CONFIG_NOSYSTEM" :: "GIT_CONFIG_GLOBAL" :: inheritedGit)
        |> List.distinct |> List.map (fun key -> key, Environment.GetEnvironmentVariable key)
    try
        for key in inheritedGit do Environment.SetEnvironmentVariable(key, null)
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(root, "config"))
        Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1")
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", Path.Combine(root, "gitconfig"))
        let first, second, rt, _, candidate = create root
        action first second rt candidate.TaskId
    finally
        for key, value in original do Environment.SetEnvironmentVariable(key, value)
        Directory.Delete(root, true)

let private setRoles binding =
    for role, body in [ "AssemblyFirst", roleA; "AssemblySecond", roleB ] do
        RoleInstructions.createRole binding "fixture" (Guid.NewGuid()) role "Fixture role" body |> success |> ignore
    RoleInstructions.attachRole binding "fixture" (Guid.NewGuid()) "Worker" "AssemblyFirst" 0 |> success |> ignore
    RoleInstructions.attachRole binding "fixture" (Guid.NewGuid()) "Worker" "AssemblySecond" 1 |> success |> ignore
    RoleInstructions.setAgentInstructions binding "fixture" (Guid.NewGuid()) "Worker" 2 agentBody |> success |> ignore

let private setTemplate binding =
    // Version 8 is the exact current bootstrap-only body; previous versions
    // are fixture-only, not a reconstruction of actual deployment history.
    for version in 0..7 do
        let body = if version = 7 then template () else $"Fixture v{version} " + template ()
        match RoleInstructions.setLaunchTemplate binding "fixture" (Guid.NewGuid()) version body with
        | Ok _ -> ()
        | Error error ->
            let encoded = Convert.ToHexString(Encoding.UTF8.GetBytes body)
            failwithf "Template setup version %d failed: %A; body UTF8 hex=%s; body=%A" version error encoded body

let private getSnapshot binding = LedgerStore.fetch binding |> success
let private render binding snapshot agent = RoleInstructions.renderLaunchPromptAt binding snapshot "Fixture" agent |> success
let private inbox binding snapshot agent = ReviewWorkflow.getAgentInboxAt binding snapshot agent |> success
let private checkpoint binding task generation summary =
    let sequence = (getSnapshot binding).Header.NextSequence
    ReviewWorkflow.checkpointAttempt binding (Guid.NewGuid()) task "Worker" 0 0 generation
        summary "Continue fixture" "" "owner-reported fixture evidence" |> success |> ignore
    sequence

let private prepare binding task =
    // First generation checkpoints straddle a decimal boundary (actual
    // canonical sequences, not fabricated checkpoint filenames).
    let next = (getSnapshot binding).Header.NextSequence
    let mutable boundary = 10L
    while boundary <= next do boundary <- boundary * 10L
    while (getSnapshot binding).Header.NextSequence < boundary - 1L do
        checkpoint binding task 1 "Padding checkpoint" |> ignore
    let oldLow = checkpoint binding task 1 "OLD-GENERATION-LOW"
    let oldHigh = checkpoint binding task 1 "OLD-GENERATION-HIGH"
    Assert.That(oldHigh, Is.EqualTo(oldLow + 1L))
    Assert.That(String.CompareOrdinal(string oldLow, string oldHigh), Is.GreaterThan(0))
    let snap = getSnapshot binding
    let _, attempt = ReviewWorkflow.getTaskAt binding snap task |> success
    let agent = ReviewWorkflow.getAgentWorkingTurnAt binding snap "Worker" |> success
    ReviewWorkflow.recoverAgentAssignment binding (Guid.NewGuid()) task "Worker" attempt.Value.Version agent.Version 1
        "Explicit disposable recovery" |> success |> ignore
    ReviewWorkflow.resumeAgent binding (Guid.NewGuid()) task "Worker" 0 0 2 |> success |> ignore
    let current = checkpoint binding task 2 checkpoint2
    let snap = getSnapshot binding
    let t, a = ReviewWorkflow.getTaskAt binding snap task |> success
    ReviewWorkflow.reviseTaskSpecPinned binding (Guid.NewGuid()) task t.Version a.Value.SpecBlobOid
        "Later canonical title" latestHtml |> success |> ignore
    setRoles binding
    setTemplate binding
    let snap = getSnapshot binding
    let _, a = ReviewWorkflow.getTaskAt binding snap task |> success
    let agent = ReviewWorkflow.getAgentWorkingTurnAt binding snap "Worker" |> success
    ReviewWorkflow.yieldAgent binding (Guid.NewGuid()) task "Worker" a.Value.Version agent.Version 2
        "LATEST-ELIGIBLE-YIELD" "Continue fixture" |> success |> ignore
    oldLow, oldHigh, current, (getSnapshot binding).Header.NextSequence - 1L

let private writeGuidance (binding: LedgerBinding) =
    File.WriteAllText(Path.Combine(Path.GetDirectoryName(binding.RepositoryPath), "AGENTS.md"), "WORKSPACE-GUIDANCE")
    File.WriteAllText(Path.Combine(binding.RepositoryPath, "AGENTS.md"), "PROJECT-GUIDANCE")
    Directory.CreateDirectory(Path.Combine(binding.RepositoryPath, "src")) |> ignore
    File.WriteAllText(Path.Combine(binding.RepositoryPath, "src", "AGENTS.md"), "DIRECTORY-GUIDANCE")

let private explicitInstructions (rt: Runtime) =
    let target = Client.ProjectPaths.ProjectTarget.parse "Fixture" |> success
    let command : Common.GetAgentInstructionsCommand =
        { ProjectName = "Fixture"; AgentId = "Worker"; Directory = "src"; Cursor = None }
    Client.AgentInstructions.getExplicit target command rt |> success

let private submitFeedback binding task owner reviewer summary =
    let review = Guid.NewGuid()
    let source = git binding.RepositoryPath [ "rev-parse"; "HEAD" ]
    ReviewWorkflow.submitBranchReview binding (Guid.NewGuid()) task owner review "" source summary "" "" |> success |> ignore
    ReviewWorkflow.claimBranchReview binding (Guid.NewGuid()) task review reviewer 1 |> success |> ignore
    ReviewWorkflow.putReviewDocument binding (Guid.NewGuid()) task review reviewer "findings.md" "" summary |> success |> ignore
    ReviewWorkflow.decideBranchReview binding (Guid.NewGuid()) task review reviewer 2 RequestChanges |> success |> ignore
    review

[<Test; NonParallelizable>]
let legacy_render_and_exact_separate_context_controls () =
    withFixture (fun binding reader rt task ->
        let oldLow, oldHigh, current, latest = prepare binding task
        writeGuidance binding
        for agent in [ "Reviewer"; "Interpreter"; "Other Worker" ] do
            ReviewWorkflow.registerAgent binding (Guid.NewGuid()) agent |> success |> ignore
        let ownReview = submitFeedback binding task "Worker" "Reviewer" "OWN-FOCUSED-REVIEW"
        let oldReview = submitFeedback binding "old-task" "Worker" "Reviewer" "OWN-UNRELATED-REVIEW"
        let foreignReview = submitFeedback binding "foreign-task" "Other Worker" "Reviewer" "FOREIGN-REVIEW"
        let pending = Guid.NewGuid()
        let source = git binding.RepositoryPath [ "rev-parse"; "HEAD" ]
        ReviewWorkflow.submitBranchReview binding (Guid.NewGuid()) "queue-task" "Other Worker" pending "" source "QUEUE" "" "" |> success |> ignore
        let snapshot = getSnapshot reader
        let refsBefore = git binding.RepositoryPath [ "show-ref" ]
        let prompt = render reader snapshot "Worker"
        Assert.That(prompt.TemplateVersion, Is.EqualTo(8))
        Assert.That(prompt.Prompt, Is.EqualTo(expected "Worker"))
        Assert.That(render reader snapshot "Worker", Is.EqualTo(prompt))
        let explicit = RoleInstructions.renderLaunchPrompt reader "Fixture" "Worker" |> success
        Assert.That(explicit, Is.EqualTo(prompt))
        let own = inbox reader snapshot "Worker"
        Assert.That(own.Agent.State, Is.EqualTo("Ready"))
        Assert.That(own.Working.IsNone, Is.True)
        let focus = own.Focused.Value
        Assert.That(focus.Attempt.AssignmentGeneration, Is.EqualTo(2))
        Assert.That(own.InstructionLayers.Documents |> List.map _.Content, Is.EqualTo(box [roleA; roleB; agentBody]))
        Assert.That((RoleInstructions.resolveAt reader snapshot "Worker" |> success), Is.EqualTo(own.InstructionLayers))
        let _, _, _, issuance, receipt = ReviewWorkflow.getAssignment reader task 2 |> success
        Assert.That(issuance.SpecBlobOid, Is.EqualTo(focus.Attempt.SpecBlobOid))
        Assert.That(receipt.IsSome, Is.True)
        let _, _, _, newest, newestOid = ReviewWorkflow.getTaskWithSpec reader task |> success
        let issued = LedgerStore.readHistoricalTaskSpec reader snapshot task issuance.SpecBlobOid |> success
        Assert.That(issued, Is.EqualTo(pinnedHtml))
        Assert.That(newest, Is.EqualTo(latestHtml))
        Assert.That(newestOid, Is.Not.EqualTo(issuance.SpecBlobOid))
        let checkpoints =
            [ oldLow; oldHigh; current; latest ]
            |> List.map (fun seq -> ReviewWorkflow.getCheckpoint reader task seq |> success |> snd)
        Assert.That(checkpoints |> List.map _.AssignmentGeneration, Is.EqualTo(box [1;1;2;2]))
        Assert.That(checkpoints |> List.map _.Sequence, Is.EqualTo(box [oldLow;oldHigh;current;latest]))
        Assert.That(checkpoints[2].Summary, Is.EqualTo(checkpoint2))
        Assert.That(checkpoints[3].Summary, Is.EqualTo("LATEST-ELIGIBLE-YIELD"))
        // Exact readers are not selection/assembly APIs: an older generation is readable.
        Assert.That(checkpoints[0].OwnerAgentId, Is.EqualTo("Worker"))
        match ReviewWorkflow.getCheckpoint reader task (latest + 100000L) with
        | Error(StaleEntityVersion _) -> ()
        | result -> Assert.Fail(sprintf "Missing checkpoint must be explicit: %A" result)
        match ReviewWorkflow.getAssignment reader task 3 with
        | Error(StaleEntityVersion _) -> ()
        | result -> Assert.Fail(sprintf "Future generation must be rejected: %A" result)
        match LedgerStore.readHistoricalTaskSpec reader snapshot "foreign-task" issuance.SpecBlobOid with
        | Error(InvalidBinding _) -> ()
        | result -> Assert.Fail(sprintf "Foreign task spec must be rejected: %A" result)
        Assert.That(own.UnacknowledgedFeedback |> List.map _.RequestId,
                    Is.EquivalentTo([ownReview.ToString("N"); oldReview.ToString("N")] :> string seq))
        Assert.That(own.UnacknowledgedFeedback |> List.forall (fun n -> isNull n.Feedback), Is.True)
        let _, ownDoc = ReviewWorkflow.getReviewDocument reader task ownReview "findings.md" |> success
        Assert.That(ownDoc.Content, Is.EqualTo("OWN-FOCUSED-REVIEW"))
        let _, foreignDoc = ReviewWorkflow.getReviewDocument reader "foreign-task" foreignReview "findings.md" |> success
        Assert.That(foreignDoc.Content, Is.EqualTo("FOREIGN-REVIEW"))
        // Document lookup is not a self-inbox filter; assembly must select applicable references.
        let reviewer = inbox reader snapshot "Reviewer"
        Assert.That(reviewer.Focused.IsNone, Is.True)
        Assert.That(reviewer.AvailableReviews |> List.exists (fun r -> r.ReviewId = pending.ToString("N")), Is.True)
        Assert.That((render reader snapshot "Reviewer").Prompt, Is.EqualTo(expected "Reviewer"))
        let interpreter = inbox reader snapshot "Interpreter"
        Assert.That(interpreter.Focused.IsNone, Is.True)
        Assert.That((render reader snapshot "Interpreter").Prompt, Is.EqualTo(expected "Interpreter"))
        let guidance = explicitInstructions rt
        use guidanceDoc = JsonDocument.Parse(guidance.ToJsonString())
        let hierarchy = guidanceDoc.RootElement.GetProperty("AGENTS.md")
        Assert.That(hierarchy.EnumerateObject() |> Seq.map (fun p -> p.Value.GetString()) |> Seq.toList,
                    Is.EqualTo(box ["WORKSPACE-GUIDANCE";"PROJECT-GUIDANCE";"DIRECTORY-GUIDANCE"]))
        let roleDocs = guidanceDoc.RootElement.GetProperty("instructionLayers").GetProperty("Documents")
        Assert.That(roleDocs.EnumerateArray() |> Seq.map (fun p -> p.GetProperty("Content").GetString()) |> Seq.toList,
                    Is.EqualTo(box [roleA;roleB;agentBody]))
        // Raw UTF-8 prompt vs serialized reader outputs: not provider tokens or cache.
        let separate = bytes (json own) + bytes (guidance.ToJsonString()) + bytes (json (newest, newestOid))
        TestContext.Out.WriteLine($"renderTextBytes={bytes prompt.Prompt}; renderJsonProxyBytes={bytes (json prompt)}; separateContextJsonProxyBytes={separate}; historicalSpecBytes={bytes issued}; checkpointJsonProxyBytes={bytes (json checkpoints[3])}")
        Assert.That((getSnapshot reader).Commit, Is.EqualTo(snapshot.Commit))
        Assert.That(git binding.RepositoryPath [ "show-ref" ], Is.EqualTo(refsBefore))
        // Relevant role and local guidance mutations are invisible to the legacy prompt.
        RoleInstructions.updateRole binding "fixture" (Guid.NewGuid()) "AssemblyFirst" 1 "Fixture role" revision |> success |> ignore
        File.WriteAllText(Path.Combine(binding.RepositoryPath,"src","AGENTS.md"), "DIRECTORY-REVISED")
        let updated = getSnapshot reader
        Assert.That(updated.Commit, Is.Not.EqualTo(snapshot.Commit))
        Assert.That((render reader updated "Worker").PromptSha256, Is.EqualTo(prompt.PromptSha256))
        Assert.That((inbox reader updated "Worker").InstructionLayers.Documents.Head.Content, Is.EqualTo(revision))
        Assert.That((inbox reader snapshot "Worker").InstructionLayers.Documents.Head.Content, Is.EqualTo(roleA))
        Assert.That((explicitInstructions rt).ToJsonString(), Does.Contain("DIRECTORY-REVISED"))
        // A taskless explicit start is distinct from rendering.
        ReviewWorkflow.startTasklessWork binding (Guid.NewGuid()) "Interpreter" interpreter.Agent.Version "Interpret fixture request" |> success |> ignore
        let after = getSnapshot reader
        let taskless = inbox reader after "Interpreter"
        Assert.That(taskless.Agent.State, Is.EqualTo("Working"))
        Assert.That(taskless.Focused.IsNone, Is.True)
        Assert.That((render reader after "Interpreter").Prompt, Is.EqualTo(expected "Interpreter")))

[<Test; NonParallelizable; Explicit("Intentional new-mode regression: legacy render lacks issued context")>]
let new_mode_must_include_exact_issued_context_not_latest_spec () =
    withFixture (fun binding reader _ task ->
        let _, _, _, _ = prepare binding task
        let snapshot = getSnapshot reader
        let prompt = render reader snapshot "Worker"
        // Contract-level RED against the actual legacy entry point; no pretend
        // new-mode renderer, flag, or check-in API exists on this baseline.
        Assert.That(prompt.Prompt, Does.Not.Contain(latestHtml))
        Assert.Multiple(fun () ->
            Assert.That(prompt.Prompt, Does.Contain(pinnedHtml), "Issued task specification missing")
            Assert.That(prompt.Prompt, Does.Contain(roleA), "Ordered role context missing")
            Assert.That(prompt.Prompt, Does.Contain(roleB), "Ordered role context missing")
            Assert.That(prompt.Prompt, Does.Contain(agentBody), "Agent context missing")
            Assert.That(prompt.Prompt, Does.Contain("LATEST-ELIGIBLE-YIELD"), "Current generation continuation missing")))

[<Test; NonParallelizable>]
let blocked_dependencies_are_snapshot_bound_and_not_launch_authority () =
    withFixture (fun binding reader _ task ->
        setTemplate binding
        ReviewWorkflow.registerAgent binding (Guid.NewGuid()) "Dependency Owner" |> success |> ignore
        let definition : Client.ChallengeWorkflow.ChallengeConditionSpec =
            { OwnerKind = "Agent"; OwnerAgentId = "Dependency Owner"; SuggestedAgentId = ""; Description = "OWN-DEPENDENCY-CONDITION" }
        let challenge = Guid.NewGuid()
        Client.ChallengeWorkflow.ChallengeWorkflow.create binding challenge "Worker" "OWN-DEPENDENCY" task [definition] |> success |> ignore
        let foreign = Guid.NewGuid()
        Client.ChallengeWorkflow.ChallengeWorkflow.create binding foreign "Dependency Owner" "FOREIGN-DEPENDENCY" "" [definition] |> success |> ignore
        let snap = getSnapshot binding
        let t, a = ReviewWorkflow.getTaskAt binding snap task |> success
        let agent = ReviewWorkflow.getAgentWorkingTurnAt binding snap "Worker" |> success
        Client.ChallengeWorkflow.ChallengeWorkflow.blockAttempt binding (Guid.NewGuid()) "Worker" task a.Value.AttemptId
            t.Version a.Value.Version agent.Version a.Value.AssignmentGeneration "Waiting for dependency" [challenge.ToString("N")]
        |> success |> ignore
        let blocked = getSnapshot reader
        let raw = inbox reader blocked "Worker"
        let enriched = Client.ChallengeWorkflow.ChallengeWorkflow.enrichInboxAt reader blocked "Worker" raw |> success
        Assert.That(enriched.Focused.IsNone, Is.True)
        Assert.That(enriched.Blocked.Length, Is.EqualTo(1))
        Assert.That(enriched.BlockedChallengeWaits.Head.ChallengeIds, Is.EqualTo(box [challenge.ToString("N")]))
        Assert.That(enriched.ChallengeDependencies |> List.map _.ChallengeId, Is.EqualTo(box [challenge.ToString("N")]))
        Assert.That(enriched.ChallengeDependenciesOmitted, Is.EqualTo(0))
        Assert.That((render reader blocked "Worker").Prompt, Is.EqualTo(expected "Worker"))
        Assert.That((getSnapshot reader).Commit, Is.EqualTo(blocked.Commit))
        match Client.ChallengeWorkflow.ChallengeWorkflow.enrichInboxAt reader blocked "Dependency Owner" raw with
        | Error(LedgerIntegrityError _) -> ()
        | result -> Assert.Fail(sprintf "Foreign actor enrichment must fail: %A" result))

[<Test; NonParallelizable>]
let malformed_checkpoint_and_valid_shape_wrong_identity_are_distinct () =
    withFixture (fun binding reader _ task ->
        let sequence = checkpoint binding task 1 "Original checkpoint"
        let snapshot = getSnapshot reader
        let _, record = ReviewWorkflow.getCheckpoint reader task sequence |> success
        let path = checkpointPath task record.AttemptId sequence
        let body, oid = LedgerStore.readAt reader snapshot path |> success
        let replace content =
            let snap = getSnapshot binding
            let _, current = LedgerStore.readAt binding snap path |> success
            LedgerStore.transact binding
                { OperationId = Guid.NewGuid(); Actor = "fixture"; Kind = "CorruptCheckpointFixture"; RequestJson = "{}" }
                (fun _ -> Ok [{ Path = path; ExpectedBlobOid = current; Content = content }])
            |> success |> ignore
        // Deliberate corruption of disposable ledger, not production authority.
        Assert.That(oid.IsSome, Is.True)
        replace "{"
        match ReviewWorkflow.getCheckpoint reader task sequence with
        | Error(LedgerIntegrityError _) -> ()
        | result -> Assert.Fail(sprintf "Malformed checkpoint must fail: %A" result)
        let node = System.Text.Json.Nodes.JsonNode.Parse(body.Value).AsObject()
        node["ownerAgentId"] <- System.Text.Json.Nodes.JsonValue.Create("Foreign Owner")
        node["attemptId"] <- System.Text.Json.Nodes.JsonValue.Create(Guid.NewGuid().ToString("N"))
        node["assignmentGeneration"] <- System.Text.Json.Nodes.JsonValue.Create(99)
        node["sequence"] <- System.Text.Json.Nodes.JsonValue.Create(sequence + 1L)
        replace (node.ToJsonString())
        // Characterization of existing historical reader defect: the reader
        // locates a file but does not check these embedded identity fields.
        // W27 owns reader correction, not this assembly test-only lease.
        let _, wrong = ReviewWorkflow.getCheckpoint reader task sequence |> success
        Assert.That(wrong.OwnerAgentId, Is.EqualTo("Foreign Owner"))
        Assert.That(wrong.AttemptId, Is.Not.EqualTo(record.AttemptId))
        Assert.That(wrong.AssignmentGeneration, Is.EqualTo(99))
        Assert.That(wrong.Sequence, Is.Not.EqualTo(sequence)))
