# CAS property harness source attestation

Reviewed immutable source pin: `5607baf9913f4976add13c984e0182906d4fe8c7`.

Expected Git blobs at this pin:
- `Client/Modules/NativeSchedulerAccountReservation.fs`: `fd9c9591c74ce906e15dfc191c2dbac0995b8dc3`
- `Client.Tests/NativeSchedulerRosterPolicyTests.fs`: `e6988ac42f4191f807b57f04dcb6ddec160b503f`

The property-only integration cherry-picks accepted tests without making the reviewed pin an ancestor of HEAD. Requiring commit ancestry is therefore a topology restriction, not a proof that the tested source bytes are different. `prepare.fsx` requires the pin to resolve, checks both HEAD blobs against the reviewed pin, and now checks both working-tree file bytes via `git hash-object` against those blobs. It refuses pre-existing generated output. An absent pin fails closed at `rev-parse`.

**Scope limitation:** `PropertyHarness.fsproj` references `Client/Client.fsproj`. Consequently the compilation includes additional production modules not individually pinned by this harness. The source/fixture checks attest the specific CAS module and original fixture, **not** every dependency in the Client assembly. The harness cannot establish that no other unreviewed production code could execute. Acceptance must explicitly assess this boundary; do not treat a passing CAS suite as a blanket attestation of the whole Client assembly. No production modules or main checkout were edited.

Tests: positive `dotnet fsi --exec prepare.fsx` succeeded on accepted-only composition; negative wrong control blob failed `SOURCE_PIN_MISMATCH`; repeated prepare with generated output present failed `GENERATED_PATH_NOT_EMPTY`. Release property suite outcome is recorded in the task checkpoint/review request.
