# B04 — P1 — Multi-ledger migration cannot rely on an unstated atomic cutover

**Frozen source:** `36155553147705748c90d73a5aaf497b511768ef`, lines 33–43, 117, 331–344, 378–379, 386, 392.

**Finding.** Section 14 inventories every project ledger plus one global admission ledger, insists that a failed conversion keeps the old schema authoritative, and says rollback must not overwrite post-cutover work. Those are correct targets, but there is no defined **publication/activation boundary** for a migration that necessarily writes multiple independent Git ledgers. A checked Git commit per project is not an atomic cross-project migration. Writer handshakes and a freeze are necessary, but not by themselves a durable protocol for mixed prepared/active states after a crash.

**Interleaving.** Projects A and B and the account admission ledger are frozen for conversion. A's queue/attention indexes and converted records are committed. B's conversion fails or its push reply is lost. A client restarts before recovery. Without a durable, versioned cutover phase/epoch and read-side gate, it can expose A's new queue semantics while B and the global ledger are still on the old protocol; alternatively, reverting A's branch can erase or conflict with committed evidence. "The old schema remains authoritative" cannot be established by local process state alone.

**Required design change.** Specify a fail-closed staged migration protocol across scopes (for example: global migration epoch with `Frozen/Preparing/Validated/Activated` states, per-project prepared receipts and explicit read/write admission gates, or an equivalent strictly ordered per-project activation model). Define which schema is authoritative in each state, require a verified fully prepared set before admitting new queue launches, and define replay/rollback after every partially completed push. Old clients must be fenced before any incompatible writer commits; new clients must refuse to expose or mutate partially activated scopes. Never roll back by erasing immutable ledger history. Preserve source/ref/checkpoint audit data and explicit operator activation authorization.

**Acceptance fixtures.**
1. Crash after converting A but before B/global validation: no new queue dispatch and no partial new-schema availability exposed; idempotent resume or safe abort.
2. Lost receipt for one project conversion and concurrent old-client write: verified head/epoch determines reconciliation; no guessed failure or mixed-format write.
3. Account-global ledger prepared but project still old: Manager scheduler cannot admit a new launch until both sides agree on active protocol epoch.
4. After legitimate new-schema activation and work commits, rollback never force-rewrites them.

**Severity:** Blocking data-consistency/cutover contract. Does not authorize migration now.