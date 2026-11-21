# P1 — Delayed-Git control does not test remote Git contention or ledger retry behavior

**Frozen source:** `fcadb38ed2312e961e1329f54ca1bbf6e56df7cb`.

**Original contract:** "Stress a 1-second injected slow remote Git operation and cancellation to demonstrate cheap reads are not blocked or retried unnecessarily" with deterministic subprocess/fetch bounds.

**Observed:** `Client.Tests/BoundedProcessTests.fs:186-250` runs a disposable `git -c alias.pause=!sleep 10 pause` child, starts a separate `git status --porcelain` while it sleeps, cancels the alias, and then runs a *separate* uncancelled `git -c alias.pause=!sleep 1 pause` control. My exact-source Release run passed and measured ~2.84ms independent status, ~502.51ms cancelled child, ~1007.54ms delayed control. This verifies BoundedProcess cancellation and independent local subprocess scheduling. It does **not** inject a delayed remote `fetch`, exercise a real LedgerStore read while the remote operation is held, or assert that the read does not initiate duplicate retries/fetches under contention.

**Impact:** The test can remain green if the actual ~0.8-1.3s MCP read path blocks on a fetch lock, repeats Git operations, or misclassifies cancellation. It does not establish the contention property central to the task.

**Candidate remedy:** Add a disposable remote/ledger fixture with a controlled delayed fetch in parallel with a cheap pinned read; assert completion, cancellations, operation count and no unbounded retries. Keep wall-clock values informational and OS parity explicitly unclaimed. Any alternative fixture proving the same end-to-end condition is acceptable.