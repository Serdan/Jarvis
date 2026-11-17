# Native Windows bounded-process lifecycle: 2026-11-17 smoke evidence (HOLD)

## Scope and immutable provenance

- Canonical Jarvis P2 task: `task-4bcca217de1b4045b436db172d42068a`, Native Windows/macOS smoke-test gate.
- Source checked out on native Windows at the fetched remote `main` commit **`aa2e46ff2927b8e9fd0af25204377c299f2ec576`**. This is a test baseline, not a claim that newer local Linux sources or a release artifact are identical.
- Device: `KRUSO-PF539JMB`, native Windows x64. `dotnet --info` reported Windows OS version `10.0.26300`, RID `win-x64`, .NET SDK `11.0.100` and host/runtime `11.0.0` (MSBuild 18.12.2). Test project targets `net11.0`.
- Worktree: registered isolated `Jarvis-Windows-QA::worker23-native-subprocess-qa-20261117`, branch `qa/worker23-native-subprocess-qa-20261117`, based on commit above. The preexisting Windows QA main checkout had unrelated uncommitted WQ11 modifications and was not touched.
- Implementation under test: `Client/Modules/BoundedProcess.fs` Windows Job Object and named EventWaitHandle helper; `Client/Program.fs` `--jarvis-internal-process-group` entrypoint. The standalone F# smoke loads the actual **Release** `Client/bin/Release/net11.0/JarvisClient.dll`, invokes `BoundedProcess.run` directly and exercises the managed `dotnet` host helper path. It is not a published apphost/single-file binary smoke.

## Exact execution and test environment

1. Native `dotnet --info` on the isolated worktree: Windows 10.0.26300/x64; .NET SDK 11.0.100; .NET host 11.0.0.
2. Executed `dotnet test Client.Tests/Client.Tests.fsproj --configuration Release --filter FullyQualifiedName~BoundedProcessTests --logger "console;verbosity=normal"` in that worktree. Restore and Release builds succeeded, but the 12 NUnit cases **did not execute**. All 12 are reported failed in global `OneTimeSetUp` because `Client.Tests/HermeticHarnessTests.fs:87-90` intentionally rejects Windows and macOS: `Hermetic Client.Tests currently supports Linux only; Windows/macOS known-folder isolation is a tracked follow-on.` This is a valid, profile-safety fail-closed control; **do not bypass or weaken the setup**. Therefore no native NUnit acceptance is established, and no full Windows Release suite was run.
3. Independently executed `dotnet fsi --exec native-windows-bounded-process-smoke.fsx` in the worktree (initial development run used `.worker23-native-smoke.fsx` with identical test logic for the corresponding cases). The FSI process uses a temporary disposable directory, builds script fixtures there and calls the production bounded-process implementation; its child PIDs are observed before its own finally-cleanup. The FSI script can run one named case via an additional substring argument, e.g. `dotnet fsi --exec native-windows-bounded-process-smoke.fsx success_reaps`.

### Observed Windows native test matrix

| Case | Outcome | Evidence / boundary |
| --- | --- | --- |
| Named readiness event, Job Object, stdout and stderr forwarded | **PASS** | Exit 0, `TimedOut=false`; both ASCII markers separately present |
| ClosedInput delivers EOF | **PASS** | Child outputs `LEN=0` |
| TextInput raw UTF-8 byte preservation | **PASS** | Child reads `Console.OpenStandardInput()` raw bytes, base64 round-trip equals UTF-8 `hello ø world` |
| TextInput as `Console.In.ReadToEnd()` + non-ASCII stdout round-trip | **FAIL / encoding triage** | Exit 0, no timeout, returned text `GOT:hello Γö£Γòò world`, not `GOT:hello ø world`; because raw bytes do match, this alone does not prove input corruption in the bounded executor. Windows console encoding/decoding must be characterized separately |
| InheritInput with nonreading target | **PASS** | Exit 0 without hanging, stdout marker present |
| Nonzero exit distinct from timeout | **PASS** | Exit 23, `TimedOut=false` |
| Direct hung target deadline | **PASS** | `TimedOut=true`, `ExitCode=-1` at ~5.02s with 5s configured deadline |
| Concurrent large stdout/stderr with 31-byte caps | **PASS** | Exit 0, both truncation flags true, both retained UTF-8 result sizes <=31 bytes |
| Cancellation after child PID marker | **PASS** | Typed canceled outcome, child PID no longer alive within 3s (observed ~1.47s for whole case) |
| 300,000-byte TextInput to nonreader | **PASS** | Timed out in ~5.03s without unbounded stdin write |
| Parent exits while descendant holds **both** outer stdout/stderr open | **PASS for deadline and cleanup** | `TimedOut=true`, `ExitCode=-1`, both descendant stream markers present; descendant PID `17652` independently observed dead after result and before test cleanup (prior run also PID `4152`) |
| **Normal parent exit with detached descendant whose own stdout/stderr are separately redirected** | **FAIL / Windows release blocker** | Parent printed `SUCCESS_PARENT_EXIT` but bounded call returned `TimedOut=true`, `ExitCode=-1` after ~20.03s instead of clean success. Reproduced on two whole-matrix runs and the isolated run described below |
| Premature **owner** process crash while target remains in Windows Job Object | **PASS** | Owner forcibly killed with `Kill(false)` (not recursive); actual target PID `31252` independently observed dead after owner exit, before fallback cleanup |

