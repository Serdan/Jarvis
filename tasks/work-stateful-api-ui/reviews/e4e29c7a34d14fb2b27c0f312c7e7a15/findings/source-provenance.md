# Finding 2 — Source commit provenance disappears at the client projection boundary

**Severity:** Scope-completeness / provenance (required for H1a's stated source-provenance contract).  
**Frozen source:** `ce66e7a1344768086f06c590857603f93c0bd81e`  
**Location:** `Server/WorkStatefulApiContracts.fs` (`WorkReadStage.SourceCommit`), `Server/WorkStatefulApiRead.fs` (`SourceSha` read), `Client/Modules/WorkStatefulTransport.fs:100-114,160-202`.

The server correctly includes the persisted source SHA in `WorkReadStage.SourceCommit`. The client converts `WorkReadStage` to H0 `WorkStage` without retaining `SourceCommit` (or any source-provenance diagnostic). `ReadProjection` returns only the lossy H0 snapshot plus generic diagnostics. Consequently downstream read-only consumers cannot determine which exact source commit was recorded for the shown stage, even as **reported, not independently verified** provenance.

The pinned H1a spec expressly calls for preserving source provenance. The existing `ReviewStage.Unknown` and `IntegrationStage.Unknown` mapping is appropriate because a raw SHA is not an accepted decision; this finding does **not** request promotion of bare SHA fields into verified review/integration outcomes.

**Required:** Preserve the raw/validated source SHA in a typed, explicitly non-authorizing and non-attesting read-only field or stage detail, while retaining the UNKNOWN status of independent review/integration decisions. Reject or diagnose malformed source SHA. Add a client transport test that the source SHA survives the server-to-view boundary and cannot be mistaken for acceptance.