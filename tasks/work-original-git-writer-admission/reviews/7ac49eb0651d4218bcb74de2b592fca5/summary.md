# Independent review — Accepted (correction only)

- **Review:** `7ac49eb0651d4218bcb74de2b592fca5`
- **Frozen source:** `10d488d40540ffb709f465cc69b0d8bb711d9c21` on the immutable review ref; base `35f675cc1564710d10fdb2f50a03edff39368a28`.
- **Prior blocker:** `d91838abaed94023ac73627d037dde8e` (ChangesRequested decision 7377), `findings/contention-reentry.md`.
- **Contract:** `work-original-git-writer-admission` task spec v3, strictly default-off; no SQLite migration, live import, dual writers, deploy or operator cutover.

## Independent source verdict

The three-path correction from prior frozen `d91838abaed910235c73627d037dde8e4e3b5f0c` changes `Client/Modules/LedgerStore.fs`, `Client/Modules/OriginalGitWriterAdmissionJournal.fs`, and `Client.Tests/LegacyWriterProtocolFenceTests.fs`. From the task base the existing six changed paths remain within the extended writer/fence/test lease.

The prior nested admission defect is corrected for both `LedgerStore.transact` and `putRecord`: one logical journal permit now spans only bounded replans after an explicitly reported non-fast-forward rejection and a successfully fetched competing head. The journal identity payload is the stable request hash, not each changing proposed commit. An admitted retry never invokes the same UUID's `admit` again. The classifier requires Git's nonzero exit and porcelain `[rejected]` with `(fetch first)` or `(non-fast-forward)`; an advanced remote tip alone is not proof of rejection.

When remote receipt confirms the operation, the journal settles; a demonstrably effect-free rejected series may settle Rejected, allowing STOP to drain. A timed-out, failed/unverified push or failed confirmation retains `unresolvedRemoteEffect` and goes UNKNOWN, with no blind retry and a non-drained STOP witness. Already-applied canonical receipt is replay-safe. The `publish false` preactivation path retains the previous bounded Git contention retry instead of imposing new journal-only restrictions while the migration remains default-off.

I did not identify an outstanding blocker in this exact correction. This **does not** certify entire original-writer source coverage, authenticated ingress/import, Git-to-SQLite migration, deployment, operator GO, or production hosted-remote behavior; the journal's route-coverage witness remains false and all separate downstream gates HOLD.

## Independent execution and provenance

At `qa/reviewer1-w21-10d-20261118`, created as a clean isolated worktree pinned to exact frozen head `10d488d40540ffb709f465cc69b0d8bb711d9c21`, using .NET SDK `11.0.100` and Release:

- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --no-restore --filter FullyQualifiedName~LegacyWriterProtocolFenceTests`: **26 passed / 0 failed / 0 skipped**.
- Overlapping adjacent Client filter covering `LegacyWriterProtocolFenceTests`, `LedgerStoreTests`, `AccountCapacityLedgerTests`: **45 passed / 0 failed / 0 skipped**.
- `dotnet test Server.Tests/Server.Tests.fsproj -c Release`: **488 passed / 0 failed / 0 skipped**.
- `git diff --check 35f675cc1564710d10fdb2f50a03edff39368a28 10d488d40540ffb709f465cc69b0d8bb711d9c21`: **clean**; isolated checkout `git status`: **clean**.

The three added deterministic real bare-Git regressions cover project transaction contention/replan with the same UUID and drain, `putRecord` contention/drain, and timeout plus competing push leaving UNKNOWN and STOP pending without republishing. These are hermetic local Git tests, not hosted GitHub or live migration acceptance.

**Verdict: Accepted for the frozen source correction only; all migration/cutover operator gates remain HOLD.**
