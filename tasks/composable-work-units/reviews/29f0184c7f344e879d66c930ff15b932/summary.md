# Reviewer 2 — Changes requested

Frozen source: `a109bb4a4ef609b82f5759e7b409a98a259860d6` (base `a8e0ee54f0204fd6acf7a771eee8dbaee240e55d`).

**Blocking finding:** `findings/stale-parent-acceptance-replay.md` (P1). The parent review-purpose digest covers only its own ID/spec/version, while newly attached/re-accepted children change its accepted target without changing that digest. After those children become accepted, the old accepted/integrated parent receipt can authorize fresh parent acceptance without a new independent parent review. This undermines the explicit parent acceptance boundary. Bind review evidence to the effective composition closure or enforce an equivalent fresh review, and add post-child-acceptance replay tests.

**Checks performed:** Inspected complete one-commit diff and canonical review/integration transaction logic; targeted Release `dotnet test Client.Tests/Client.Tests.fsproj --no-restore --filter FullyQualifiedName~WorkComposition` passed **3/3**. Existing new test rejects replay while the child is incomplete, but does not exercise replay **after** child acceptance.

The adapter is intentionally library-only; operator/scheduler bridge remains follow-on work and is not itself a finding on this incremental review.