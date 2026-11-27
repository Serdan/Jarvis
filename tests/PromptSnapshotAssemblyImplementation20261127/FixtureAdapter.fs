module PromptSnapshotAssemblyFixtureAdapter

open System
open System.Collections
open System.IO
open NUnit.Framework
open Client.LedgerStore
open Client.ReviewWorkflow
open Client.RoleInstructions
open AutomationHealthHostedFixture

module A = Client.PromptSnapshotAssembly
module Accounting = Client.PromptContextAccounting

// The exact accepted fixture is linked unchanged. Its private setup and explicit
// RED target the legacy renderer, so this adapter independently uses the same
// disposable Git fixture and real canonical readers. It does not substitute a
// new implementation behind the legacy test or claim registered route parity.
[<Test; NonParallelizable>]
let actual_issued_fixture_context_assembles_without_activation () =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-prompt-implementation-" + Guid.NewGuid().ToString("N"))
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
        let binding, reader, _, _, candidate = create root
        let task = candidate.TaskId
        let agent = "Worker"
        let snapshot () = LedgerStore.fetch binding |> success
        ReviewWorkflow.checkpointAttempt binding (Guid.NewGuid()) task agent 0 0 1
            "OLD-GENERATION" "Old continuation" "" "" |> success |> ignore
        let beforeRecovery = snapshot ()
        let _, attempt = ReviewWorkflow.getTaskAt binding beforeRecovery task |> success
        let working = ReviewWorkflow.getAgentWorkingTurnAt binding beforeRecovery agent |> success
        ReviewWorkflow.recoverAgentAssignment binding (Guid.NewGuid()) task agent attempt.Value.Version working.Version 1
            "Explicit disposable recovery" |> success |> ignore
        ReviewWorkflow.resumeAgent binding (Guid.NewGuid()) task agent 0 0 2 |> success |> ignore
        let current = snapshot ()
        let t, a = ReviewWorkflow.getTaskAt binding current task |> success
        let latestHtml = "<h1>LATEST-NOT-ISSUED</h1>"
        ReviewWorkflow.reviseTaskSpecPinned binding (Guid.NewGuid()) task t.Version a.Value.SpecBlobOid
            "Later canonical title" latestHtml |> success |> ignore
        for role, body in [ "AssemblyFirst", "ROLE-ORDER-FIRST: å"; "AssemblySecond", "ROLE-ORDER-SECOND: 界" ] do
            RoleInstructions.createRole binding "fixture" (Guid.NewGuid()) role "Fixture role" body |> success |> ignore
        RoleInstructions.attachRole binding "fixture" (Guid.NewGuid()) agent "AssemblyFirst" 0 |> success |> ignore
        RoleInstructions.attachRole binding "fixture" (Guid.NewGuid()) agent "AssemblySecond" 1 |> success |> ignore
        RoleInstructions.setAgentInstructions binding "fixture" (Guid.NewGuid()) agent 2 "AGENT-SPECIFIC" |> success |> ignore
        let common = "You are an autonomous Jarvis executor. Your primary output is completed work through canonical Jarvis tools, not conversational progress reports.\n\n"
                     + "Call agent_inbox for your own agent ID, then call get_agent_instructions for the applicable workspace/project/directory AGENTS.md hierarchy. The inbox supplies the focused assignment and resolved role/agent layers; the explicit instruction read supplies AGENTS.md and repeats those layers. Read the pinned task specification, current assignment generation, latest checkpoint and review feedback before acting."
        let identityTemplate = "Your project is {{projectName}}. Your agent ID is {{agentId}}."
        for version in 0..7 do
            let body = if version = 7 then common + "\n\n" + identityTemplate else $"Fixture v{version} " + common + "\n\n" + identityTemplate
            RoleInstructions.setLaunchTemplate binding "fixture" (Guid.NewGuid()) version body |> success |> ignore
        let ready = snapshot ()
        let _, a = ReviewWorkflow.getTaskAt binding ready task |> success
        let working = ReviewWorkflow.getAgentWorkingTurnAt binding ready agent |> success
        let sequence = ready.Header.NextSequence
        ReviewWorkflow.yieldAgent binding (Guid.NewGuid()) task agent a.Value.Version working.Version 2
            "LATEST-ELIGIBLE-YIELD" "Continue fixture" |> success |> ignore
        let snap = LedgerStore.fetch reader |> success
        let refsBefore = git binding.RepositoryPath [ "show-ref" ]
        let inbox = ReviewWorkflow.getAgentInboxAt reader snap agent |> success
        let focused = inbox.Focused.Value
        let scope : A.Scope = { Project = "Fixture"; Agent = agent; Snapshot = snap.Commit }
        let scoped value : A.Scoped<_> = { Scope = scope; Value = value }
        let resolved value = A.Resolved (scoped value)
        let pin : A.Pin =
            { Task = focused.Task.TaskId; Attempt = focused.Attempt.AttemptId
              Generation = focused.Attempt.AssignmentGeneration; SpecBlob = focused.Attempt.SpecBlobOid }
        let issuedSnapshot, _, _, issue, receipt = ReviewWorkflow.getAssignment reader task pin.Generation |> success
        Assert.That(issuedSnapshot.Commit, Is.EqualTo(snap.Commit))
        Assert.That(receipt.IsSome, Is.True)
        Assert.That(issue.OwnerAgentId, Is.EqualTo(agent))
        let issuePin : A.Pin =
            { Task = issue.TaskId; Attempt = issue.AttemptId; Generation = issue.Generation; SpecBlob = issue.SpecBlobOid }
        let html = LedgerStore.readHistoricalTaskSpec reader snap task issue.SpecBlobOid |> success
        let _, _, _, newest, _ = ReviewWorkflow.getTaskWithSpec reader task |> success
        Assert.That(newest, Is.EqualTo(latestHtml))
        let checkpointSnapshot, checkpoint = ReviewWorkflow.getCheckpoint reader task sequence |> success
        Assert.That(checkpointSnapshot, Is.EqualTo(snap.Commit))
        Assert.That(checkpoint.OwnerAgentId, Is.EqualTo(agent))
        let cp : A.Continuation =
            { Pin = { issuePin with Task = checkpoint.TaskId; Attempt = checkpoint.AttemptId; Generation = checkpoint.AssignmentGeneration }
              Sequence = checkpoint.Sequence; Summary = checkpoint.Summary
              NextAction = checkpoint.NextAction; OwnerReportedEvidence = Option.ofObj checkpoint.Evidence }
        let layers =
            inbox.InstructionLayers.Documents
            |> List.mapi (fun index doc ->
                let value : A.Layer =
                    { Kind = if doc.Scope = "role" then A.Role elif doc.Scope = "agent" then A.Agent else failwith "Unexpected fixture layer"
                      Ordinal = index; SourceKey = doc.SourcePath; Version = Some doc.Version; Text = doc.Content }
                scoped value)
        Assert.That(inbox.InstructionLayers.SnapshotCommit, Is.EqualTo(snap.Commit))
        let rendered = RoleInstructions.renderLaunchPromptAt reader snap "Fixture" agent |> success
        let identity = "Your project is Fixture. Your agent ID is Worker."
        Assert.That(rendered.Prompt, Is.EqualTo(common + "\n\n" + identity))
        let template : A.Template =
            { Version = rendered.TemplateVersion; Common = common; IdentitySuffix = identity }
        let input : A.Input =
            { Scope = scope; Template = resolved template; Layers = A.Resolved layers
              Focus = resolved (A.Assigned
                  { Pin = pin; Issuance = resolved issuePin
                    Spec = resolved { Task = task; Blob = issue.SpecBlobOid; Html = html }
                    SelectedCheckpoint = A.Resolved (Some sequence); Checkpoint = A.Resolved (Some (scoped cp)) })
              Actions = A.Resolved [] }
        let output = A.assemble input None Accounting.noBudgets |> success
        Assert.Multiple(fun () ->
            Assert.That(output.Prompt, Does.Contain("<h1>Source preserved</h1>"))
            Assert.That(output.Prompt, Does.Contain("ROLE-ORDER-FIRST: å"))
            Assert.That(output.Prompt, Does.Contain("ROLE-ORDER-SECOND: 界"))
            Assert.That(output.Prompt, Does.Contain("AGENT-SPECIFIC"))
            Assert.That(output.Prompt, Does.Contain("LATEST-ELIGIBLE-YIELD"))
            Assert.That(output.Prompt, Does.Not.Contain(latestHtml))
            Assert.That(output.Prompt, Does.Not.Contain("OLD-GENERATION")))
        Assert.That(inbox.Agent.State, Is.EqualTo("Ready"))
        Assert.That((ReviewWorkflow.getAgentInboxAt reader snap agent |> success).Agent.State, Is.EqualTo("Ready"))
        Assert.That((LedgerStore.fetch reader |> success).Commit, Is.EqualTo(snap.Commit))
        Assert.That(git binding.RepositoryPath [ "show-ref" ], Is.EqualTo(refsBefore))
    finally
        for key, value in original do Environment.SetEnvironmentVariable(key, value)
        Directory.Delete(root, true)
