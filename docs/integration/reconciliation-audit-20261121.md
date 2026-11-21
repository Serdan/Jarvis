# Integration reconciliation: Pending submissions, uncertain effects, shared reviews
**Date:** 2026-11-21 (UTC)
**Task:** integration-pending-effects-authority-audit-20261121
**Operator-directed Worker:** Worker 8; attempt f723fb23-ec74-4d44-b122-8230a5aff4a9; generation 1
**Scope:** Evidence audit only. No integration submission/completion, Git merge, remote push, effect retry/acknowledgment/cleanup, scheduler activation, deployment or runtime change.

## 1. Source-of-truth boundary and decision

Observed remote project main: **d702352ef705f9991ac1cb14dc15a13cce78aa44** (git ls-remote origin refs/heads/main, exit 0); clean local main. Canonical orphan ledger was independently inspected by immutable Git tree at **58068d2e7793d677cb1ade19b68130394f75499f**; Jarvis integration reads at **917678519121b5a6b62bed6177ed47e7066ee54e**, additional task reads at **7483a2106548d50abe1c8cf242f9db12284ec001 / f3810dda91ca8b7bf845fe3dec46df3a96ac7625**. The ledger advances concurrently; these are observed snapshots, not a lock or claim that future states are unchanged. Refer to Integrator checkpoints **9656**, **9578**, and **9579**; their original uncertainty and publication holds remain intact.

**At those verified snapshots: exactly 11 Pending integration submissions across 10 tasks; 35 locally pending effect intents, all NotFoundAtVerifiedHead; three independently Accepted exact sources in shared _reviews.** No Pending record has a positive complete-integration receipt. Two modern Pending records are independently Accepted with v1 immutable pinned SHA; five legacy Pending records have Accepted reviews but sourceVersion=0 / no canonical ReviewedHeadOid; four have ChangesRequested reviews. NotFoundAtVerifiedHead is not negative-effect proof and grants no retry, discard, archival, or completion permission.

## 2. Exhaustive canonical Pending submissions

All rows below are **Pending** at the observed ledger snapshots, have targetRef **refs/heads/main**, integratedCommit=null and completedAtUtc=null. The source hash is the original submittedCommit, not a mutable branch tip. Each review was independently read via review_details and the exact managed review ref was confirmed by git ls-remote. 'In main?' uses git --no-replace-objects merge-base --is-ancestor SHA d702352e...; this **only** establishes Git ancestry, never a canonical completion receipt.

