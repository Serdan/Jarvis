# Independent review — side-effect-free ObserveAgent and universal public state changes

**Frozen source:** `8716439cfe4b6187978b3ae259d97a435f26510e` on `refs/heads/feat/observe-agent-readonly` into `main`. Supersedes withdrawn unclaimed request `4bdd283fe52b448f9a4fc101d2c25257` and addresses Reviewer 1's earlier P2 finding in `d6f9ffaf3efa42a2a64d8f6319dd2db5`.

## Contract
1. `observe_agent(projectName, agentId, targetAgentId)` uses distinct observer and target labels and **one immutable verified Git-ledger snapshot**; both must be registered on the same bound ledger. It does not activate or record Seen for either agent and exposes a compact projection with client-local, noncanonical last-seen provenance.
2. Any forwarded **project-scoped** MCP result now appends a consistent JSON block `{"agentStateChanges":[...]}` and `jarvis/agent_state_changes` metadata. If no *confirmed* canonical mutation occurred, the array is explicitly empty. Effects are obtained from existing immutable transaction receipts, **not inferred** from command names or from the final state. This is advisory state evidence, not authorization. The primary result, error and instruction content are preserved.
3. Automatic self-`agent_inbox` activation returns a typed, confirmed `agentInboxActivation` receipt in the same block (and metadata): `ActivatedByThisCall`, `BeforeState: Ready`, `AfterState: Working`, confirmed commit head and operation ID. Repeated reads report false without inventing another transition; the receipt survives failed subsequent reads.
4. Cross-project `notify_agent` merges **both** the source-agent validation and destination result mutation lists, including when the destination fails. Source canonical mutation evidence is not lost or silently portrayed as `[]`.
5. The client/server wire format is protocol 3.63; old client responses missing the optional activation field continue to deserialize. There is no release or deployment authorization.

## Reviewer evidence
- New focused Server Release tests for success/error/no-op state-effect blocks, original error preservation, committed inbox activation after an error, repeated read and cross-project aggregation — **passed**.
- Client regression: 8 Ready targets × 2 observer sessions, one verified Git fetch per observation, no synthetic state/Seen changes, genuine activation, repeated self-inbox, post-activation failed read — **passed**.
- Full Server Release suite **126 passed / 0 failed**.
- Full Client Release suite running on frozen source under Job `2571259e59654ed29413190cef07e007`; previous complete suite passed 527/527.
- Git patch and branch clean; no server/client deployment.

## Suggested independent review focus
Whether `agentStateChanges: []` accurately means *no confirmed canonical change* rather than none could have occurred on uncertain write outcomes; whether all forwarded commands and error paths attach metadata; cross-project source/target effect merging and possible duplicate cache invalidation; preserving image/export result types and instructions; authority boundaries; and response-size/compatibility implications.
