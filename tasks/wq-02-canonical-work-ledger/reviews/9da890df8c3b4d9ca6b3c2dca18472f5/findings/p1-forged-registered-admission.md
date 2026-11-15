# P1 — Caller can admit a launch already Registered, bypassing first-connect and account token provenance

**Frozen source:** `71fc78d051d6509596389abfa8fe7cb5f444cd1f`

**Evidence:** `Client/Modules/WorkQueueGitLedger.fs:189-251,327-365`; inherited `Server/WorkQueueContracts.fs:404-431,621-631,641-675,734-781`.

`AdmitProject` delegates the **entire caller-provided Launch record** to `Contracts.admitProject` without validating its initial state. That contract checks only duplicate ID and injective match against available work. `liveUnclaimed` includes an already-Registered launch with `EverClaimed=false`. `structurallyCoherent` checks launch ID/project and references, but not that admission began `ReservedUnregistered` with no lease/claim/session/dispatch.

**State-transition counterexample:** Publish eligible Planning stage P. Call `AdmitProject` with launch L carrying `State=Registered("forged-uuid","forged-session")`, `Lease=Some { Generation=1; StopRequested=false; LastSeenAt=now }`, `EverClaimed=false`, `PrimaryWorkId=None`, `DispatchCount=0` and an arbitrary `AccountTokenId`. It is matched and committed. `ClaimNext L` then sees Registered+non-stopped lease and atomically claims P, although no Reserved -> Submitted -> first connection sequence, authenticated session, or account reservation occurred. This is a valid latest-head CAS, not an optimistic-concurrency race; replay protection cannot help.

**Required correction:** Validate canonical initial launch shape at the project admission boundary: ReservedUnregistered; no lease, credit, primary/ancillary claims or ever-claimed flag; initial version/dispatch count; exact role/project/token identity. Reject any purported Registered/Submitted/terminal launch at admission. Do not turn `AccountTokenId` into authority without the separately verified account reservation; until account-global proof exists, keep the adapter explicitly unusable as live admission authority. Persisted-state coherence should detect impossible launch phase histories. Add forged-Registered/Submitted/terminal admission negatives and true two-client claim tests; preserve legitimate first-connect and replay receipts.

**Basis:** Direct, deterministic source-path counterexample; no behavioral test for forged initial launch exists in the reviewed 12-test suite.