| Created seq | Task ID | Submission ID | Submitted commit (full SHA) | Review ID / current verdict | In main? |
|---|---|---|---|---|---|
| 7121 | p0-cross-project-launch-actor-isolation | c2e8f9b62e8e4e618bd2c87e352b4095 | 99686a6737a147cee0a76aa345ad374109a4f84f | 19b3f20789c94dc4a319016fc8359892 / Accepted legacy | No |
| 8506 | p1-chatgpt-agent-tab-reclamation-20261119 | f50d118ba86349f89a3a12086ef44c56 | 36a2d776503d11109663318844fa4b08c06e1918 | a92c1fc7ccbd44f7be750e76ff752c9a / Accepted pinned | No |
| 6535 | task-2f00cd4c007b4b58b9a10591bac3eb0f | b40168a2fa344ee6a9d211e7c74377e2 | 01ad0fab22a2d0283ca4792d2f59b42e2ba8c9e4 | 01ad0fab22a2d0283ca4792d2f59b42e / Accepted legacy | No |
| 6687 | task-6c6d71c8d3ef408eb30886ded9522af3 | 974e07a3b53c4b139a3b7f7be9d6dd30 | 02be332fd1a513a9d6e22a2d5ef9b271576c5ca1 | 68478fd9c709437f9fe4d5a58a5ee23c / Accepted legacy | No |
| 6685 | task-ae4409179c81478684759a79b344e83f | 71e8a3356f2040399e4bf1a50e672c84 | 61000a04698aa33922d3f8386ae22af097fd3817 | 3f8a6d91e4b74208a53725c1bd82f09c / Accepted legacy | No |
| 7043 | task-d2c2fe925f824d92b2f21ef10dc69337 | ed136921a4024773843f416f58958b9e | 2e4f470177fc76d45d698f11cf6d67566e87f40d | 6af7d25b331f4e1a9df39ee4426e75c1 / Accepted legacy | No |
| 8509 | work-c-original-git-reader-reinspection-negatives-20261118 | e7a9c2f47cd14b8e9d46e62984569b11 | 7a4ca6945bc25946c49e07064b674d3ec3783b08 | 3f0d21d67ed14c9fa8d49f429b42d5ef / Accepted pinned | No |
| 5025 | work-first-ui-verified-git-loader | a40b71c474974a9495d6d0145f0cc30e | f25f4ad28e559483af275bce09387238f4276c9f | c3f4910de8a24c69b6a932d0cc697e27 / ChangesRequested | Yes |
| 5066 | work-first-ui-verified-git-loader | 6a08e9736f6c40c381ec55728e89ee42 | aeab8b208deafda979339249ead473ba8c7f7809 | d0b4c33f6fa04ab8a8a1a0bb61b58991 / ChangesRequested | Yes |
| 4714 | work-reactive-scheduler-wake | 9de7ca8237b84e45b1f3220dd1c4749e | b3e3d9c2f5f5fef1b6697cfca872c26177d1d6d1 | b3e3d9c2f5f54ef1b6697cfca872c261 / ChangesRequested | Yes |
| 4401 | wq11-fake-provider-first-connect-smoke | df36a51eb9fb4e7f948e2bfa02eb04f5 | b8a886a3de2298b392693f6054ee256d57015bf2 | 7ac3d8defc3a4cf8b38adc3215908cf0 / ChangesRequested | No |

**Source lease / remote ref evidence (in same row order):**
1. c2e8f9: submission sourceRef refs/heads/fix/cross-project-launch-actor-worker5; current attempt also binds that source, but ordinary remote ref is ABSENT. The exact Accepted review ref exists and resolves to 99686a67; review sourceVersion=0, ReviewedHeadOid absent.
2. f50d118: refs/heads/fix/agent-tab-reclaim-worker22-20261119; current attempt binds it, but ordinary remote ref is ABSENT. Immutable review ref and integration ref both resolve to 36a2d776; sourceVersion=1 and ReviewedHeadOid exact. TargetHeadAtSubmission=a83549a6897a4e26aa4834ff81dc38b6e16ee85e.
3. b40168: refs/heads/feat/register-agent-role-task-worker11; remote branch/review ref both resolve to 01ad0fab; current task attempt has no source binding, and legacy review sourceVersion=0.
4. 974e07: refs/heads/feat/global-agent-roster-worker7; current attempt matches, branch/review ref resolve to 02be332f; legacy review sourceVersion=0.
5. 71e8a3: refs/heads/feature/review-dependent-blocking; current attempt matches, branch/review ref resolve to 61000a04; legacy review sourceVersion=0.
6. ed1369: refs/heads/feat/account-global-agents-worker9-20261117; current attempt matches, branch/review ref resolve to 2e4f4701; legacy review sourceVersion=0.
7. e7a9c2: refs/heads/fix/work-c-reader-currentmain-worker4-20261119; current attempt matches sourceRef but its later current BaseCommit differs from the frozen review's base. Branch, immutable review ref and integration ref resolve to 7a4ca694; sourceVersion=1 and ReviewedHeadOid exact; TargetHeadAtSubmission=a83549a6897a4e26aa4834ff81dc38b6e16ee85e.
8. a40b71 / 6a08e9: both cite refs/heads/feat/work-first-verified-loader-worker1-20261116, now advanced to cf6d43dd219db259ed5e243eecfc1c6029ceef28, not either old submitted SHA. Task is Closed; immutable review refs still resolve to respective old submitted SHAs; review verdicts ChangesRequested. A separate later record cb925f331f9e4c9f9bc2c1eb78c87ac3 is Integrated and **does not retroactively complete** either older Pending record.
9. 9de7ca: refs/heads/feat/work-reactive-wake-worker2-20261116, now advanced to 7fdf233bec2c1f0b3db1d998d20d2f2f7dc353bc, not the Pending SHA. Task Closed; review ref preserves the old SHA; ChangesRequested. A separate later d4d6666420e54c52b69c9e11c45e8397 record is Integrated, not proof that 9de7ca was completed.
10. df36a5: refs/heads/test/wq11-fake-provider-first-connect-worker7, remote branch and review ref match b8a886a3; task Closed, ChangesRequested, no current source lease.

