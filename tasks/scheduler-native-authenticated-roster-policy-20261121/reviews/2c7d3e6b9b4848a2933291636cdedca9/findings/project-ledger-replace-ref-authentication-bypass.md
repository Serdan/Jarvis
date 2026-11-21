# P0 — Local Git replace ref can spoof a signed roster's project ledger identity

**Review:** `2c7d3e6b9b4848a2933291636cdedca9`. **Frozen source:** `d6bf6aae44ea2cc348bd1c4a1f906ff6bdd85606` (parent `d702352ef705f9991ac1cb14dc15a13cce78aa44`).

## Defect

`Client/Modules/AccountCapacityLedger.fs` `verifyNativeProjects` calls `LedgerStore.fetch b` and treats its `snapshot.Header.ProjectId/LedgerId/Epoch` and `snapshot.Commit` as independent proof that the approved *remote* contains the matching project ledger. But the underlying `Client/Modules/LedgerStore.fs` Git runner (`execute`, lines 400–451; `showBlob`/readHeader lines 609–625; `fetchWithRetries` lines 649–710) does **not** disable Git replacement objects. Its fresh remote fetch obtains the real SHA, while subsequent local `git show SHA:ledger.json` follows a clone-local `refs/replace/<SHA>` to a different ledger header. AccountCapacityLedger's new `GIT_NO_REPLACE_OBJECTS=1` protects only its own account Git commands, not this distinct project Git reader.

## Independent source-bound actual reproduction

Executed an ephemeral F# script referencing the **frozen Release** `JarvisClient.dll` against a newly created disposable bare project remote and separate client clone (Jarvis job `9d3adc3608794abb8170a49be9233549`, actual FSI exit 0). Without any replacement, fetching with a deliberately incorrect `LedgerId` returned `LedgerIntegrityError "Ledger header identity, epoch, schema or status mismatch."`. Then constructed a separate local root commit containing the false ledger ID, installed `git replace <actual-remote-ledger-commit> <forged-local-commit>` **only in the client's clone**, and repeated the same `LedgerStore.fetch`.

Actual output: `CORRECT_ID_FETCH=OK`; `WRONG_ID_BEFORE=REJECTED`; `LOCAL_REPLACEMENT_VISIBLE=true`; `WRONG_ID_AFTER=ACCEPTED`; `REMOTE_OID_UNCHANGED=true`; `P0_REPRODUCED=true`. The bare remote's authoritative `refs/heads/jarvis-orchestration` OID was not modified; only the local clone interpreted it differently. The temporary fixture was deleted afterward, and the frozen source checkout was not edited.

## Security consequence

A signed account policy and supplied complete project bindings can match a *forged local reading* of a foreign or stale project ledger, permitting `readVerifiedDisposableNativeRosterPolicy` to return `MembershipComplete=true` and `PolicyComplete=true` even though that identity is absent at the real approved project remote. Production dispatch is correctly disabled today, so this is a **P0 policy-proof correctness blocker**, not evidence of live launches. The task explicitly requires real remote/epoch identity proof and fail-closed foreign-project behavior; the positive verifier is not sound.

## Required correction and adverse gate

Ensure every evidence-bearing project Git command in this path ignores local replacement objects (`GIT_NO_REPLACE_OBJECTS=1`) and cannot be spoofed by local grafts, object alternates, or inherited Git metadata redirects; coordinate any shared `LedgerStore` change with its owner, or use a narrow independently isolated native-project verifier. Do not accept a projected `PolicyVerified` solely from a normal `LedgerStore.fetch` when those trust prerequisites remain unproved. Add a fresh-clone/disposable-bare-remote adversarial fixture with an authentic signed account policy, one forged local project replace ref, unchanged actual remote head, and assert `NotAuthoritative`/false completeness. Keep WQ07 account semantics and disabled production entrypoints unchanged.