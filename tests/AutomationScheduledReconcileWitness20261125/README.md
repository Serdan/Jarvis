# Ordinary hosted Scheduler reconciliation witness

Pinned integrated base: `7a9ffa965b13f95a071cb125edbaa24cbe76f320`
(tree `ec4e4c40e657bccad7f07756f0cd8ede5ff38190`).

Two disposable cases exercise the actual recurring BrowserSchedules.Service,
Program.executeReadySchedule, authenticated caller/occurrence/permission/ledger
guards, ScheduledReadyResume.executeOnceTracked, and
Program.reconcileReadySchedule against a bare fixture remote and real
canonical Git operations. Only the browser endpoint is faked over an
authenticated loopback WebSocket. The real launchAgent.js source bytes are
loaded and asserted on the executeJs frame. No actual provider invocation.

- Late actor loss at the second preflight (after durable Published journal)
  produces no browser frame. The persisted original selection and canonical
  intent are matched exactly; the non-invocation blob binds operation ID,
  request hash, sequence and stage. A subsequent healthy occurrence uses a
  fresh operation ID for the identical Ready episode and reaches a fake
  successful submission. No terminal/no-invocation proof is fabricated for
  the invoked successor.
- The disconnect case aborts the fake socket after receiving executeJs.
  OutcomeUnknown must retain the original intent with no no-invocation proof.
  Reconnect a fresh authenticated available fake window, change both source
  and route/revision, and assert no new browser frame and unchanged canonical
  receipt/proof readback. The submitted successor in the first case is also
  fenced against changed-source/route replay.
- Both cases compare the actual persisted EnteringSend selection with its
  canonical intent and call the actual production reconciliation callback
  with original journal identity. Changed request source hash, binding hash,
  operation UUID, or actor is rejected. Reconciliation is inspection, not Send
  authority. The pre-existing accepted ScheduledReadyResume20261125 restart
  matrix covers the scheduler's automatic callback invocation at interrupted
  stages (23/23 in checkpoint 15738); this fixture adds hosted positive and
  changed-effect/transport evidence, not another restart model.

Run from an isolated checkout:

```sh
dotnet test tests/AutomationScheduledReconcileWitness20261125/AutomationScheduledReconcileWitness20261125.fsproj -c Release -m:1 --logger 'trx;LogFileName=w21-scheduled-reconcile-final-v2.trx'
```

Final job `4b915a90524142579dcbc3c403e52fb2`, exit 0, 2/2 passed,
0 failed, 0 skipped. The dependencies were built from the pinned source
in job `9d99002c152d41a5b652db84ece2d027`; final fixture-only run added
`--no-restore -p:BuildProjectReferences=false`. TRX at
`TestResults/w21-scheduled-reconcile-final-v2.trx`, SHA256 `5c87c9bc2c41fc964afbd6e0cdebdf0eb33a98cbb76a945a2e9e808188c56cf9`.
Initial strengthening exposed test-only assumptions about diagnostic display
and WebSocket message fragmentation; these were corrected using the stable
OutcomeUnknown contract and the existing accepted bounded multi-frame reader.
No production defect established, no production or shared project changes.

This is a deterministic fixture, not an actual OAuth renewal, provider
acknowledgement, OS crash/restart, or deployment witness. It does not
introduce any occupancy prerequisites or provider-terminal authority into
ordinary Ready resume. No live sends, production restarts or activation.