All five older Accepted review records above have SourceVersion=0, ReviewedHeadOid=null, integrationRef=null and targetHeadAtSubmission=null. Their historical Accepted verdict is real but insufficient for the **new** pinned-review SubmitIntegration contract; do not overwrite old reviews or forge provenance. The four ChangesRequested records are ineligible. Even where a source commit already appears in main history, do not manufacture CompleteIntegration evidence, rewrite a pending record, or delete it.

### File-scope overlap with Accepted sources

Read-only git --no-replace-objects diff --name-only between each review's recorded BaseCommit and exact submitted/reviewed SHA established **path intersections only**, not textual conflict or safe composition. Against the three independently Accepted shared sources:
- c2e8f9 intersects BrowserSchedules 7883e0fe on Client.Tests/BrowserSchedulesTests.fs and Server/McpTools.fs.
- b40168 intersects BrowserSchedules on Server/McpTools.fs.
- 974e07 intersects BrowserSchedules on Client/Program.fs.
- 71e8a3 intersects BrowserSchedules on Common/Messages/AgentCommand.fs and Server/McpTools.fs, and Challenges 50bf6da on Client.Tests/ChallengeWorkflowTests.fs.
- ed1369 intersects BrowserSchedules on Server/McpTools.fs.
- f50d118, e7a9c2, a40b71, 6a08e9, 9de7ca and df36a5 have no overlapping changed *paths* with the three shared sources by that base-to-source comparison.
- None of the 11 Pending changed-path sets overlaps the Scheduler Phase-1 planner's three changed paths. Work-C e7a9c2 shares Server.Tests/Server.Tests.fsproj with older 974e07 and ed1369, another historical path collision.
- BrowserBridge fixture corrections and protocol changes are **separately owned**; path non-overlap never proves integration admissibility or semantic compatibility.

## 3. Local effect-intent reconciliation: all 35 preserved

Jarvis reconcile_pending_operations inspected each local operationId against **one verified Git ledger head** (58068d2e7793d677cb1ade19b68130394f75499f). pending_operations_page independently listed all 35 without pagination (NextCursor=null), matched the reconciliation set and action counts; later targeted inspections of four SubmitIntegration intents at verified heads f3810dda... / 1bdf3825... remained NotFoundAtVerifiedHead. **No local Pending operation has an Applied receipt.** The 2 archived Abandoned records are separate from the 35 and not a license to discard anything here.

Every operation ID below retains the status **NotFoundAtVerifiedHead / effect uncertain**. Their original command SHA256 is retained by the local journal; do not infer a different command or submit a new UUID. The following grouping is exhaustive:

