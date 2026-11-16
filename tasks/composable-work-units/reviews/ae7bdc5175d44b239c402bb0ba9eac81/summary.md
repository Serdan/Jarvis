# Independent review — composition MCP operator surface

**Frozen source:** `a939ff7d5a5cdba1c219ebde0d1b1ab2488619e5` (base `d1b528bb47a018b1904adf7659454bda325a43c1`).

**Verdict:** RequestChanges. One P2 finding: `findings/overview-page-response-cap.md`. The read API bounds the *number* of composition units, but each unit can enumerate the same long WQ stage IDs from its linked canonical task; repeated stage IDs across units may push the serialized response over the 900 KiB outer transport cap. Add a byte-bounded result/projection and a transport-level amplification regression test.

**Correction:** The first version of this finding mistakenly suggested using giant stored unit specs or acceptance histories as a reproduction. The Git ledger actually imposes 64 KiB per graph/WQ blob. That reproduction was withdrawn and the finding document revised to isolate derived cross-product response amplification instead.

**Positive checks:** Reviewed frozen diff for operator read, versioned JSON mutation parser, acceptance adapter exposure, MCP OAuth metadata, cursor handling, and tests. Implementation uses the existing verified Git ledger and prevents caller-supplied acceptance claims. The graph-to-WQ scheduler bridge is deliberately outside this slice.

**Reviewer tests:** `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~WorkComposition`: 6/6 pass; `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore`: 294/294 pass. Full `Client.Tests` release run timed out at 180 seconds, with activity-persistence permission warnings; no full-client-suite pass claimed.