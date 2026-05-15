# Development Process

## Purpose

Use this process to stay organized during software work without adding unnecessary ceremony.

The goal is simple: understand the code, make focused changes, verify them, and leave the project in a clear state.

Use the full process for substantial work. Use the lightweight workflow for small fixes.

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
