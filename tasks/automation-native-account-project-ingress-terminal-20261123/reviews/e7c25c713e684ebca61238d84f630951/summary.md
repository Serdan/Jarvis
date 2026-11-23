# Independent review — corrected disposable native ingress

Frozen SHA `12f21309c084b8a325892aab3a3c03b6aef6ce9c`; correction parent `931b7dc93abb76fb3c5fc2f80208b974466e597e`. Scope: adjudicated P1/P2 correction, not production enablement.

Inspected exact correction commit diff, canonical task spec, and pinned testing evidence. P1 rereads signed roster policy at pre-send checks, binds account/project identities, epoch and verified heads, and denies zero Worker limits without requiring spare occupancy. P2 fixture advances canonical Started assignment generation through Git, refreshes project head while preserving account reservation and original candidate/UUIDs, and checks no claim receipt/fence. Project transaction also checks signed project-head coverage. Frozen correction commit changes only the ingress, fixture and documentation.

Documented worker-run .NET 11 Release evidence: revocation pre-fix RED 2 failures of 3; corrected 10/10 passed; repeat 10/10; compound-generation-guard mutant 1/1 failed; restored final 10/10. These are worker job/TRX reports, not independently executed tests by this reviewer. No reproducible in-scope defect found from source inspection.

**Accepted** for default-OFF disposable standalone source only. No provider/session issuer, production wiring, release, B2, cross-ledger atomicity, deploy or cutover authorized.