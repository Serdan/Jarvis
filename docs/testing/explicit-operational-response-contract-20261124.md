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


## Adjudicated partial-receipt correction (2026-11-24)

Original W14 review c7e3594a2d184ff6b7b02783f6bd9e35, decision 13049, remains unchanged. Independent Adjudicator 3 disposition 13118 authorizes only the partial-evidence finding (blob 49490a0412300fc29d832730aaae090a3d0598b9). Correction task automation-w14-partial-receipt-correction-20261124; clean merge 8dd9368cab4d87221d034d97329d3aacb3e6ac6b preserves both frozen original eaf2286af6540cdd0b169fe8291d66a1f078b3ff and published 08994560914ac473c4c9d935bf584c0e3cc7b3cb as parents. No launcher edit, unrelated ledger-publication correction, target integration, or deployment.

The response now stores typed completeness independently of Error. Server preflight composition and destination client-owned job composition share one union/deduplication function: only two complete inputs produce complete evidence; incomplete inputs retain their independently known union, and a pending destination remains pending. The forward envelope and terminal job serializer preserve partial evidence without replay or inventing an error; source activation and original result/error/operation identity remain intact. A mixed-version writer emits a null legacy list for incomplete non-UncertainOutcome results, with the known subset in an additive field. UncertainOutcome retains its legacy-safe partial list. Old readers that ignore completeness and the additive field can still display an empty complete list and lose the new subset over an old hop; that inherent visibility limitation is not a new guarantee. New readers recover legacy missing/null receipts as unknown, distinguish explicit completeness and reject contradictory receipt status. New peers invalidate conservatively for unknown evidence. A coordinated rollout remains required.

Verification (owner-reported local Release executions, independent exact-source review still required):

- RED 70e08e89f6bb462bad32b4b607d26f5a: two new tests fail on the original merged implementation (expected known subset, got Unknown []). Initial GREEN 50d5d73dc4d947d0bb826237a32a4869 11/11 Server, 829cd90c5b244bc6b5d65025f3a0839b 16/16 Client. Expanded tests include complete/unknown pairs, legacy missing in either direction, ordinary success and ordinary/uncertain error, duplicates, Pascal/camel JSON old/new record fixtures, source activation, pending/terminal polling and no duplicate destination execution.
- Final scoped Client f3acf14e6ab24bc0962269f23a3cf39a 279/279; narrower named response-alert, template, integration and forwarding regressions 12b5f4c634534121ad421b93eaefd24d 27/27; isolated final four-case forwarding matrix eb30c10623b8412d8bf65cd3ef6f429b 4/4. Counts overlap, not additive. W17 actual authenticated post-commit challenge wake harness 0837ae3e9a394e43b56362d43090fecf 13/13.
- Expanded Server 4fbd3bdc11ba4e3fb7365f05aef107c4 135 passed, 3 failed (138 total). All three failures independently reproduce on a fresh, untouched checkout of exact published 08994560914ac473c4c9d935bf584c0e3cc7b3cb (841bfe3d16594246918010060e3a6c94, 81 passed, same 3 failures): `client-backed MCP tools expose an optional per-command deviceId` (165 vs 166), `command catalog matches AgentCommand union and MCP tools`, `mcp_tool_oauth_metadata_matches_command_permissions` (unexpected server-local RecordAggregateInclusion). Catalog fixtures are unchanged by this correction; this is a baseline issue, not claimed green or repaired here. Aggregate-inclusion typed evidence test passes in the expanded Server run.
- Ordinary response measurement 497254d54adb4888a5e6d803ca9fe8fe: 30 calls, 153 bytes min/max, unchanged from original W14 typed candidate; p50 0.555 ms, p95 0.962 ms, process rchar 3577 and syscr 69. Shared-host process counter/latency differences are not attributed to repository reads or claimed as an improvement/regression. The additive field is absent in ordinary complete receipts. Updated response JSON schema parses as valid JSON.
- Separate health-coupled harness (constructor migration compatibility) 7a02661ced704c2899ca04fbae417bb9: 15/15 Release tests passed.
