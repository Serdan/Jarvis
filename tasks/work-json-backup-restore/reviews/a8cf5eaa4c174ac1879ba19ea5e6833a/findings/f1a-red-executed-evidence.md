# Worker 13 F1a native RED-first evidence — test contract only

**Frozen source:** `f4c103940a420d5845ef95b0c9657b4f3d9f05d9` on published `refs/heads/feature/work-json-backup-f1a-red-w13-20261117`, based on current origin/main. Exactly one file changed: `Server.Tests/WorkSqliteBackupTests.fs` (+215 lines). `git diff --check` clean, worktree clean. No production code, scheduler, recovery, import, F1 serializer, host, or Git publisher edits. Source is intentionally **UNMERGED**.

**Native SDK/positive baseline:** dotnet 11.0.100, `dotnet restore Server.Tests/Server.Tests.fsproj`, `dotnet build Server.Tests/Server.Tests.fsproj -c Release --no-restore` => PASS. `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-build --no-restore --filter TestCategory!=F1aRed` => **489 PASS, zero fail, zero skipped**, including unsupported-future-format fail-closed case.

**RED run:** `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-build --no-restore --filter TestCategory=F1aRed` => **7 discovered / 7 executed / 7 expected FAIL** (not skipped; no compiler failures).

| RED test | Concrete currently observed failure | Future boundary |
| --- | --- | --- |
| `f1a_red_full_e0_g1_domain_and_account_state_require_lossless_two_project_restore` | `No trusted, credential-free projection for SQLite body` | Complete authenticated two-project B/E/G bodies, account capacity, receipt/audit/sequence, exact re-export before fence |
| `f1a_red_original_repository_ref_and_commit_must_survive_typed_export_and_inverse` | `No trusted, credential-free projection for SQLite body` | Original verified RepositoryId/ref/SHA, not reconstructed from SHA or backup path |
| `f1a_red_existing_b_admissible_200_character_id_cannot_disappear_from_snapshot` | `Unsafe or unsupported record identity` | Existing B-allowed ID parity, no silent skip or truncation |
| `f1a_red_prepared_external_effect_keeps_original_operation_identity_and_fence` | `No trusted, credential-free projection for SQLite body` | Prepared effect original operation and uncertainty preserved |
| `f1a_red_scheduled_verifier_needs_retained_reservation_role_permission_and_source_binding` | `No trusted, credential-free projection for SQLite body` | Verifier SHA256 plus true reservation, permission, role and original source scope; no bearer or manual grant export |
| `f1a_red_rehashed_valid_json_from_untrusted_backup_root_cannot_authorize_restore` | `Unsafe backup/restore unexpectedly succeeded` | Checksum alone is not a trusted Git/repository/commit restore witness |
| `f1a_red_stale_restore_must_persist_a_boot_fence_before_any_new_commit` | `Stale restored SQLite remains writable before persisted Recovery fence` | Persist Recovery lock before host admission and dispatch |

**Limitations and design dependency:** RED staging fixtures currently pass arbitrary B `BodyJson` through its existing permissive API. They establish known rejection/leakage *gaps*, but cannot count as authenticated positive future GREEN. Worker20 independently owns `work-trusted-sqlite-domain-provenance` closed B ingress/schema and verified original source+credential origins. Replace provisional raw positive fixtures with exact accepted typed constructors only after independent B review/integration. G1b owns post-restore persistent Recovery admission fencing. Physical F1 file/directory fsync, atomic publication and async retry were already independently accepted; not modified or re-certified here.

**Decision requested from reviewer:** assess test correctness and provenance as an **authored + executed RED-FIRST CONTRACT ONLY**; do not accept full-domain serializer/restoration, plaintext secret safety, Git backup authenticity, live deployment, Scheduler, F1b GREEN, or integration of intentionally RED tests.