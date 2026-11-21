# P0: Local Git replacement refs can spoof exact mechanical merge-tree proof

**Review:** f5c18f4707cb4d72acdc8742a418e1f6  
**Frozen candidate:** 1317089967fc2907527d8899f6e8a513d3ffd751  
**Disposition:** ChangesRequested — security/provenance correctness blocker

## Observed source

- `Client/Modules/LedgerStore.fs:2259-2302` implements `verifyMechanicalIntegrationMerge` using `git rev-list --parents -n 1`, `git merge-tree --write-tree --no-messages`, and `git rev-parse <integrated>^{tree}` without disabling local Git replacement refs.
- `LedgerStore.fs:430-438` invokes Git with `GIT_TERMINAL_PROMPT=0` but **not** `GIT_NO_REPLACE_OBJECTS=1`; `isAncestor` / `verifyIntegrationOnRemote` rely on ordinary Git object resolution as well.
- `ReviewWorkflow.fs:1357-1359` trusts this mechanical proof before creating the canonical `Integrated` receipt.

## Independent reproduction (disposable bare remote, no Jarvis source or live remote changes)

With Git 2.47.3, create base B; source S and independently advanced target T; clean Git merge I with parents T,S. Create distinct commit J with the same parents T,S **but a tree containing an unreviewed `extra.txt`**; publish J as the bare remote's target by an ordinary (non-force) fast-forward from T. In the verifier's local clone, run `git replace J I`.

- Remote target advertises J, whose **real** tree was `400c1dd88d29103aaa79e47cd4d6757cd278f772`.
- Expected mechanical Git merge tree was `3d5b1f7c621b4e72308d60b52c0be20f11747e0f`.
- Default `git rev-list --parents -n 1 J` reported J with the required T,S parents.
- Default `git rev-parse J^{tree}` returned the **expected** clean tree `3d5b1f7c621b4e72308d60b52c0be20f11747e0f`, despite the remote object being J with malicious extra content.
- `GIT_NO_REPLACE_OBJECTS=1 git rev-parse J^{tree}` exposed the actual divergent tree.

This demonstrates that the candidate's proof-bearing command sequence can be made to accept an unreviewed tree if the verification repository contains a local replacement ref. It is a Git-level reproduction, **not** a full `ReviewWorkflow.completeIntegration` run. The candidate machine reports Git 2.56.0 and no existing `refs/replace` at inspection, which does not establish the future safety of the verifier. The local Jarvis shell-executable repro was permission-denied; the independent reproduction was run in a separate ephemeral environment.

## Required correction / regression

All integrity-sensitive object inspection and ancestry/merge-tree commands must read the *real* commit content, independent of local `refs/replace` (and address graft or equivalent local history overlays). Use a single trusted Git execution context with replacement disabled and exact tree/parent comparison. Add a genuine bare-remote negative regression that publishes J with an unreviewed extra tree edit, installs a local replacement mapping J→I on the verifying clone, and proves `completeIntegration` refuses the receipt. Also verify the safe valid merge still completes without any replacement object.

A clean `git diff --check` and passing existing tests cannot cover this missing adversarial case.