| Original action (count) | Exact operation IDs |
|---|---|
| Claim (8) | 206fbc2f-d1e4-43ca-a817-58494d0b80bc; 7e4c8868-60fd-4c7a-a263-17e9c21b2d96; 8e8c5132-a5e1-4e6d-b46c-b8d7de424a16; 92f7ade9-2832-4666-a691-9a22484f6cbc; c3306b35-8750-46c5-9b7c-454c567ee706; cdc8c14f-684a-4577-a1e0-1ed2edf66bcc; d5089e53-ab53-44f7-b784-ad255fc96ff5; ee515f1f-399a-4eee-995c-b1c571538ed6 |
| PutDocument (8) | 1d8d9679-645b-4f70-b85c-fac6a4ffc818; 5208bef5-2652-42c6-bc0a-a9acccbf17dc; 6315a3dd-f0df-4a2d-9f8a-01e7573ce479; 6d307bba-6f85-4b38-8511-0fd083872f53; 779325bf-b6bd-411d-9aa9-71cf1bda20f2; 85126f25-4d65-4503-bdb9-85d35521d9b5; 88834514-9135-4425-911d-0633dd30a4b2; b714018d-4e02-48ab-b782-f6bc2a3891d1 |
| NotifyAgent (5) | 0bacf795-bad0-4216-b11b-55459e4ccc9c; 66d2c72d-573f-4d3a-8696-9fb087dc4368; 809b7181-bac2-4101-b76a-37110d124524; 81603c39-e92e-4278-bb92-537c58b92f94; c874fc88-3757-4ace-a866-783faeacfb76 |
| Submit (4) | da5088e3-2335-4aab-bb21-0b67e517b1dd; ec4dd21e-daee-4188-a2e8-d9c59bcc82a1; f4c2d1d9-f8ac-4eae-b6d2-28c32358ab41; f538a567-f11c-4404-a105-fe95495cc799 |
| SubmitIntegration (4) | a1c829d8-a538-41bf-9256-4e78b55a0960; ae68fe49-d6dc-45d6-819e-f78124a1aed8; bb0c96d2-3284-4b7a-83b3-548ad4678cc8; caebf064-33d6-4212-879c-18f5cc9bbe1d |
| YieldAgent (2) | 0e835b42-d94f-45d1-9f06-b1bad7702c31; 2aecade2-8224-4de3-aa0f-faeb07b16750 |
| CompleteAgentWork (1) | 383aa4ca-8b40-4070-92fa-47d456c1e19b |
| Decide (1) | 42763757-79df-4674-8f2e-66544b35918f |
| CreateTask (1) | ba219a1b-9bfb-46eb-9364-3e13c460b0c4 |
| ReviseTaskSpecPinned (1) | e15e2612-26ae-4e3c-ace5-82cab35ee0a0 |

**Separate old local SubmitIntegration intents:** targeted read-only original-command inspections (not retries) identify a1c829d8 and ae68fe49 as attempts against task simplify-core-task-tools; bb0c96d2 against work-agentfree-emptyserver-restart-fixture; caebf064 against tool-catalog-prune-names. None corresponds to one of the ten canonical Pending task IDs in §2. That mismatch and NotFoundAtVerifiedHead **do not prove absence of eventual or external effects**. Do not conflate the 4 local uncertain intents with the 11 verified historical submissions.

**Permitted now:** retain exact operationId, stored command hash/ledger binding, verified head, source path and immutable review IDs; repeat only *read-only* GetOperationStatus, InspectPendingOperation and reconciliation against subsequently verified remote heads; compare actual Applied receipts for matching (projectId, ledgerId, epoch, operationId, original request hash) before proposing any later acknowledgment. **Permitted acknowledgments now: zero.** No automatic retry, same/new UUID replay, archive, supersession, cleanup or action completion by inference. Where an operation remains NotFoundAtVerifiedHead, treat it as protected unknown and require an explicit authority decision and transport-resolution evidence before doing anything further.

## 4. Shared _reviews: exact Accepted source authority

The following reviews are independently Accepted and v1-pinned, but have Review.TaskId=_reviews, **not** the ID of any real task. All managed immutable review refs exist on the authoritative remote and match ReviewedHeadOid exactly. All three reviewed SHAs are outside remote main ancestry.

