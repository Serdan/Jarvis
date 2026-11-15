# P1 — Idempotency receipts do not bind the full semantic mutation payload

**Frozen SHA:** `1b886591ae7e87158fc0debe2bf5f01858d17b7d`  
**Source:** `Server/WorkQueueContracts.fs:215-237, 399-439, 454-465`; tests `Server.Tests/WorkQueueContractTests.fs:182-195, 197-220`.  
**Contract:** Same operation ID + changed payload must be `OperationIdReused`, not silently interpreted as exact retry.

**Reproduction:** `projectCas` stores only caller-provided payload strings. `publishStage` uses `publish|<stage.Id>`: publish stage A with Dependencies={B}, then retry with the *same operation ID and A.Id* but Dependencies={C} or a different SourceLineage. The receipt matches and returns `Ok current` instead of `OperationIdReused`. Likewise `postMessage` keys only message ID/body, ignoring Subject and role work content, and `admitProject` keys only launch ID/role ID, ignoring ProjectId, AccountTokenId, RoleVersion and other launch fields. A lost receipt or wrong replay can therefore conceal a materially different command.

**Impact:** An independent client can believe a different stage graph, role message recipient or token-bound launch was canonically accepted. This violates the stated stable-payload replay invariant and is not detected by existing tests, which mostly change already-included fields.

**Required:** Canonicalize/hash the full immutable logical command arguments into the receipt (including type discriminator, relevant source/role/project identities and nested work/message contents); distinguish permissible server-generated result fields from caller request fields. Add same-operation-ID/different-stage-dependency, same-message-ID/body/different-Subject, and same-launch-ID/role/different-account-token negative tests.
