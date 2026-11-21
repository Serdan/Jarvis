# P0: Completion receipt cannot prove non-force target publication or preserve a racing target head

**Review:** f5c18f4707cb4d72acdc8742a418e1f6  
**Frozen candidate:** 1317089967fc2907527d8899f6e8a513d3ffd751  
**Disposition:** ChangesRequested — target-concurrency and publication-provenance gap

## Source and missing proof

`Client/Modules/ReviewWorkflow.fs:1320-1368` receives only `integratedCommit` at completion. `LedgerStore.verifyIntegrationOnRemote` at `LedgerStore.fs:2233-2252` fetches the *present* advertised remote target and checks `submittedCommit <= integratedCommit <= remoteHead`. For a distinct mechanical merge, `LedgerStore.verifyMechanicalIntegrationMerge` only requires the first parent to descend from the `TargetHeadAtSubmission`. No durable record or trusted receipt proves **what target head existed immediately before publication** or that the push was a non-force fast-forward. The runbook warns that Git ancestry cannot attest push flags and asks for external host protection, but the explicit managed-namespace protection section does not establish target-branch no-force protection as a checked precondition.

## Independent disposable bare-remote witness (Git 2.47.3)

Build immutable reviewed source S from base B. Independently advance target to T1; submit S with `TargetHeadAtSubmission=T1`. Produce the exact clean two-parent mechanical merge I=(T1,S). Before publication, target advances to T2 (descends from T1). An ordinary non-force push of I is correctly rejected. **Force-push I** to the fixture bare remote, discarding T2. The present advertised remote ref is I, and all of the candidate's completion conditions still hold:
- S is an ancestor of I, and I is an ancestor of advertised remote head I.
- I has target first parent T1, which descends from captured submission target T1; source second parent S exactly.
- I's tree equals `git merge-tree --write-tree --no-messages T1 S`.
- T2 is not an ancestor of the advertised head; the raced target work was discarded.

The bare-remote fixture has no target branch protection (as expected for a negative test), and the reproduction is of the predicate sequence rather than a Jarvis end-to-end call. This is sufficient to show that current `CompleteIntegration` cannot distinguish compliant publication from a forced rewind of an unseen target advance.

## Required resolution

Enforce or verifiably attest the approved final non-force FF rule at the publication boundary: e.g. a scoped publisher with durable pre-push target OID, ordinary non-force push outcome and fetched post-push receipt, combined with verified target-branch host protection / no-force enforcement. If this capability lies outside the present ledger, **fail closed for promotion** and make the outstanding external proof an explicit gate; don't call ancestry-only `CompleteIntegration` proof of non-force publication. Add a real bare-remote negative test for T1 submission / racing T2 / forced old merge I, and positive non-force T2 recomposition.

The new positive test already demonstrates the normal stale non-force rejection, which is useful but does not protect the system if the wrong publisher or a force-capable credential bypasses that procedure.