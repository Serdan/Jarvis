# Independent F0 codec correction review — Accepted (F0 only)

**Review:** `6e19b3c52d764fc3aee09af243814b63`  
**Frozen source:** `2a6e6c54d330bc2352ddb070ed52219d8969bf66`  
**Prior review:** `9d4b874bd2cd4e12ba868b8fc1612cbe` (`ChangesRequested` at `ecabbf023a6b750c2c15c92711048712de40f590`)

## Independent verification

Read the exact correction diff and updated `Server/WorkSnapshotCodec.fs` plus `Server.Tests/WorkSnapshotCodecTests.fs`. Reran `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore` on the exact frozen worktree using .NET 11 Release: **330 passed, 0 failed, 0 skipped**.

The corrections now:
1. Require kind-specific fields and known statuses; require a claim identity for `Claimed` stage and availability ordering for `Available`; reject rehashed incomplete canonical files.
2. Preserve authoritative committed sequence independently of the maximum surviving record revision, including empty post-deletion snapshots; keep future-record and torn-sequence checks.
3. Validate Git full-ref syntax, including rejecting invalid colon, backslash, `..`, `.lock`, duplicate separators and control characters on both export and import.
4. Require the scheduled verifier to bind an existing same-project reservation; reject unknown credential fields and manual-grant records. Crucially, comments now accurately state generic `Map<string,string>` fields are **not** a plaintext-secret sanitizer.

## Scope and boundary

**Accept the pure, default-off F0 serialization/validation codec and these corrections only.** This is not an acceptance of the complete backup/restore feature or the generic string model as a secrecy boundary. F1 must independently verify a trusted typed SQLite-to-snapshot projection and prove no plaintext scheduled bearer or ephemeral manual grant reaches any otherwise allowed string field; map authoritative record identities, preserve a transaction-consistent sequence, implement backup publishing/restore, and review the actual producer and recovery behavior. Do not integrate into a live host, deploy, restore production state or approve cutover on this F0 review.