The initial run of the standalone 13-case matrix (prior to adding the raw-byte check there were 12 cases) had a test harness path error in the owner-crash nested FSI (`#I` was relative to the temporary file). Corrected `#I` to the actual isolated build-output absolute path. The corrected owner-crash case passed. Do not count the first harness-path failure as an implementation defect.

### High-signal process tree: immediate parent dead, bounded result still pending

Focused exact command:
```text
dotnet fsi --exec native-windows-bounded-process-smoke.fsx success_reaps
```
The parent script launches a child with its **own** `RedirectStandardOutput=true` and `RedirectStandardError=true`, waits until the child writes a PID marker, writes its parent PID marker, prints `SUCCESS_PARENT_EXIT`, and finishes. The test samples native PID liveness 2.5s later **while** `BoundedProcess.run` is still pending:

```text
SUCCESS_LIVE_TREE parent_pid=18580 parent_alive=false descendant_pid=1992 descendant_alive=true bounded_finished=false
FAIL success_reaps_detached_descendant elapsed_ms=20035
Outcome { ExitCode = -1; TimedOut = true; StdOut = "SUCCESS_PARENT_EXIT\r\n"; ... }
```

This rules out a parent still computing as the reason for the 20-second delay. Native Windows may be retaining/inheriting a writable pipe handle in the live descendant even when it separately redirects its own logical streams, leaving the Windows group helper's output-copy completion pending after the immediate child exits. That handle explanation is a **hypothesis**, not a proven handle-table trace. The actual release-gating behavior is unambiguous: a normal command completes only as a deadline timeout on Windows in this fixture. No claim that the descendant survives the deadline is made; the test has a fallback cleanup.

Implementation seams for review: `runInternalGroupHost` in `Client/Modules/BoundedProcess.fs` waits for immediate `child.WaitForExit()` and then unboundedly awaits `Task.WhenAll(stdout, stderr)`; `runWithCancellation` waits on the group-host process and both streams before returning. Any correction needs to preserve the separate negative case in which descendants **intentionally hold and write to** both inherited pipes; do not convert that real deadline failure into an unbounded wait or silently drop output. The Windows child-start barrier must still assign the Job Object before target launch, and all three stdin modes must remain independent of the barrier.

## Distribution gate / outstanding work

**Windows: HOLD.** Although several native lifecycle cases passed, successful cleanup with a separately redirected descendant failed, NUnit was intentionally setup-blocked, and representative framework-dependent apphost and published single-file helper startup have not been validated. The focused and full Release suites are not accepted on Windows.

**macOS: HOLD / untested.** No macOS Jarvis device was registered in this session. There has been **no native** `libSystem.B.dylib` setsid/kill load, helper-startup, inherited-pipe, cancellation, early-parent-exit, native apphost, or package evidence. A Linux managed build or source inspection cannot substitute.

**Required before any cross-platform distribution:** Investigate Windows descendant handle ownership and helper drain/exit behavior with process-handle/pipe evidence; add a deterministic native regression that differentiates normal-exit detached descendant from intentional pipe-holding deadline; implement a reviewed correction if required and rerun this smoke and safely hermetic native NUnit Release tests. Integrate the Windows known-folder isolation work through its own review, without bypassing the guard. Validate managed host, framework-dependent apphost and single-file artifact explicitly. Obtain a native macOS runner or explicitly exclude macOS releases. Finally obtain full Release test results, independent review, and explicit release authorization. **No deployment, release publication, source integration, or platform GO is claimed by this report.**
