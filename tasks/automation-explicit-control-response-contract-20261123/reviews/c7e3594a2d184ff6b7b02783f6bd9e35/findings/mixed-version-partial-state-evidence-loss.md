# Confirmed state effects disappear when composed with a legacy unknown receipt

Source eaf2286af6540cdd0b169fe8291d66a1f078b3ff; spec b613ca419eb51f187238ec5d9dbf4d0eaf93d4bc. Original obligation: preserve actual state changes, committed/unknown evidence, source/device/actor, and exact terminal evidence; preserve mixed-version behavior rather than optimizing evidence away.

**Reproduced (independent Release regression):** reviewer-owned worktree review-explicit-control-r4-eaf2286 at exact head, only test fixture appended to Server.Tests/ServiceTests.fs; no product edits. Job a2de04cc64db450ca7cb98ca9afe65bf exit 1, test reviewer4_mixed_version_unknown_retains_confirmed_source_subset, expected Unknown [{ProjectName="Source"; AgentId="Worker 1"}], actual Unknown []. Existing clean 92/92 Server tests job 130011bbf53542d1846bc7de89a3bcc1 exit 0.

Reproduce through actual public merge helper:
```fsharp
let known : AgentStateChange = { ProjectName = "Source"; AgentId = "Worker 1" }
let receipt : AgentInboxActivation =
    { ProjectName = "Source"; AgentId = "Worker 1"; ActivatedByThisCall = true
      BeforeState = Some "Ready"; AfterState = Some "Working"
      CommittedHead = Some(String('a', 40)); OperationId = Some "source-activation" }
let source : AgentCommandResponse =
    { Result = Some "{}"; Error = None; Instructions = []
      AgentStateChanges = [known]; InboxActivation = Some receipt; PendingJob = None }
let legacy = JsonSerializer.Deserialize<AgentCommandResponse>("""{"Result":"destination-result","Error":null,"Instructions":[],"InboxActivation":null,"PendingJob":null}""")
Assert.That(legacy.StateChangeEvidence, Is.EqualTo(StateChangeEvidence.Unknown []))
let merged = McpToolHelpers.mergeStateEffects source legacy
Assert.That(merged.InboxActivation, Is.EqualTo(Some receipt))
Assert.That(merged.StateChangeEvidence, Is.EqualTo(StateChangeEvidence.Unknown [known]))
```

Server/McpTools.fs:551–577 computes the correct completeness (unknown), but drops the independently confirmed subset via the final null branch when the other reply has no legacy receipt and no UncertainOutcome error. Common/Messages/AgentCommand.fs:927–935, 983–986 couples Unknown nonempty to the command error, and would reject an explicitly unknown nonempty receipt when the requested operation succeeded. Client/SignalR/Client.fs:1609–1626 has analogous composition at the original destination job; source-only inspection of that sibling, not a second independently executed repro.

Impact: mixed-version routing reports incomplete rather than falsely complete evidence (good), and the source inbox activation is still separately retained; source invalidation was already performed before destination forwarding. However the known affected-agent list disappears, contrary to the actual-state-evidence obligation. A caller using the typed known subset gets Unknown [] instead of the independently confirmed source effect, including on otherwise successful destination replies. Do not claim lost canonical activation, invalidation failure, or actual mutation loss; this is a receipt completeness defect, not a deployment incident. The bounded mixed-version path is specifically in scope.

Candidate remedy for independent adjudication: keep completeness independent from command success/error and preserve union of independently known effects when either side is incomplete; legacy inference stays conservative and explicit compatibility adapter should avoid false confirmation in old readers. Cover both server merge and client terminal composition, confirmed+unknown / unknown+confirmed, source receipt preservation, real wire round trips and pending-job behavior. Exact representation/compatibility choice is for independent adjudication, not prescribed new work or new authority. No source author edits, integration, live effects or scheduler changes.