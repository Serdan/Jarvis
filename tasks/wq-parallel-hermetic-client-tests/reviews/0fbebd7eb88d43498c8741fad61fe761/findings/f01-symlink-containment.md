# F01 — P1: Lexical sandbox validation accepts escaped symlink ancestors

Frozen source: `Client.Tests/HermeticHarnessTests.fs:14-21,33-77`, `scripts/test-client-hermetic.fsx:69-95,125-149`, commit `c5a2a1f5c3d784e7bb275a9e7be97ae214e0c7f3`.

**Issue.** `Under(root,path)` uses `Path.GetFullPath` and a text prefix. It does not resolve symbolic links or reparse points in path ancestors. The `JARVIS_CLIENT_TEST_HERMETIC_ROOT` externally managed branch skips creating/attesting the directory layout. Suppose a disposable `<root>/config` symlink points to a second directory outside `<root>`; `XDG_CONFIG_HOME=<root>/config`, `TMPDIR=<root>/tmp`, and other asserted paths set lexically inside `<root>`. All path checks can pass, yet `ClientConfig.configPath()` resolves through that link and can operate outside the intended sandbox. Likewise `data` can redirect default activity and ledger locations. The new one-test positive assertion also misses this case.

**Impact.** The claimed fail-closed guarantee for direct/external-root invocation does not hold if a sandbox ancestor is redirected; real operator state could be accessed if that outside target were a live path. The ordinary runner's newly created private directories mitigate its default invocation, but do not establish the general fixture invariant.

**Correction.** Reject linked/junction directory components, attest ownership and real containment for all layout paths, and validate each relevant environment mapping under externally managed roots. Add a negative regression with TWO disposable directories only, confirming a symlink escape is rejected before any outside write. Never test against operator files.

**Evidence.** Static review of exact frozen SHA. A synthetic shell test was blocked by Jarvis command policy and was not executed; no live files were touched.