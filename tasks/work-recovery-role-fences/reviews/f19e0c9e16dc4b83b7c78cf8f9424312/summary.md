# Review: Changes requested (G1b RED-FIRST tests only)

Frozen commit `b6a35bf529d95b614b0ce0a7c7c7d1546b539478`, base `51b67bf1d5ba7e16512b915f8004456440b3489c`. Scope: tests only; no source implementation or merge authorized.

Blocking: `Server.Tests/WorkRecoveryRestoreBootFenceRedFirstTests.fs:57-61` treats `Error _` from restored authority as success, so unrelated version, validation, storage, or schema errors may falsely prove RecoveryRequired gating. Assert the specific typed recovery-fence rejection, and show otherwise identical fresh admission succeeds against an intact authority. Record explicit RED-first evidence from the frozen commit.

Verified `git show`: two files, 77 insertions; production files unchanged. The restored-vs-intact restart scenarios are appropriate. Attempted targeted `dotnet test` from active checkout, but NUnit discovered zero matching tests because the checkout lacks this frozen commit; **no runnable RED/GREEN outcome established**. Do not treat that run as validation.

Decision: ChangesRequested for discriminator/test proof, not a production-code judgment.