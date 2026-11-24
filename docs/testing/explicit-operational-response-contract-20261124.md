# Explicit operational response contract — owner evidence

Source provenance: W14 isolated branch composes accepted W11 eed1fe1b74fbaf3dca867d7915ffaaeed9767c81 and W27 correction c406b948aa547d99d1c42e0542f8af4f6e7953a2 (review 26cdeee39b9b4abc98ea3a293e0f7f8b, decision 12905), plus published 9e66a2cbeefb0493d6662c7a075713e0f3b58378 containing W17 314c328a3f92afc66771276e0424538d9afd564e. Merge 6fecf4a6e25fc2d0181828130bf662d00c39fee3 resolves only Client/SignalR/Client.fs overlap; the prepared-source path forwards the exact existing post-commit callback, never restores the retired implicit instruction wrapper. W18 unaccepted ACK changes not cherry-picked. No target merge/push or production activation.

## Measured comparison

The Stage A canonical checkpoint 12675 (attempt c42f487a2a2d4b9597bad1a824984f39) measured the actual pre-retirement 9f6b976-era inbox job: 17,043-byte original ToolResponse, 3,836-byte Instructions array, 17,203-byte enclosing poll payload. The two source documents were 1,871 and 1,518 UTF-8 bytes. Source inspection established two implicit file opens and three logical full-file passes per document, 10,167 content bytes; those are logical reads, not observed syscall counts. Removal's counterfactual 13,191 bytes without Instructions was not a measured post-change envelope. W27 owns that large ambient-context reduction, not W14.

W14 remeasured the incremental contract against the exact accepted W27 correction using the same disposable ordinary ReadFile fixture (two repository files of those sizes, 13-byte requested source). Fresh FSI processes loaded each snapshot's Release Client.Tests assemblies, one warm-up, 30 actual calls with JSON serialization; harness Client.Tests/operational-response-measure.fsx. Linux process-wide /proc/self/io deltas include the measurement read, not just source-file activity. They are not physical disk bytes. No real agent identity, live repository or scheduler was used.

| Metric | Accepted c406b948 baseline (job 0bc48eab09ad4243880c283e1ad3fce6) | Typed candidate (job 51ff6299ac8d4849870525e66b88e00a) |
| --- | ---: | ---: |
| Serialized AgentCommandResponse, each call | 133 bytes | 153 bytes |
| p50 latency including serializer | 0.900 ms | 0.926 ms |
| p95 latency including serializer | 1.671 ms | 1.410 ms |
| Process rchar delta, 30 calls | 493 bytes | 493 bytes |
| Process syscr delta, 30 calls | 62 | 62 |

No latency improvement claim: one small warm fixture, shared-host variance. Identical I/O in this fixture is consistent with both snapshots already having removed implicit repository reads. The older Stage A fixture and this disposable fixture are not directly comparable in total bytes. W11's existing verified-negative poll regression measures zero synchronous remote fetches; it remains in the scoped Client suite.

Independent old-record and new-record actual System.Text.Json serialization/deserialization, both Pascal and camel property naming, server job 439b54290d824caaaf7444c08bf4c34d (92/92):

| Response fixture | Legacy bytes | Typed + compatibility bytes |
| --- | ---: | ---: |
| Ordinary | 153 | 173 |
| STOP + soft timing | 429 | 716 |
| Error with confirmed mutation | 179 | 199 |
| Unknown state-change evidence | 154 | 172 |

The small ordinary overhead is explicit state completeness; exceptional controls pay temporary dual-field overhead until coordinated compatibility retirement. Both control shapes are charged in the existing raw result byte-budget guard; the final visible alert boundary also rechecks the guard. No arbitrary limit increase or claim that this estimate is a measured SignalR frame size. Old-reader fixture validates missing legacy Instructions for ordinary responses and exact projection when controls exist. New-reader prefers the typed field, including explicit empty, rejects contradictory receipt status; missing/null legacy mutation evidence is unknown. Error/operation ID, activation receipts and original job result are retained. Best-effort at-most-once remains unchanged; a lost response after consumption can lose an alert.

## Verification and boundaries

Red-first commit a200fb1ca1d45bfe23ea8087bfac4052ebcffc43 demonstrates stale one-shot in raw job JSON, false authoritative empty missing evidence, and source alert loss. Corrected prerequisite independently accepted before final composition. Owner tests and measurement job IDs are recorded in the canonical attempt checkpoint; independent review of the exact final head is still required. Neither source freeze nor passing owner tests authorizes integration/cutover. Existing session thresholds remain 25/30-minute cooperative advisories.
