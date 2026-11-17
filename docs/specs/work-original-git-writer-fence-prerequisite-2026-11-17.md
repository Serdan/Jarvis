# C3 prerequisite proposal — authoritative original-Git writer admission and quiescence

**Status:** Proposed engineering dependency for independent review; **not approved, deployed, enabled, or a migration GO**.
**Parent:** `work-verified-git-sqlite-import` (C3).
**Base:** integrated C2 source `9c290e299faf7ee40061f262f682c36b30d0771a`, accepted C2 `528b72659a6865aa1b8bb47875a2c4bbb4fdd4a9`, review `d4a29f0b7c9a4b5db14746b2f8ae1974`.
**Scope of this document:** explain the missing source-authenticated protocol; propose a separately reviewed dependency rather than constructing a fictitious positive fence receipt.

## 1. Reproducible blocking fact

The C2 contract in `Server/WorkLegacyImportFence.fs` contains `IWorkLegacyOriginalGitFenceGateway` but no production adapter. The positive implementation `commitFencedOfflineFixture` is restricted to disposable SQLite paths and obtains quiescence, pending-intent high-water and source-effect high-water from a fixture gateway. Equality of returned records cannot prove those external facts.

The original writer remains separately capable of publishing to Git:

- `Client/Modules/LedgerStore.fs`, `transact` (approximately lines 2180–2290): a non-force original-ledger push after fetching/planning. There is no mandatory remote-enforced barrier consulted by every caller at the actual publication boundary. The planner is also bypassed when an operation UUID is already applied.
- `Client/Modules/AccountCapacityLedger.fs`, `transact` (approximately lines 610–685): an independent account-CAS push path. Certain account operations check `MigrationPhase`, but account phase alone does not fence every original project writer.
- `Client/Modules/LegacyWriterProtocolFence.fs`: a host-selected local binding; `readHostScope` returns `Ok None` when `JARVIS_WQ10_LEGACY_WRITER_FENCE_BINDING` is not set. The checked scope is **not evidence of every other installed client** or of an atomic barrier at the remote push.
- `Client/Modules/LegacyDirectWriterFence.fs`: some direct workflow writes wrap the original transaction and independently inspect account/project heads, but its own `inspectHighWater` explicitly returns `Unknown` for the original pending-intent journal, source-effect journal and disconnected-client/protocol inventory. These facts cannot be replaced by `LedgerStore.Header.NextSequence`.

Consequently, a new *Server-only* adapter cannot exclude a second, unchanged device with valid Git push credentials. A local lock, receipt hash, signed statement from one client, polling loop, or check-before-push can all race or be bypassed. **Do not implement `IWorkLegacyOriginalGitFenceGateway` as a positive adapter until the old writer protocol can supply enforceable evidence.** Existing Git-backed ledgers remain authoritative.

## 2. Proposed independently reviewed prerequisite task

**Proposed title:** `[P1][Stateful Work/C3 dependency] Remote-enforced original Git publisher admission and all-client effect journal`.

**Ownership boundary:** new reviewed original-writer transport/protocol changes and matching disposable multi-client fixtures. No direct edits to E Scheduler, F backup, G Recovery, C0/C2 accepted importer, or live host configuration in this task. This work may need an explicitly approved compatibility change to the original `LedgerStore` and `AccountCapacityLedger` publication paths; that exception must be reviewed separately from import C3.

**Acceptance contract:**

1. **Single enforceable publication authority.** Identify the account and every original project repository/ref by canonical project ID, ledger ID, epoch, approved Git remote identity, and source device. All legacy Git publication channels, including direct task/review/challenge/Manager mutations, implicit inbox activation, old UUID replay, account capacity, project conversion, review/source-ref side effects, and recovery attempts must be under a common admission protocol. Bypass clients with still-valid old credentials must be *remotely prevented from pushing*, not merely told to consult a local flag. If current Git hosting cannot supply this guarantee, document the required server-side publishing gateway, branch rules/credential revocation, or alternate authorized remote enforcement and stay NO-GO.
2. **No check/push gap.** Publication admission is linearized with its corresponding remote CAS or rejected by the remote authority. Acquiring the durable exclusive barrier fences **all future** original-writer admissions across every client, including offline/stale versions and outstanding requests. Reads and explicit recovery remain possible, but no legacy mutation or original source side-effect may bypass admission by changing its command classification. A lost response or process restart retains the barrier and unknown operation until reconciled.
3. **Complete and authenticated witness.** Capture original account/project/ref/device identities, monotonic barrier epoch, authenticated publisher protocol/credential version coverage, last permitted operation UUID + request hash, original pending-intent journal high-water, and original source-effect journal high-water from the authority that actually observes/adjudicates publications. Missing enrolled clients, unrevoked credential paths, unknown effects or unavailable remote snapshots cause a typed NO-GO, never zero or `true` defaults. One human project name on two devices must not alias a canonical approval.
4. **Independent verifier.** The C3 adapter rereads the real original Git remote refs/heads and trusted admission authority independently of the caller. Verify the exact pre-fence heads, all account/project ref epochs, a durable barrier receipt and anti-replay operation ID. Compare every observable newer Git commit to the admitted high-water; stale, forged, missing, divergent, or untrusted heads are errors. Do not confuse a Git CAS success with confirmation of all-client quiescence.
5. **Crash and reconciliation.** Barrier acquisition, each account/project fence CAS, pending publisher admission, source publication, and SQLite import have durable states and idempotent same-UUID retries. At every ambiguous phase, publication and SQLite promotion remain fenced. No automatic rollback of the original fence after a partial import.
6. **Constrained integration.** A source-only, default-off registration seam may implement `IWorkLegacyOriginalGitFenceGateway` only after the above has an actual authenticated dependency. No MCP host route, scheduled executor, production deployment, real ref freeze, live ledger import, or SQLite authority cutover in this acceptance.

**Hermetic tests:** two original project bare Git remotes and one account bare Git remote; two independent registered clients plus a stale/unregistered clone; simultaneous pre-/post-barrier CAS; outstanding and lost-response push; alternate source-ref publication; same-name distinct canonical/device authorities; revoked/unrevoked credentials; forged/wrong refs and high-water; mismatched epoch/version; disconnected client; restart and replay during each phase; unknown effect journal; and partial account/project import rollback. A stale clone must demonstrably fail to publish *at the remote*, not only at an SDK guard. Full .NET 11 Release suites and independent exact-SHA review required.

## 3. C3 disposition and continuation

Do not fabricate `WorkLegacyGitFenceReceipt.AllOriginalClientsQuiesced = true`, `NoNewOriginalMutations = true`, a SHA-256 witness, or a minimum protocol version when the authentic original publisher cannot prove those values. C2's accepted disposable tests are necessary but do not establish this guarantee.

After the proposed transport/protocol prerequisite is reviewed and integrated, resume C3 in separate additive `Server/WorkLegacyOriginalGitFence*` modules behind `IWorkLegacyOriginalGitFenceGateway`; bind trusted original Git evidence, rerun cross-device/restart/fail-closed tests and submit a fresh independent review. Any operator cutover remains a separate explicit GO/NO-GO, not a side effect of this proposal.
