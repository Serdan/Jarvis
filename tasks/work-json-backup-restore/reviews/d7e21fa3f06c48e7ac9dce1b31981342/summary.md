# Reviewer 1 — F1a RED-only independent review
Verdict: Accepted strictly as isolated RED-first test contract, NOT for integration, production restore, or GREEN readiness.

Frozen commit f4c103940a420d5845ef95b0c9657b4f3d9f05d9 versus baseline 3773830282ed449fb224af3fa1264ecaaf34e83b: precisely one file Server.Tests/WorkSqliteBackupTests.fs changed (+215 lines), git diff --check clean. No implementation or host modifications.

Eight new tests pin two-project/full-domain inverse, source RepositoryId/ref/commit, B-admissible 200-character identifier, Prepared effect identity, self-consistent tampered backup root rejection, persistent recovery fence, verifier/reservation/role/source retention, and unsupported future manifest format. Crucially comments identify raw caller-supplied B JSON as untrusted, and tests are marked F1aRed rather than falsely passing it off as an authenticated B schema.

Evidence limits: dotnet test on the **main checkout**, not frozen review branch, built but matched zero tests under FullyQualifiedName~F1a; this gives NO execution proof for the review commit. Frozen change verified by source diff plus git diff --check only. No compilation or native/remote acceptance claimed.

GREEN/integration blockers remain: Worker20 typed authenticated B provenance/verifier model, trusted backup root, persistent G1b recovery fence, lossless sequence/receipt/audit/account roundtrip and no plaintext scheduled bearer/manual grants. Before implementation acceptance run real frozen .NET11 Release checks and negative-path validation. Preserve F1b/restore/Scheduler/deployment NO-GO.