# F06 closure assessment — frozen architecture review

**Review:** `a65e09f0b6d94ca6b6c2d81dd5a3487e`  
**Source:** `90ed868d04524d0483ccd979cffb5029d8cf7027`  
**Prior blocking finding:** `da4bf7f8e2a54e12a4e795adc8ec3479`, F06/P1 on source `05518449e126f2a312e2646cb73786a9be7337c8`  
**Disposition:** F06 corrected; no blocking findings. Documentation-level acceptance only.

## Counterexample replay

1. Integrator W owns the accepted exact SHA and has durably dispatched original push P, but no canonical `Integrated` receipt exists. Lead inserts unsatisfied hard prerequisite B by project Git CAS. The correction attaches `IntegrationGateHold` in that **same CAS** to W's stage generation, original push ID, graph revision and unsatisfied B. All relevant producers, wakeups, claims, demand-credit matches and downstream policy admission compare the live hold and verified index/graph coverage (§5, line 166; §7, line 253).
2. P may finish *after* the gate CAS, or may have already finished with its acknowledgement lost. `ReconcileGatedExternalPush` preserves original push/target/SHA and writes `ExternalEffectObservedPendingGate`, **not** final `Integrated`, `DomainFinalizedTerminalPending`, `IntegrationAuthorized` or a downstream producer. It cannot replay P. If another mutating effect remains unresolved it retains RecoveryBlocked/source/capacity fences; only a verified project launch-terminal receipt permits independent global token release (§7, lines 255, 259, 261; §9, lines 309–313).
3. Before B's satisfied/waived receipt, `ResolveIntegrationGateException` rejects; a negative remote-ancestry read does not establish P cannot still land. Once all original edges are satisfied or Lead/operator authorizes explicit versioned waiver/replan, one project CAS marks each edge's historical disposition, closes the hold, writes the single final `Integrated` and `IntegrationAuthorized` producer, and updates index/high-water. No second push or Integrator launch (§7, lines 255–259; §10, lines 322, 345–347).
4. Competing gate edit, ordinary `CompleteIntegration`, push reconciliation, `ResolveDependencyGate` and satisfaction/waiver share project claim/domain/graph/gate CAS and original operation receipts. Domain-first edit rejects `DomainAlreadyFinalized`; gate-first ordinary completion fails. Already claimed downstream work is fenced through its own dependency generation; remote cross-project hold coverage failure is Incomplete, not Ready (§5, line 166; §7, line 259; cases 74–80).

**Result:** The prior F06 exploit — a remote push that lands after B's hard gate causing D to wake before B is satisfied — is no longer an authorized state transition. The physical Git side effect is preserved without bypassing workflow authorization.

## Negative-space fixtures and residual clarity

Acceptance cases **74–80** explicitly cover push-after-gate, delayed acknowledgement, unknown remote result, two-client CAS races, audited waiver, post-B satisfaction finalization, already-claimed downstream work and cross-project/index coverage. Case 79 exercises resolution after the original launch safely terminalizes. These are **required future fixtures**; no implementation of these tests was executed for this documents-only review.

**Nonblocking Phase A precision recommendation:** Make the exact `ResolveIntegrationGateException` typed precondition explicitly cross-reference safe original-activation/source-job reconciliation, not merely the immutable effect receipt. The safety requirement already exists normatively in §7's “If any side effect ... remains unknown, preserve RecoveryBlocked, gate hold and necessary capacity/source fences” (line 259) and §9's mutating-job/source-fence rules (lines 309–313), so this is an implementation-contract clarity improvement, **not** a finding that a currently permitted transition bypasses the hold.

No additional blocking defect identified in retained F01–F05, global capacity/C01, session-bound UUID, Role/Task messages, deterministic matching/FIFO, SCC diagnostics, source continuation, exact frozen review or staged migration boundaries. No source changes, merge, implementation, deploy, restart or scheduler activation were performed.
