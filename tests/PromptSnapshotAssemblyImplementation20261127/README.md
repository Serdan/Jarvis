# Isolated prompt snapshot assembly implementation

Pinned task `robustness-prompt-snapshot-isolated-assembler-20261127`, spec blob `3923d52a4d763fa4f0c8e385ddef46af445d0a08`, assignment generation 1.

The dedicated branch composes exact accepted fixture commit `7567056a423f32cecc1fb2723fdc3351c1b4de51` and accounting commit `03be07bc06751f878f9128a449138a7a2e18f72a`. Neither accepted input is edited. The module is not added to Client.fsproj.

## Reproduce

```sh
dotnet test tests/PromptSnapshotAssemblyImplementation20261127/PromptSnapshotAssemblyImplementation20261127.fsproj -c Release --logger 'console;verbosity=normal' -m:1
```

The test project links the actual accepted accounting source, new assembler, disposable Git fixture and frozen SnapshotTests.fs. The original Explicit RED remains bound to the legacy renderer (and must remain RED if selected). The separate adapter exercises the five missing-context obligations on the real new pure assembly seam using independently prepared canonical disposable evidence, without replacing the original fixture or asserting registered route acceptance. Pure tests cover ordering, scope, stale/missing evidence, duplicate layers, deterministic prefix, exact accounting, advisory limits and diagnostic negative space.

## Contract and limitations

- The caller supplies one authenticated project/agent/snapshot scope, already resolved applicable layers in explicit ordinal slots, the validated template's stable and rendered identity-bearing fragments, exact focus and issuance, historical issued spec, selected eligible checkpoint identity, and pertinent scoped action text. Distinct missing evidence and resolved absence are represented explicitly. The assembler cannot prove that the caller authenticated those records or selected the correct checkpoint/review/dependency; it rejects mismatching scope, pins and sequence at its boundary. Canonical readers, eligibility and local guidance pinning remain production obligations. The adapter verifies same-snapshot reads in a mutation-free disposable fixture; its independently known yield sequence is not a production selector.
- Precedence remains global, workspace, project/directory, role attachment order, agent, assignment. Common, local and role sections precede agent-specific text, identity, assignment and continuation. Duplicate source keys or ordinal slots fail closed instead of silently dropping a conflicting layer. Text is preserved; no input is truncated to meet advisory budgets.
- No arbitrary inbox/history payload or credential field is accepted. Scope identifiers and pin values are not diagnostic labels; numeric accounting is delegated to the accepted accounting core. Pertinent input text is intentionally included in the prompt, so the upstream resolver must not supply secret/unrelated data. No claim is made that arbitrary malicious text can be recognized or safely redacted.
- The adapter only knows how to split the exact fixture template; general validated template splitting (including non-cacheable custom templates), verified scope and evidence selection, complete review/adjudication and dependency context, reviewer/taskless role-specific focus, canonical freshness and check-in, envelope serialization and transport limits, actual MCP/SignalR and scheduled/manual ingress parity, provider cache behavior, zero authority changes, and production activation remain separate obligations. No ledger/store reader, credential, launch permission, check-in, dispatch, migration or deployment is introduced here.
