# Independent review — composition MCP operator surface

**Frozen source:** `a939ff7d5a5cdba1c219ebde0d1b1ab2488619e5` (base `d1b528bb47a018b1904adf7659454bda325a43c1`).

**Verdict:** RequestChanges. One actionable P2 finding: `findings/overview-page-response-cap.md`. The new overview API counts units but does not bound JSON payload bytes; the public client has a 900 KiB response limit and can fail to return any page for otherwise legal specs/history. Recommend a bounded view DTO and a transport-level regression test.

**Positive checks:** Reviewed the exact-commit diff for operator read, versioned JSON mutation parser, acceptance adapter exposure, MCP OAuth/command metadata, snapshot/cursor handling, and tests. The code uses the existing verified Git ledger and avoids caller-supplied acceptance evidence. The scope intentionally excludes graph-to-WQ scheduler activation; do not treat that as a defect in this slice.

**Tests performed by Reviewer 1:** `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~WorkComposition`: 6/6 pass; `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore`: 294/294 pass. A separate full `Client.Tests` release run timed out at 180 seconds; logs included activity-persistence permission warnings, so no full-client-suite pass is claimed.

**Previous submitter summary:** HEAD pushed; both release builds succeeded; their focused Client composition and Server composition tests passed. This is the operator-access slice, not scheduler activation.