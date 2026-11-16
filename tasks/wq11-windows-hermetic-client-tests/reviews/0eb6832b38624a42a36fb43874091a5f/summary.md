# Reviewer 3 — WQ11 correction review (e0043d41)

**Verdict: Accepted for the scoped source correction only.** Frozen source `e0043d41443669a3aafe11b718bef45c4af992e6`, parent `1490e0b295ad84ca01ec3714cb67234ea59acf09`, review `0eb6832b38624a42a36fb43874091a5f`. **This is not Windows hermeticity or full-suite acceptance.**

## Previous findings rechecked

- **F03 P1 — parent-crash/orphaned testhost recovery:** The prior parent-only exclusive lease was insufficient. The new harness creates a Windows kill-on-close job and an owner-only `.run-started` marker before starting its child; a minimal FSI gated launcher does not start VSTest until its own job membership has been verified and the parent releases `.process-start-gate`. On normal exit, timeout, or failed termination, the harness queries the job for active members, forcibly terminates remaining members where possible, and writes `.run-drained` only upon observing an empty job. Scratch removal is prohibited when that proof fails. Explicit stale recovery additionally refuses a started root without an owner-only drain receipt. The added native Windows orphan-recovery and kill-on-job-close negative tests target the two prior failure modes. **Source-level correction accepted; Windows behavior has not been independently executed at this SHA.**
- **F02 P2 — unsafe SDK bootstrap instructions:** The updated runbook now requires a separately attested disposable Windows OS account/profile or VM **before** the first `dotnet restore`, `dotnet build`, or FSI preflight. A distinct checkout and redirected TEMP/NuGet package path are explicitly insufficient. This closes the documentation gap.
- **F01 — guarded root-allocation cleanup:** Previous post-creation cleanup correction remains present; no regression was identified in the changed paths.

## Evidence and limits

- Frozen `origin/jarvis-review/wq11-windows-hermetic-client-tests/0eb6832b38624a42a36fb43874091a5f/r1` resolves to `e0043d41`; worker worktree at the same SHA was clean when inspected.
- Independently read the six-file `1490e0b2..e0043d41` delta, including the Windows-native job API/layout, parent launcher handoff, recovery lease checks, regression tests and documentation. `git diff --check 1490e0b2 e0043d41`: exit 0.
- Owner reports Linux .NET 11 Release build exit 0 and focused hermetic tests **9 passed, 8 Windows-only skipped** (17 discovered), plus injected root-failure cleanup. These are owner-reported, not independently replayed in this review.
- **No actual Windows job-object/orphan regression result for `e0043d41` was supplied.** No independently verified Windows full unfiltered Client.Tests exit-0 result, test counts, bounded timeout, process-tree drain or scratch cleanup for this exact SHA. The **separate Windows acceptance/portability gate remains Blocked**. Native focused regression execution and eventually complete isolated Windows Release full-suite evidence are still required before claiming Windows acceptance. No GitHub-hosted Actions, real-profile SDK startup, source integration, or production activity is authorized by this verdict.

The review accepts the correction's source-level design against the specific previously requested F03/F02 changes without asserting unobserved Windows execution or removing the independent validation gate.
