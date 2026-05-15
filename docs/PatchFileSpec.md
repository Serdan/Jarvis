# PatchFile Improvement Spec

## Purpose

Improve `PatchFile` so agents can make focused, reviewable edits without falling back to ad-hoc text replacement scripts.

The command should remain safe by default, but it should provide better diagnostics, optional dry-run behavior, and enough structured output for an agent to recover when a patch does not apply.

---

## Current Problem

The current unified-diff patch implementation is intentionally strict. It applies hunks only when line numbers and context match exactly.

That is safe, but brittle:

- Minor line shifts cause patch failures.
- Error messages do not show enough context to regenerate a better patch.
- There is no dry-run mode in the command payload.
- The response does not expose changed line counts, hashes, or hunk diagnostics.
- Agents may fall back to less transparent edit mechanisms when patches fail.

The goal is not to make patching reckless. The goal is to make safe patching easier to diagnose, retry, and verify.

---

## Goals

1. Keep `PatchFile` safe and atomic.
2. Support dry-run validation before writing.
3. Improve diagnostics for failed hunks.
4. Return structured patch results.
5. Support optional fuzzy hunk matching for small line shifts.
6. Preserve expected-hash protection.
7. Make successful and failed patch attempts easy for an agent to explain.

---

## Non-Goals

- Do not support arbitrary shell execution as part of patching.
- Do not silently apply patches with poor confidence.
- Do not allow patching outside the project root.
- Do not replace git as the source of truth for final diffs.
- Do not require fuzzy matching by default.

---

## Proposed Protocol Changes

### `PatchFileCommand`

Extend the command with optional patch behavior fields.

```fsharp
type PatchFileCommand =
    { ProjectName: string
      FilePath: string
      ExpectedHash: string option
      Format: PatchFormat
      Patch: string
      DryRun: bool option
      FuzzyContextLines: int option
      ReturnContent: bool option }
```

Field behavior:

| Field | Default | Meaning |
|---|---:|---|
| `DryRun` | `false` | Validate and calculate the result without writing the file. |
| `FuzzyContextLines` | `0` | Number of nearby lines to search when the declared hunk line does not match. `0` means strict mode. |
| `ReturnContent` | `false` | Whether to include patched file content in the response. |

Recommended limits:

- `FuzzyContextLines` should be clamped to a small range, for example `0..50`.
- `ReturnContent` should be ignored or truncated for very large files if needed.

---

## Proposed Response Type

Add a structured result type instead of returning only `Content`.

```fsharp
type PatchHunkStatus =
    | AppliedStrict
    | AppliedWithOffset of offset: int
    | Failed

 type PatchHunkDiagnostic =
    { HunkIndex: int
      Status: PatchHunkStatus
      OriginalStartLine: int option
      AppliedStartLine: int option
      Message: string
      ExpectedContext: string list
      ActualContext: string list }

 type PatchFileResult =
    { Applied: bool
      DryRun: bool
      FilePath: string
      HunksApplied: int
      ChangedLines: int
      BeforeHash: string
      AfterHash: string option
      Content: string option
      Diagnostics: PatchHunkDiagnostic list }
```

Response behavior:

- On successful write: `Applied = true`, `DryRun = false`, `AfterHash = Some ...`.
- On successful dry run: `Applied = true`, `DryRun = true`, `AfterHash = Some ...`, file unchanged.
- On failed patch: return a validation error containing serialized/structured diagnostics if the existing error channel cannot carry a result.

Preferred long-term shape: allow command handlers to return `Result<PatchFileResult, AgentError>` so failed patch diagnostics can be structured rather than squeezed into a string.

---

## Patch Application Rules

### Strict Mode

When `FuzzyContextLines = 0` or omitted:

1. Parse the unified diff.
2. Verify target path is inside the project root.
3. Read the current file.
4. Verify `ExpectedHash` if provided.
5. Apply hunks at their declared line positions.
6. If all hunks apply, either write the result or return dry-run success.
7. If any hunk fails, do not write anything.

Strict mode should preserve current safety behavior.

### Fuzzy Mode

When `FuzzyContextLines > 0`:

1. Try strict hunk application first.
2. If strict placement fails, search nearby candidate positions within `FuzzyContextLines`.
3. A candidate position is valid only if all context and removal lines match exactly at that location.
4. If exactly one candidate matches, apply the hunk there.
5. If zero candidates match, fail with diagnostics.
6. If multiple candidates match, fail with an ambiguity diagnostic.
7. Never apply a hunk based only on additions.
8. Never apply a hunk when context/removal lines do not match exactly.

This gives resilience against line shifts without guessing.

---

## Diagnostics Requirements

Failed patch diagnostics should include:

- Hunk index.
- Target line from the patch header, if available.
- Failure reason.
- Expected context/removal lines.
- Actual file lines around the attempted location.
- Closest candidate information, if available.
- Whether the failure was mismatch, out-of-range, malformed patch, or ambiguous fuzzy match.

