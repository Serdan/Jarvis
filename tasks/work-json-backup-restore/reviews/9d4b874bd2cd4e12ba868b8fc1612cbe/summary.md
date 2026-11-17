# Reviewer 3 — independent frozen F0 review

**Verdict:** Changes requested.
**Task:** `work-json-backup-restore`.
**Review:** `9d4b874bd2cd4e12ba868b8fc1612cbe`.
**Frozen commit:** `ecabbf023a6b750c2c15c92711048712de40f590`, against base `a94241633c5a40111fccffe90a22e93c951ae1d0`.

## Evidence
- Read the 377-line `Server/WorkSnapshotCodec.fs` and 234-line `Server.Tests/WorkSnapshotCodecTests.fs` from the exact frozen commit, plus the task and 2026-11-17 Work architecture/alignment contract.
- Independent `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~snapshot_codec`: **10 passed / 0 failed**, .NET 11 Release. Worker-supplied review submission reports 327/327 Server tests and 2,000-record measurement; those larger results were not independently rerun here.
- Verified `git check-ref-format refs/heads/a:b` exits 1, whereas current F0 source-ref predicate accepts the string.
- No SQLite integration, Git backup publisher, restore-to-DB, production activation or source changes performed.

## Findings requiring action
1. **P1 — Required logical record invariants missing.** `findings/required-record-invariants.md`. WorkStage/EffectJournal can be accepted with arbitrary one-field payloads and unconstrained status, then regarded as restorable despite missing dispatch/effect-fence identity.
2. **P1 — Sequence equality rejects legitimate post-deletion snapshots.** `findings/committed-sequence-deletion.md`. The global authoritative sequence must not be inferred from the highest surviving row sequence unless durable retained markers explicitly guarantee that invariant.
3. **P2 — Secret-field boundary is unproven.** `findings/secret-field-boundary.md`. Arbitrary allowlisted string identifier values can contain a bearer; the field-name allowlist does not prove the stronger no-plaintext guarantee.
4. **P2 — Git ref provenance accepts invalid refs.** `findings/git-source-ref-validation.md`. Reject colon/control and other Git-invalid ref strings or use verified typed source references.

The codec has a good deterministic canonicalization/checksum foundation, and the scoped tests pass. These are validity and safety contract issues rather than a request to implement F1 before SQLite B acceptance. Re-submit a new exact frozen commit after correcting the F0 invariants; keep F1 blocked on accepted SQLite B and do not deploy or activate Work.
