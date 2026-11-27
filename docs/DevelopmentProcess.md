# Development Process

## Purpose

Use this process to stay organized during software work without adding unnecessary ceremony.

The goal is simple: understand the code, make focused changes, verify them, and leave the project in a clear state.

Use the full process for substantial work. Use the lightweight workflow for small fixes.

For new or changed tool surfaces, [pertinence by default](AgentCommandSurface.md#pertinence-by-default-operator-decision-2026-11-27)
is an architectural acceptance criterion, not optional optimization. Verify the
actual registered route's bounded default output at realistic scale, completeness
within documented scope, deterministic ordering, exact expansion/pagination,
authorization and state/effect evidence preservation. Measure response bytes and
decision usefulness before introducing a broader abstraction.

## Context efficiency as a quality attribute

Optimize total inference burden per successfully completed work item, not the
size of an isolated response or the number of calls alone. A successful item
meets its acceptance criteria, including required verification, independent
review and lifecycle evidence. Account for all contributing attempts, roles,
continuations, retries, recovery, failed speculation and review rework; report
incomplete/failed items separately and include their consumed resources in the
workload total. Never improve the denominator by declaring incomplete work done.

For changes affecting prompts, tools, orchestration, context delivery or recovery,
record an observed baseline and comparable before/after evidence for the affected
flows. Use a representative suite covering bootstrap, continuation, reviewer,
orchestration and recovery, including adverse paths and realistic project scale;
justify any unaffected or unmeasurable cases. Hold task scope, acceptance
criteria, provider/model, configuration and workload comparable, distinguish
cold and warm caches, and record versions, sample size and measurement boundary.
Prefer authorized offline fixtures/replays where adequate; live measurement
requires its own authorization.

Report actual provider input/output tokens and cached-input tokens where
available (including reasoning usage if separately reported). Otherwise label
UTF-8 bytes or locally estimated tokens explicitly as proxies, not provider
usage, cache hits or money saved. Record per-stage prompt and tool-catalog sizes,
tool result bytes, repeated immutable content, number of inference turns/tool
calls, retries, and end-to-end latency to successful work (including tail or
failure cases as appropriate). Report missing instrumentation as unknown;
do not infer cache hits from stable prefixes or quote speculative savings.
Use the existing context-accounting measurements as they become available;
do not build a parallel accounting mechanism.

Set justified, flow-specific budgets against the measured baseline and review
growth in context, calls and latency, including trade-offs between them.
A budget is not permission to truncate required evidence, weaken authority,
reduce review independence, skip validation or move cost into follow-up calls.
Accept growth only with a documented correctness/usefulness benefit and
proportionate cost; investigate regressions rather than applying arbitrary
global caps. Prefer stable cache-friendly prompt prefixes, explicitly versioned
dynamic suffixes, role-scoped tool catalogs and deterministic runtime decisions
over repeated model interpretation. See [pertinence by default](AgentCommandSurface.md#pertinence-by-default-operator-decision-2026-11-27)
for response-specific requirements.

Independent review of affected changes must evaluate the measurement evidence,
the budget/trade-off justification, and semantic preservation. For unaffected
changes a short scope justification suffices. Missing evidence is a limitation
to resolve or explicitly adjudicate, never a passing measurement. Context
efficiency complements rather than replaces correctness, safety, maintainability
and usability criteria.

---

## Lightweight Workflow

For most small or medium tasks:

1. Check git status.
2. Inspect the relevant files.
3. Make focused changes.
4. Run the most relevant test, build, or verification step.
5. Review the diff.
6. Summarize what changed, what passed, and what remains.

This is the default workflow.

Small tasks usually do not need extra tracking files, structure documents, or dedicated process notes.

---

## When to Use the Full Process

Use the full process when the task is large enough that context might be lost.

Good candidates include:

- Multi-file feature work
- Large refactors
- Unfamiliar repositories
- Risky or high-impact changes
- Work that may span multiple sessions
- Tasks with several dependent subtasks
- Investigations where discoveries need to be preserved

Do not use the full process automatically. Use it when it helps.

---

## Full Process

### 1. Create or Confirm a Working Branch

Use a dedicated branch for substantial work.

Example:

```bash
git checkout -b feature/your-task
```

If an appropriate branch already exists, continue there.

Before switching or creating branches, check git status so existing work is not accidentally lost or mixed up.

---

### 2. Track the Task When Needed

For larger tasks, create or update `ProcessTracker.md` at the project root.

Keep it short and useful. Include:

- **Goal**: What the task is trying to accomplish.
- **Status**: What is currently happening.
- **Notes**: Important findings, risks, or decisions.
- **Subtasks**: A concise checklist of actionable steps.
- **Verification**: Tests, builds, or manual checks to run.

Avoid turning the tracker into a diary. Record only information that will help continue, review, or verify the work later.

If the task is small, skip this file.

---

### 3. Map the Codebase When Useful

Create or update `Structure.md` only when a codebase map would genuinely help.

Use it for unfamiliar, large, or changing project structures.

Include:

- Main folders or modules
- Short descriptions of what they do
- Important entry points
- Test locations
- Generated or ignored directories when relevant

Do not update `Structure.md` for minor edits unless the project layout changed or the map would otherwise become misleading.

---

### 4. Break Large Work Into Subtasks

Split substantial work into small, concrete steps.

Good examples:

- Investigate the command dispatch path
- Add validation for project-relative paths
- Update tests for patch handling
- Run build and fix compile errors

Poor examples:

- Improve code
- Clean up
- Finish feature

If a subtask becomes too broad, split it again.

---

### 5. Work Incrementally

For each subtask:

1. Locate the relevant code.
2. Make a focused change.
3. Run the smallest useful verification.
4. Review the diff.
5. Update task notes only if the result affects future work.

Prefer small, understandable changes over large, hard-to-review rewrites.

---

### 6. Verify

Run the most relevant checks available.

Examples:

```bash
dotnet test
dotnet build
npm test
npm run build
cargo test
```

Record important results if using a task tracker.

If verification cannot be run, state why.

---

### 7. Commit Sensibly

Commit when the work reaches a coherent checkpoint.

A good commit should have:

- A clear message
- Related changes grouped together
- No accidental generated files
- No unrelated user changes

Check git status before committing.

If unrelated changes are present, call them out before creating the commit.

---

### 8. Finish Cleanly

At the end of the task:

- Review git status.
- Review the final diff when appropriate.
- Summarize what changed.
- Summarize what passed.
- Mention what remains.
- Update task or structure notes only if they still add value.

If the task is complete, the branch can be merged or prepared for review.

---

## Guiding Principle

Use the least process that preserves clarity.

The process should prevent lost context, accidental changes, and unverified work. It should not create paperwork for its own sake.
