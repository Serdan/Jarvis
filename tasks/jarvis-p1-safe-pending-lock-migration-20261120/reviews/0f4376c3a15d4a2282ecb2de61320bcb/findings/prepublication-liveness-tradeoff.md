# Nonblocking liveness/compatibility risk: prepublication pending UUIDs now require inspection

**Frozen SHA:** `bfde56bcc79cedf923d8eaff7ea66cd0af488f23`.

The safety correction intentionally refuses ordinary same-UUID callback replay whenever the journal already contains a pending command. This includes a prepublication DNS or directory-fsync failure, even if an in-memory fixture knows no Git push was attempted. Such observations are not durable across process crashes; refusing ordinary automatic retry is the correct conservative behavior for this security-focused review.

**Remaining operational cost:** `PendingRecoveryApi.retry` allowlists only selected TaskWrite/ReviewWrite actions. `YieldAgent`, `ResumeAgent`, and several other actions are absent, so a recoverable transient failure can leave a durable pending journal that cannot be replayed through the current operator retry API. Correction may require a separately authorized, positively proven supersession, or a future crash-durable phase/no-effect proof that permits a narrower retry. Neither a missing canonical receipt nor mutex owner death authorizes blind replay.

This is **not** evidence that ordinary automatic retry should be reinstated. It is a deliberately documented liveness and compatibility tradeoff to triage independently; the Linux safety verdict does not approve a new recovery policy or close operational recovery work. Existing tests updated to require inspection are consistent with the stated fail-closed safety contract.