# High — review mutations are not fenced by taskless activation

## Original obligation
Pinned spec 949851d88b4623d4f6d4019fd75a1da0e6533966 explicitly requires "no stale claim or old session can act", STOP and newer generation win, and concurrent claim/start and restart must not produce duplicate claims or decisions. This is an existing lifecycle requirement, not a demand to implement a new scheduler.

## Source evidence
Frozen 50ec1e737cba59516004d864531d1a709e674561: Client/Modules/ReviewWorkflow.fs:4571–4609, claimBranchReview checks only review version and Unclaimed; decideBranchReview checks only review status, version and ReviewerId. Neither reads the agent record, checks Working, nor binds a taskless activation identity. Client/Modules/OrchestrationApi.fs:2441–2453 forwards the review actor and review version directly to these functions. Client/SignalR/Client.fs:1152 dispatches ReviewWriteCommand directly to ReviewApi.write. The legacy MCP claim/decide tool signatures have review version, not taskless activation identity. The new start/yield transitions at ReviewWorkflow.fs:2745–2787 do not change the review version. The Manager prompt directs the model not to act on a rejected start, but that is not an authoritative mutation fence.

## Reproduction (source-derived, no live mutation)
1. Reviewer R starts taskless execution and claims review Q at version 2.
2. R yields without deciding; Q remains Claimed v2. Operator parks R or scheduler starts a new taskless activation for R.
3. A delayed review decision from the old session, using the still-current Q v2 and R, reaches decideBranchReview. The predicate succeeds; it writes a terminal verdict even though the original activation is over. Similarly a delayed claim against an unchanged Unclaimed version is accepted while R is stopped.
4. Review version CAS prevents two successful decisions on the same version, but cannot ensure that the *current* activation wins. An old session may win the race and the new session receives a conflict.

## Impact and scope
The taskless lifecycle change makes scheduler resumption possible but does not fence the irreversible review mutation itself. This is a correctness boundary under delayed operations, not a claim that malicious callers bypass account permissions. The role prompt is useful cooperative steering, not evidence of stale-session rejection. Source inspection, not an executed F# reproduction; no production writes were attempted.

## Candidate remedy for independent adjudication
Bind scheduler-managed taskless review claims/decisions to the authoritative activation generation or equivalent existing session fence, verify it in the transaction, and preserve intentional manual review compatibility as a separately explicit path if required. Test delayed claim and decision after yield/park/restart with unchanged review version, plus valid resumed decision. The precise API design remains for adjudication; do not expand scope to the Work/SQLite cutover.