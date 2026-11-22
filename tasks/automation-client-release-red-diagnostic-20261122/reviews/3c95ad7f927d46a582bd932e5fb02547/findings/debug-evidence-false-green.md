# P2 — Debug evidence is admitted as Release PASS

Frozen source `1d73d13d7c735ca1ea91acd2079ba8f7563473bb`; `scripts/tests/automation-client-release-diagnostics-20261122/analyze_release.py` lines 45-49, 86-90.

**Actual behavior.** The classifier checks `dotnet test`, project path, and absence of `--filter`, but does not require Release configuration (nor compare to the fixed launcher's full command). Using a valid one-test passing TRX, a manifest with correct pinned source/SDK, `exit_code=0`, `timed_out=false`, and `cmd=["dotnet","test","Client.Tests/Client.Tests.fsproj","-c","Debug"]` returns **PASS**.

**Independent direct reproduction.** Reviewer 4 executed a read-only synthetic input via `python3 -c` in the exact frozen source worktree; output: `DEBUG_CONFIGURATION_NEGATIVE verdict=PASS`, terminal exit 0. This does not claim the original worker ran Debug; its genuine launcher commanded `-c Release`.

**Contract/impact.** The task, documentation, and requested acceptance all explicitly require .NET 11 **Release**. The analyzer currently turns a Debug receipt into a purported complete Release PASS. Reusing this classifier for a future composed matrix could falsely satisfy the Release gate.

**Proportional remedy (subject to independent adjudication).** Require the expected Release project invocation, or at minimum explicitly parse and validate `-c Release` / `--configuration Release` and reject duplicate conflicting switches, and add a Debug-negative regression. Preserve the original 900.126s timeout as UNKNOWN. No production/Client.Tests correction or full-suite replay is implied.