| Existing real work / source owner | Shared review ID / decision | Immutable reviewed SHA | Managed remote review ref | Review base |
|---|---|---|---|---|
| p1-organon-project-owned-browser-schedules-20261119 / Worker 18 | 7883e0fe26c0ede07782c4c686ed1a2d / Accepted seq9503 | 7883e0fe26c0ede07782c4c686ed1a2df836fade | refs/heads/jarvis-review/_reviews/7883e0fe26c0ede07782c4c686ed1a2d/r1 | a83549a6897a4e26aa4834ff81dc38b6e16ee85e |
| scheduler-core-planner-20261120 / Worker 5 | c2d0f7a1e8b44a9f93d51b0a6f72e4c8 / Accepted seq9468 | 34facab5db97b2f238d2415c8f7ae6b59866b754 | refs/heads/jarvis-review/_reviews/c2d0f7a1e8b44a9f93d51b0a6f72e4c8/r1 | 95a471fc565b57fe309c65fc85f371693dbe9067 |
| challenge-accepted-tests-main-rebase-20261120 / Worker 1 | fca0b69e4f1d4a439c951c9a4b2e7d61 / Accepted seq9548 | 50bf6da84abcd03cc65b631f8849d873715cfcbb | refs/heads/jarvis-review/_reviews/fca0b69e4f1d4a439c951c9a4b2e7d61/r1 | d702352ef705f9991ac1cb14dc15a13cce78aa44 |

**Why current SubmitIntegration cannot accept them:** installed Client/Modules/ReviewWorkflow.fs lines 1235-1319 requires the canonical task entity at tasks/<taskId>/task.json, selects branchReviewPath(taskId, reviewId), then checks review.TaskId = taskId and a managed review ref for that namespace. get_task("_reviews") returned Missing ledger entity: tasks/_reviews/task.json, independently corroborated by Git ls-tree at verified ledger head. Submitting under any unrelated real task fails identity checks and would be an authority bypass; fabricating tasks/_reviews is prohibited. Current P0 Worker 5 protocol review e7f4290f5d6c4e1c9dc23316af1692d8 was **ChangesRequested at canonical decision seq9659** for frozen 15eaecd90117529ef0a43c441c161f8e23bdde9e; no unaccepted implementation path is authority.

**Smallest recommended extension, after P0 protocol is independently Accepted and safely integrated:** extend the existing canonical submission authority with a typed **review locator** (TaskBound(taskId, reviewId) unchanged; SharedAccepted("_reviews", reviewId) explicit). For shared review, require independently verified Accepted v1+ review, exact immutable managed review ref and ReviewedHeadOid, original TaskRef/BaseCommit/TargetRef, reviewer decision and authenticated source identity; link to a **real existing task** only where its original attempt/source lease and owner are proven at immutable ledger history. For BrowserSchedules, current p1-organon task attempt has no SourceRef binding, so matching owner/name alone is insufficient; require an **explicit immutable authorized source-to-real-task binding receipt** or fail closed. Persist the original review namespace + review ID/ref/SHA and binding receipt on the *same existing IntegrationSubmission record path for that real task*. CompleteIntegration must dereference that exact locator and enforce the normal review, source, mechanical composition, non-force publisher and operation-CAS checks. Never rewrite Review.TaskId, mint a synthetic task, mutate old Accepted review, copy it as a fake new Accepted review, or substitute a moving source branch. If no valid real task binding can be established, pause for Lead's explicit shared-review authority design; do not silently weaken the task-bound path.

The independent alternative is a dedicated typed shared-review integration ledger key, but it adds read/complete/routing surface and should be used only if authenticated real-task binding proves impossible. Do not create a parallel database/platform.

## 5. Ordered, conditional Integrator handoff

