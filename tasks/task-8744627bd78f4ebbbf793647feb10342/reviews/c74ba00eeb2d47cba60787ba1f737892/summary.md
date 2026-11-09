# Independent review — Windows bounded-process output loss

**Verdict: Accept (no blocking findings).**

Frozen commit: `a11c76f80301abf488e3db5e083885e802d2086f` on `fix/windows-bounded-output`, based on `a9adf77b0b1b75420f2d99e901319df713e817b3`.

## Root cause and implementation

KRUSO's `InvalidBinding` is caused by lost stdout in the Windows `BoundedProcess` helper, not by mismatched approved FinanceFlow Git endpoints. The original helper started its target with inherited standard handles, which did not reliably forward the already-redirected helper stdout/stderr on Windows. The patch explicitly redirects the child streams, forwards **raw bytes** concurrently through the helper's standard outputs, and proxies stdin with EOF/flush/close handling. It waits for stdout/stderr EOF before returning the child exit status; the outer command continues to own the Windows Job Object and its timeout, cancellation and descendant teardown. macOS and Linux paths remain unchanged.

Checked helper invocation and event barrier order, pipe backpressure and output draining, per-stream caps in the parent, stdin behavior, descendant lifetime, error propagation and cross-platform isolation. No blocking source findings. The branch is clean; `git diff --check` passes.

## Independent verification (Release, .NET 10)

- Client.Tests full suite: **528 passed, 0 failed**.
- BoundedProcessTests focused suite rebuilt from reviewed source: **12 passed, 0 failed** (including new stream and exact Git URL regressions).
- Server.Tests full suite: **122 passed, 0 failed**.
- Initially `.NET 11 rc3` compilation failed because its expected .NET 10 targeting pack was not restored. Restoring with installed stable .NET 10 SDK 10.0.403 resolved the environment problem; source/build tests then passed.

## Release gate

The newly compiled Windows self-hosted client has **not** been executed on KRUSO in this review (test host is Linux). The new tests will exercise the Windows path when run on that OS, but they do not constitute a live Windows verification here. After an operator-approved compatible client release, smoke-test `git remote get-url origin`, `get_git_status` and `ledger_health` on KRUSO to prove the actual output-forwarding and trusted-endpoint validation paths. Do **not** change FinanceFlow's Git origin or trusted binding as a workaround.

No deployment or live FinanceFlow changes performed.
