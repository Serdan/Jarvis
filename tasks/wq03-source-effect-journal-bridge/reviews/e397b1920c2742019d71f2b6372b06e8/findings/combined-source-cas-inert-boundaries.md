# Reviewer 1 — independent combined-source findings

**Review** \`e397b1920c2742019d71f2b6372b06e8\`  
**Exact frozen source** \`53ab33a70446f9e2873ab5d5be4006a0c261322d\`  
**Verdict** **ACCEPTED — strictly for inert combined-source integration; NOT activation authorization.**

## 1. Source identity and ancestry (verified)

- \`git show -s --format=%H %P\` reports merge head \`53ab33a70446f9e2873ab5d5be4006a0c261322d\`, first parent \`4f83130173d7758304fe7c0602897bdfc425dda6\`, second parent \`273fd0b9dc73558f8c64fac28e24662c2872801a\`. \`git merge-base --is-ancestor\` returned 0 independently for both parents and the submitted base \`e26a572b25f9ba829f1014f2b5ca36fd77900e85\`.
- First parent is the exact WQ07-on-WQ03 combined source, separately reviewed **Accepted** by Reviewer 3, review \`1d4384f3a93149f0bd14a2acf195feda\`, decision sequence 3509. Second parent is the exact independently **Accepted** inert source journal, review \`ee2e8859bcdd48649a23d5bbc2764770\`, decision sequence 3500.
- Relative to the first parent, changed paths are only \`Client/Modules/SourceEffectJournal.fs\`, \`Client.Tests/SourceEffectJournalTests.fs\`, \`docs/specs/wq03-source-effect-journal-bridge.md\`, and both relevant \`.fsproj\` files. Direct \`git diff --exit-code 273fd0b9 53ab33a --\` of the three accepted content files returns 0: implementation, regressions and limitation document are **byte-identical** to the independently accepted journal.
- Both F# compile entries occur after LedgerStore and before downstream WorkQueueGitLedger/Account modules as required by their dependency ordering. The only other project-file addition is \`Microsoft.Data.Sqlite 11.0.0\`. \`git show --remerge-diff 53ab33a\` produced no unresolved conflict; \`git diff --check 4f831301 53ab33a\` exited 0.
- Independently re-inspected the WQ07 first-parent conflict resolution: \`WorkQueueGitLedger.execute\` retains WQ03 \`sourceProof\` followed by \`validatedContent\`, then composes WQ07 \`witnessChanges\` into the **same Git-CAS publication**. This matters: preserving the source handoff gate must not drop original admission/terminal witness changes or make them second, non-atomic writes.
- Review checkout \`lead-wq03-effects-on-wq07-candidate\` was verified **clean** at exact \`53ab33a70446f9e2873ab5d5be4006a0c261322d\` after the local Release run. The Git ref surfaced by the canonical review details is the exact head above; this decision does not rely on a moving task-branch tip.

## 2. Ledger CAS and account admission preservation

- \`AccountAdmissionAdapter.admit\` calls account-global \`AccountCapacityLedger.reserve\` **before** attempting project \`WorkQueueGitLedger.execute(..., AdmitProject ...)\`; it independently re-reads the held token and binds project ID, ledger ID/epoch, approved remote authority fingerprint, launch and role/version. Unknown global outcomes return \`GlobalReservationUncertain\`; unknown project head or CAS outcomes return \`TokenRetained\`.
- \`abortUnbound\` uses an exclusive project-CAS C01 absence tombstone against original admission; a winner cannot silently override the other. Failed/uncertain abort does not release the account token. Terminal release invokes \`VerifiedProjectTerminalReceipts\` on an independently fetched canonical project head, checks origin operation, unchanged immutable witness blob, expected launch/token/role/version, and only then attempts account-side release. Same-ID foreign or overwritten witnesses cannot provide proof.
- The combined addition **does not edit** the accepted first-parent account adapter, account ledger, or WQ03/WQ07 core ledger logic. The CAS boundary is preserved rather than replaced by source-journal local state.

## 3. Source-effect bridge and fail-closed activation

- \`SourceEffectIntent\` binds the **original caller-stable operation UUID** and request SHA-256 to project and ledger identities/epoch, repository SHA, approved remote fingerprint, source ref, checkout ID, work ID, launch UUID and claim generation. \`beginBeforeDispatch\` commits that UUID with \`Prepared\` and a sticky \`RecoveryBlocked\` fence before the one permitted callback. Replay/restart or UUID reuse with changed scope/payload cannot redispatch. Concurrent client processes serialize through SQLite transactions.
- Host-local persistence is SQLite rollback journal with \`synchronous=EXTRA\`; first database creation syncs the parent directory on POSIX. Corrupt, missing or inaccessible journal produces an error rather than fabricated completion. Callback exception and lost remote acknowledgment retain \`Uncertain\` for the original UUID.
- Critically, the standalone module has **no production callers**: \`git grep\` finds its APIs only in its tests and its project compile inclusion. \`inspectForHandoff\` unconditionally returns \`RecoveryBlocked=true\`; even a synchronous file edit may yield \`AllKnownOperationsTerminal=true\` but cannot authorize source release while route coverage is missing. Listed uncovered paths include RunCommand, StartJob, RunProjectTask, WriteFile, PatchFile, ImportFile, GitCommit, GitPush, GitMerge, Retry and legacy/out-of-band effects. No terminal attestation API for detached jobs, pushes or merges exists. No production Scheduler/admission hook is newly activated.
- WQ03's separate **canonical** \`MarkSourceRecoveryBlocked\` transition is not implemented by this local journal and remains a future bridge requirement. Host-protected, create-only/nondeletable/retained \`jarvis-wip/**\` refs and proof for actual host credentials/bypass/GC likewise remain external prerequisites. A local SQLite row or clean checkout is not remote or process-tree terminal proof.

## 4. Independent executable evidence

At the exact clean review checkout (not the main working tree), local SDK **11.0.100**, command:

\`\`\`text
dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter "FullyQualifiedName~SourceEffectJournalTests|FullyQualifiedName~AccountAdmissionAdapterTests|FullyQualifiedName~AccountCapacityLedgerTests|FullyQualifiedName~WorkQueueGitLedgerTests" --logger "console;verbosity=normal"
\`\`\`

Jarvis local job \`1b0340910aad41baaaaf16fd7da87e4d\` **Completed, exit 0, 63/63 NUnit tests passed**. Both Client and Client.Tests compiled targeting net11.0. Executed cases cover concurrent account reservation, C01 project CAS races, immutable witness origin and terminal receipts, unknown/lost-ack token retention, source handoff negatives, source journal original UUID replay/restart, independent SQLite clients, lost original push, corrupt store, and two Git clone fixture paths. A pre-existing F# disposable-constructor warning is not a test failure. No hosted GitHub Actions were used.

**Evidence limit:** 63 selected tests do not establish a full-suite pass or real host-side enforcement. In particular, the journal's two-clone test creates real clone directories but uses manually supplied checkout IDs rather than proving authenticated host generation/dispatch coverage. Its simulated restart and callback exceptions are not a complete process-kill/fault-injection campaign. These are **activation blockers**, not new regressions in this inert merge.

## 5. Explicit verdict and integration dependency

**ACCEPTED for the exact frozen 53ab33a commit as an inert/additive merge only.** I found no changed-source regression or activation bypass in the reviewed delta, and the separate WQ07 parent is now Accepted. However its acceptance does **not** prove that it has been integrated onto the canonical main branch. Lead must preserve exact ancestry and verify canonical integration order and any later main advancement before integration.

This verdict **does not** approve positive source \`YieldSource\`, new successor claims, token release based on local journal records, live Scheduler/admission activation, host instrumentation claims, host WIP ruleset compliance, migration, production deployment, main push, or any hosted Actions. Before activation, obtain independently reviewable authoritative route coverage, original-operation remote/process-tree terminal evidence, durable canonical RecoveryBlocked transitions, and actual retained/immutable host WIP protection, then submit a fresh frozen review. No Codex was used.