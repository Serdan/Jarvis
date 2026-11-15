# Independent Reviewer 2 finding — WQ-04 SCC / coverage diagnostics

**Review:** `68f0a1a421254a348d33f4580baff091`  
**Task:** `wq-parallel-scc-coverage-diagnostics`  
**Frozen reviewed commit:** `8ed9671543f05073f7d453728b50772138dd87f2`  
**Verdict:** **Accepted** for this exact frozen source. No blocking SCC/coverage defect found. **This is not permission to integrate or activate Scheduler.**

## 1. Immutable source, merge and ancestry
- Read `git show -s --format='%H%n%P%n%s' HEAD` in the clean registered `lead-wq04-scc-after-journal` worktree. HEAD is the exact frozen SHA above, a merge whose parents are **first** `53ab33a70446f9e2873ab5d5be4006a0c261322d` (WQ-03 source-effect journal atop WQ-07 candidate) and **second** `0544cef1ca4a0277517165e75ac13c2ed930ab03` (SCC refresh).
- Locally verified with exit-0 `git merge-base --is-ancestor`: original SCC `d24fda76360c74695dfec8b175165cb584aeef80`, SCC refresh `0544cef1...`, WQ03 journal stack `53ab33...`, WQ07 combined candidate `4f83130173d7758304fe7c0602897bdfc425dda6`, and stated base `e26a572b25f9ba829f1014f2b5ca36fd77900e85` are ancestors of exact frozen HEAD.
- Independently checked original SCC review `b7ad73f4c56e4b698165a3be972148c0`: **Accepted**, decision sequence 3288, exact `d24fda76...`.
- First-parent diff adds or changes **only** `Server/WorkQueueDependencyDiagnostics.fs`, `Server.Tests/WorkQueueDependencyDiagnosticsTests.fs`, `Server/Server.fsproj`, `Server.Tests/Server.Tests.fsproj`. These same four files have **no diff** against the SCC second parent; no reviewed SCC source was altered in merge.
- `d24fda76... -> 0544cef1...` test diff changes only trailing spaces; no behavioral edits. `git diff --check HEAD^1 HEAD` exits 0. Frozen checkout is clean.

## 2. Independent algorithm and coverage audit
- `analyze graphVersion covered works` is a pure diagnostic function, not a claim, runnable-stage, Scheduler, persistence or source-mutation entry point. Its graph is keyed by exact stage-generation `Id` and includes only `WorkStage.Dependencies`, preserving waived hard-edge visibility while excluding informational `SubjectId` and source lineage.
- A false verified-highwater/coverage flag, absent hard prerequisite, or map key/stage `Id` mismatch returns a deterministic `Error`, not a complete/runnable graph. Diagnostic output preserves graph version and reports complete coverage only after those checks. This is **caller-verified** coverage; the boolean alone is not evidence that the caller fetched a complete index.
- Tarjan DFS uses lowlinks and stack membership; a single-stage self-loop and multi-stage SCCs are warned, distinct disjoint SCCs stay separate, and acyclic components do not warn. Warnings contain the exact members plus all internal hard-edge witnesses, without mutating/rejecting the source graph.
- Ordered F# `Map` / `Set` traversal yields stable `StageIds`, `HardEdges`, warning components, members and witness edges, independent of input insertion order or repeated reads. Complexity is O(V+E) time with O(V+E) bounded output/storage; standard recursive DFS stack is O(V).
- Inspected deterministic tests covering self-loop, 3-cycle, disjoint components/acyclic tail, removal of an edge, informational edges, waived hard edges, incomplete highwater, missing dependencies, key/ID mismatch, stage-generation identity and insertion/re-read stability.

## 3. Actually executed local .NET 11 Release evidence
- `dotnet --version` returned **11.0.100**.
- In exact frozen worktree: `dotnet test Server.Tests/Server.Tests.fsproj --configuration Release --no-restore --logger 'console;verbosity=normal'` **exit 0**, compiled `Common`, `Server`, `Server.Tests` Release/net11.0 and NUnit discovered 223 of 223 test cases.
- Independent concise confirmation: `dotnet test Server.Tests/Server.Tests.fsproj --configuration Release --no-build --no-restore --logger 'console;verbosity=quiet'` **exit 0**, non-truncated result: **223 passed; 0 failed; 0 skipped; 223 total** (5 seconds).
- No GitHub Actions, Codex, main push, deployment, Scheduler activation or external integration attempted.

## 4. Upstream gate / explicit decision
- Upstream WQ-07 combined review `1d4384f3a93149f0bd14a2acf195feda` is **Accepted**, decision sequence 3509, exact `4f831301...`.
- At decision preparation, upstream WQ-03 journal review `e397b1920c2742019d71f2b6372b06e8`, exact `53ab33...`, is still **Claimed**, without decision. Its acceptance and sequential integration remain independent prerequisites. Any changed upstream/source ancestry requires new exact frozen review, not approval by implication.
- **Verdict: Accepted** — the exact frozen WQ-04 SCC addition meets its pure deterministic diagnostic and fail-closed coverage contract. **Integration/activation remains gated on accepted and integrated upstream WQ-07 and WQ-03 source and unchanged verified ancestry.**