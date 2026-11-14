# P1: pre-index operator Challenges disappear and cannot be answered after upgrade

**Frozen commit:** `1438a6821cf3d6bd450e14e3ef7744bb0b68b7a4`, compared to `c84ce8c954546a3866835472f2656affb19e27a3`.

**Relevant source:**
- `Client/Modules/ChallengeWorkflow.fs:1056-1111`: `listOperator` exclusively reads new operator active shards and `records/challenge-index-operator-recent.json`. When that index does not exist, it defaults to `ActiveChallengeCount=0` and `Items=[]`; zero equals the empty shard count and it returns an empty TUI view without reading any existing canonical Challenges.
- `Client/Modules/ChallengeWorkflow.fs:436-470, 488-496`: only mutations through the new `indexWrites` path construct/update the new operator indexes. No initial backfill/migration is present. For a previously persisted single Pending operator condition, `respondOperator` transitions `wasActive=true` to `isActive=false`. With the new recent index missing, `prior.ActiveChallengeCount=0`, so `activeCount = 0 - 1 + 0 = -1` and `updateOperatorRecent` fails `LedgerIntegrityError "Inconsistent operator challenge index counts."`.
- `Client/Modules/ChallengeWorkflow.fs:840-843` delegates operator responses into `update`, which at lines 832-835 requires successful `indexWrites` to commit canonical response and immutable event.

**Counterexample grounded in the supported upgrade path:**
1. Before this frozen source is integrated, create one unresolved Operator-owned Challenge using the pre-index implementation (present in this branch's base code) and persist it in the Jarvis Git ledger.
2. Deploy the new index-reading code without rewriting or migrating that existing canonical Challenge. The new index files do not exist for this historical record.
3. Operator TUI `listOperator` returns `[]`, silently concealing the outstanding request despite a healthy verified ledger. An attempt to answer the condition by known ID fails at its index-count update, so even manual one-shot feedback is blocked.

**Impact:** Existing pending human decisions are lost from the operator's active queue at rollout. Silent omission is especially dangerous for optional task blocking and requeue coordination; normal response path also fails. This is not merely incomplete historical recent-outcome listing.

**Required fix:** Add a one-time authoritative and CAS-safe materialization/backfill of active operator challenge IDs and the bounded recent projection before enabling indexed reads/writes, with an explicit version/coverage marker so an absent or partially migrated index fails closed rather than claiming the queue is empty. Regression must start with a pre-index ledger containing an unresolved Operator condition, upgrade into frozen source, show visible active request and successful response, and preserve current >4100 archived performance post-migration. Avoid scanning all canonical history on each ordinary TUI poll.

**Evidence status:** Deterministic source-path proof; no migration write is present in this diff or source. Independent tests of new-index-created Challenges do not cover the legacy pre-index state. No reviewer production changes or deployment.
