# ObserveAgent age preflight — terminal evidence

Exact executable source commit: `69fcdf631f70c1875250a7a6bd332d9ceb8a01ae`, parent `291b5499051fbddfff1c76308e1a0202a5d1862f`, branch `test/observe-agent-age-w8-20261127`. Only the exclusive test and review document directories differ from the pinned base. No production edits or live recovery.

## Results

| Command (from root; see contract.md) | Terminal status | Evidence |
| --- | --- | --- |
| Proposed model + source-boundary checks, Release | Exit 0 | Tool job `51bc0214fb8d47ec9450e1e2d550b78b`, TimedOut=false: PASS registered source route, output boundary, purity guards, and independent Health evidence; PASS deterministic model, boundary/state matrix, missing/future/malformed, offset and precision. |
| Final model with `--no-build -- --require-wired`, Release | Expected exit 1 | TimedOut=false; stderr: RED: actual ObserveAgent producer lacks proposed derived age fields. Not a compilation failure. |
| Existing Client.Tests focused regression (two cases), Release | Exit 1; 1 passed, 1 failed, 0 skipped | Tool job `3210ae396aa24927b25476d881c96b26`, TimedOut=false. TRX `/tmp/jarvis-w8-observe-agent-age-20261127/observe-agent-purity.trx`, SHA256 `a43d560dd27e6b588b617b0ffdc05015869279d76c8013768bfc74d2e2e8657f`. |
| Separate disposable actual SignalR runtime witness, Release | Exit 0 | Tool job `6c0668f155914738915dc1957b656175`, TimedOut=false: PASS actual registered ObserveAgent response has no derived fields; cache provenance, state, ledger and private data preserved. Exact command in contract.md. Existing FS0760 warning in Client/Program.fs:560; no errors. |

## Baseline regression finding

On unchanged production source, `CrossAgentObservationIsReadOnlyAcrossEightReadyAgentsAndSessions` fails at `Client.Tests/ReviewWorkflowTests.fs:4351`: expected one verified ledger fetch, observed two. The assertion precedes payload and no-effect checks, so that run cannot establish the remaining route assertions. The other selected test (read-only activity predicate) passed. This is a pre-existing pinned-base observation, not evidence that the proposed age model changed production behavior, nor evidence of target activation or disclosure. Root cause has not been established. Do not edit the existing test or production route under this task. Lead/W4/W6 should reconcile the extra read with the context-efficiency work on their current integrated base and own any correction separately. The new isolated runtime witness passed the payload and read-only invariants independently of a fetch-count assertion; it does not resolve or waive that separate regression.