1. **Freeze uncertainty, not effects.** Re-read LedgerHealth, exact current remote main, all Pending canonical integration paths, 35 local operation IDs (plus newly arrived intents), and immutable review refs at one or clearly labeled sequential verified heads. No replay, cleanup or inferred completion. Separate local historical intent IDs from canonical integration submission IDs.
2. **P0 protocol first.** Await an independently Accepted exact Worker 5 source and verified safe incorporation of its corrected two-parent mechanical composition / cross-component WorkComposition acceptance behavior. Existing old APIs do not inherit fixes merely because a local branch contains them. Preserve any ChangesRequested review as non-authority.
3. **Publisher boundary before any integration effect.** Establish authenticated non-force fast-forward / expected-target CAS behavior including stale T1/T2 race rejection, exact remote receipt and after-push target ancestry. Current ledger Health says HostedOrUnknown; NonFastForwardRejection=Unknown; historical GitHub rulesets [] and branch-protection HTTP403 are NOT a positive host guarantee. Without positive verification, HOLD and escalate to Lead.
4. **Resolve shared review locator.** Obtain explicit approved review-namespace/real-task binding as in §4, with regression tests against cross-task substitution, stale mutable branch, unpinned review and fake _reviews task; preserve original three Accepted snapshots without mass re-review.
5. **Reconcile canonical 11 individually.** Re-read all exact submission/review/remote refs, current target ancestry and competing newer Integrated submissions. Two modern pinned Accepted f50d118 and e7a9c2 are conditional candidates for safe new composition, **not automatic completion**. Five legacy Accepted/SourceVersion0 require new immutable source authority or separately valid review proof; four ChangesRequested remain ineligible, including the three already present in main. Preserve history and canonical status absent verified completion receipts.
6. **Reconcile 35 separately.** Only matching positive Applied operation receipts could authorize later targeted acknowledgment under an explicitly authorized recovery action. NotFoundAtVerifiedHead retains uncertainty. The old 4 local SubmitIntegration intents are not the 11 canonical Pending records. No speculative replay or blanket cleanup.
7. **Refresh exact source/main and overlap.** Prioritize independently Accepted protocol and normal modern Pending Work-C / Worker22 only when clean current ancestry, source provenance and conflict-free mechanical merge exist. Separately consider shared Challenges (tests only), Phase1 planner (default off), BrowserSchedules, Worker11 and Health based on fresh Accepted reviews; do not assume missing original branches imply missing reviewed source. BrowserSchedules original Accepted 7883... carries a separate full Client.Tests compile issue (FS0764 SourceProjectName fixture); correction belongs to its independently leased/reviewed work, not Integrator. Any substantive conflict requires focused independent review; no unreviewed edits or overlaid code from Worker 1/5/6/11.
8. **Test and publish only after explicit authorization and all gates.** Run fresh merged-result .NET11 Release and relevant focused negative/Node tests; earlier local-only aed77776 evidence (Client 1279 passed/1 skipped, Server 575 passed) is not current composed-result proof. Use only a verified non-force expected-target publisher with post-remote receipt before CompleteIntegration; never activate deployment, scheduler, Manager, Health, browser schedules or Work cutover from this audit.

## 6. Audit reproduction and boundaries

Read sources: agent_inbox Worker 8 (role Worker v4, root/project AGENTS), pinned task spec blob 01b26a728d48ec02621cc45491c6f661e1ca896a, Integrator checkpoints 9578/9579/9656, ledger_health, get_integration_submissions for all ten task IDs, review_details for eleven task-bound reviews and three _reviews accepted snapshots, task_overview for real task candidates, get_task for original leases and negative _reviews, pending_operation_summary/page/reconcile for all 35, targeted inspect_pending_operation on all four local SubmitIntegration IDs. Read-only Git checks: ls-remote origin/main, 11 task review refs, three shared review refs, ordinary source refs and two managed integration refs; git grep -l Pending <verified ledger snapshot> -- :(glob)tasks/*/integrations/*.json returned exactly 11; no-replace-object SHA ancestry and exact base-to-source path diffs. Git ls-tree verified no tasks/_reviews/task.json. ReviewWorkflow lines 1235-1319 independently read. No code changes, tests, integration API calls, main ref updates, Git merges/pushes, journal mutations, schedules, agent launches or deployments performed by this audit.

**This document is the sole intended repository change on the separate docs source lease.** Its evidence is a read-only point-in-time audit, not a promise that external concurrent work or later ledger writes have stopped.

**Last verification:** ledger_health at trusted head **1bdf3825e9fa4e87946b819c28c314a2791bba03**, sequence **9702**; read-only git grep at that exact head still returned the same 11 Pending integration paths, and pending_operation_summary at the same head still reported 35 NotFoundAtVerifiedHead (2 separately archived Abandoned). The document was mechanically checked against pending_operations_page: **all 35 exact operation IDs and all 11 exact submission IDs are present**.
