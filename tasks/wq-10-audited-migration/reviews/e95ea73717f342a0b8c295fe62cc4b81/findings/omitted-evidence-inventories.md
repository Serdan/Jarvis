# P1 — Missing external-evidence inventories silently become empty

**Frozen source:** `88863eead9c763e984bd34d3111812f67fd7ac2a`  
**Location:** `tools/Wq10MigrationPreview/Program.cs:135,146-147`

The manifest's three nullable inventories are coalesced with `?? []`: `pendingOperations`, `inFlightSourceJobs`, `uncertainBrowserSubmissions`. If all three are omitted while the three operator-supplied gate booleans are true, the inspector reports `PREVIEW_READY_FOR_REVIEW`, empty lists and no evidence-inventory warning. This makes an omitted/uninspected journal indistinguishable from an explicitly audited empty journal, contrary to WQ-10's incomplete-evidence requirement and the README's contract that **an empty list is an assertion of inventory**.

**Independent reproduction:** disposable two-clone fixture from `tests/test_preview.py`, replace `observations` with only `legacyManagerStopped=true`, `oldWritersFenced=true`, `clientProtocolsCompatible=true`; run CLI. Observed `PREVIEW_READY_FOR_REVIEW` and no blocker for any of the missing lists. No production ledgers accessed.

**Requested correction:** distinguish missing/null from explicitly supplied empty arrays, report a concrete `EVIDENCE_INVENTORY_MISSING` blocker (or equivalent) for each omitted inventory, and add negative fixture coverage. Preserve the operator-supplied/unauthenticated evidence disclaimer: this remains a read-only preview, not cutover authorization.
