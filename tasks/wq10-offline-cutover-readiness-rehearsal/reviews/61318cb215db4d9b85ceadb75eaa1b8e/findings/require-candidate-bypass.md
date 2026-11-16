# P1 — Input-controlled provenance bypass produces a false green prepared case

**Frozen source:** `3bf142f22fc7a54b5e4c0b69e42f3b10e29f5376`.

**Location:** `tools/Wq10MigrationPreview/rehearsal.py:105-111` (`case.get("requireCandidate", True)`) and `:154` (prepared verdict).

The caller can set `"requireCandidate": false` in rehearsal.json. Then WQ10 skips the only positive-candidate source-identity check altogether. A concrete local adversarial run used the shipped two-project fixture with pending Task/Role messages and one legacy task in **each** project, set both captured Scheduler `wouldLaunch` values to `null` and set `requireCandidate=false`. It produced:

```json
{
  "rehearsalVerdict": "PREPARED_FOR_REVIEW",
  "blockers": [],
  "projectWork": [
    ["Alpha", null, ["T-alpha"], ["Role-Alpha", "Task-Alpha"]],
    ["Beta", null, ["T-beta"], ["Role-Beta", "Task-Beta"]]
  ]
}
```

This is an operator-supplied input, not a trusted source observation; it can turn absent Scheduler selection and missing source provenance into a green cutover-rehearsal gate even while unresolved legacy work is present. This contradicts the task's fail-closed source/original-ID evidence boundary.

**Required fix:** Do not let a manifest flag disable source-provenance checks for an otherwise green case. Where absence of a candidate is legitimately expected, make it an explicit non-green/audit-only outcome or require independent evidence that no eligible work needs handoff. Check selected `workId`/source lineage against the inspected project work inventory and versioned origin before reporting a prepared case. Add a regression test for the exact two-project no-candidate scenario.