Example diagnostic text:

```text
Patch context mismatch in Client/SignalR/Client.fs

Hunk 1 expected near line 84:
    |> Result.map (Seq.map contentResultToDto >> Seq.toList)

Actual content near line 84:
    |> Result.map (Seq.zip cmd.FilePaths >> Seq.map ...)

No valid fuzzy match found within 20 lines.
Refresh the file contents and regenerate the patch.
```

---

## Hashing

Keep using SHA-256 hashes in the existing format:

```text
sha256:<lowercase-hex>
```

`PatchFileResult.BeforeHash` should always be present.

`AfterHash` should be present when a patch applies successfully, including dry runs.

If `ExpectedHash` is provided and does not match `BeforeHash`, fail before parsing or applying hunks.

---

## Atomicity

`PatchFile` must be atomic from the caller's perspective:

- If parsing fails, do not write.
- If expected hash fails, do not write.
- If any hunk fails, do not write.
- If writing fails, return an error.

Recommended implementation detail:

- Compute the full patched content in memory first.
- Only call `WriteAllText` once after all hunks have succeeded.

The current implementation already broadly follows this model and should keep doing so.

---

## Backward Compatibility

Existing callers that send the current `PatchFileCommand` should keep working.

Defaults:

```text
DryRun = false
FuzzyContextLines = 0
ReturnContent = true or false depending on compatibility needs
```

If response compatibility is required, there are two options:

1. Keep the existing endpoint response as patched content and add a new `PatchFileV2` command.
2. Change `PatchFile` to return `PatchFileResult` and update action schema/tool consumers at the same time.

Recommended path: change `PatchFile` directly while the protocol is still young, then regenerate schemas and GPT tool definitions.

---

## Tests

Add tests for:

### Existing behavior

- Applies a simple unified diff.
- Rejects mismatched expected hash.
- Rejects malformed hunk header.
- Rejects removal mismatch.
- Rejects context mismatch.
- Preserves line endings.

### Dry run

- Dry run returns success and after hash.
- Dry run does not write content.
- Dry run reports changed line count.

### Fuzzy matching

- Applies a hunk shifted down within the fuzzy window.
- Applies a hunk shifted up within the fuzzy window.
- Fails when the match is outside the fuzzy window.
- Fails when multiple candidate locations match.
- Fails when only addition lines are available as evidence.

### Diagnostics

- Failure diagnostics include hunk index.
- Failure diagnostics include expected context.
- Failure diagnostics include actual nearby context.
- Ambiguous fuzzy match diagnostic lists candidate line numbers.

### Response shape

- Successful patch returns before/after hashes.
- Failed patch does not write.
- `ReturnContent = false` omits content.
- `ReturnContent = true` includes content.

---

## Implementation Sketch

### Parser

Split parsing from applying.

Suggested internal types:

```fsharp
type PatchLine =
    | Context of string
    | Remove of string
    | Add of string
    | NoNewlineMarker

 type PatchHunk =
    { Index: int
      OldStart: int option
      OldLength: int option
      NewStart: int option
      NewLength: int option
      Lines: PatchLine list }

 type ParsedPatch =
    { Hunks: PatchHunk list }
```

### Apply flow

```fsharp
parse patch
verify expected hash
for each hunk:
    try strict placement
    if strict fails and fuzzy enabled:
        search nearby valid placements
    apply or fail with diagnostic
return full result
write only if not dry run
```

### Changed line count

A simple initial calculation is acceptable:

```text
changedLines = count(additions) + count(removals)
```

This is not a semantic diff metric, but it is useful enough for summaries.

---

## UX Guidance for Agents

Agents should prefer `PatchFile` over raw text-replacement commands for source edits.

Recommended agent flow:

1. Read the file.
2. Compute or record the current hash if available.
3. Generate a focused unified diff.
4. Use `PatchFile` with `ExpectedHash` when possible.
5. Use `DryRun = true` for risky patches.
6. If strict patching fails, inspect diagnostics and retry with refreshed context.
7. Use fuzzy matching only for small line shifts, not for broad rewrites.

---

## Open Questions

1. Should `PatchFile` default to returning content for compatibility, or should content be opt-in?
2. Should failed patch diagnostics be represented as an `AgentError` case instead of a validation string?
3. Should fuzzy matching be enabled by default with a tiny window, or always require opt-in?
4. Should patch parsing support file creation and deletion?
5. Should binary files be explicitly rejected before patch parsing?

---

## Recommended First Slice

Implement in this order:

1. Add `DryRun`, `FuzzyContextLines`, and `ReturnContent` fields.
2. Add `PatchFileResult` and diagnostics types.
3. Return structured results for strict patching only.
4. Improve mismatch diagnostics.
5. Add tests for response shape and dry run.
6. Add fuzzy matching in a second slice.

This keeps the first change reviewable while immediately improving safety and observability.
