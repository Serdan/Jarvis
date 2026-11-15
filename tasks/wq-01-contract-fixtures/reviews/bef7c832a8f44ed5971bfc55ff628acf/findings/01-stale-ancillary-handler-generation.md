# P1 — Stale ancillary Role-message reply can complete a later claim generation on the same launch

**Severity:** P1, safety / handler-exclusivity and dependency-completion provenance  
**Frozen source:** `1d5a2b4daaeef825ad5d14b6dd9facd981adc6f9`  
**Locations:** `Server/WorkQueueContracts.fs:873-915`, `:919-948`, `:954-1006`  
**Accepted contract:** `docs/specs/work-queues-and-ephemeral-executions.md` §Task- and role-addressed operator communication, especially lines 371–375, requires `CompleteSubjectMessageHandling(... handlerGeneration ...)` and generation-fenced handler responsibility.

## Observation

The new `resolveAncillaryDependencyGate` correctly invalidates the original W claim by incrementing `WorkStage.ClaimGeneration` and removing M from `L.AncillaryMessages` (lines 990–1005). A later `claimAncillaryMessage` by **the same still-active launch L** increments `ClaimGeneration` again and re-adds the same message ID (lines 905–915). But `completeAncillaryMessage` accepts only `launchId`, `messageId`, `replyId`, `now` and the project CAS version. Its ownership checks at lines 922–944 match **only L/M identity**, current Claimed state, clear gate and current satisfied dependencies. They never bind the reply to the handler/claim generation that produced it.

## Reachable interleaving

1. Registered L claims primary `Plan`; L claims ancillary Role message M/W at generation `g1`. An answer from this handler is prepared but not canonically committed.
2. A hard W→B edge wins, setting `DependencyGatePending`. The old `g1` handler is no longer authorized to complete W. B completes or an authorized waiver is recorded while Plan stays Claimed.
3. `resolveAncillaryDependencyGate` releases only M/W under CAS, increments claim generation to `g2`, and marks M Pending; L and Plan remain live.
4. The same L legitimately reclaims M, incrementing W to `g3` and setting `MessageState.ClaimedAncillary L`. This same-launch reclaim is now possible specifically because the new resolver does not terminalize Plan.
5. A **stale pre-gate `g1` reply** is submitted with a fresh current project CAS version and a distinct operation ID, e.g. `completeAncillaryMessage current.Version "late-old-reply" "L" "M" "pre-gate-answer" now current`. Every current condition in lines 922–944 is true. It therefore marks M Handled and W Completed, and wakes downstream D, **without the new `g3` handler ever committing its action**.

A stale *project-version* attempt is rejected, as existing tests show, but the current API cannot reject a stale *handler-generation* attempt once a caller refreshes the CAS head. Requiring a current project version is not equivalent to possession of the current handler lease. The five new tests cover re-claim and completion, but all replies are treated as if produced by the current lease; none tests an old generation answering after same-launch reacquisition. This is a static code-path counterexample; it is not an executed negative fixture.

## Required correction

Add an explicit expected ancillary handler/claim generation (and, if independently versioned, handler-lease identity) to the canonical completion request/operation payload and require it to match W's currently claimed generation, the associated M handler record and live launch. Return/pin the generation when `claimAncillaryMessage` succeeds. Make `completeAncillaryMessage` reject a reply originating from an invalidated generation even when the same launch L later reclaims M and even if the caller supplies the latest project version. Do not solve this by rejecting all same-launch reacquisitions: preserving Plan while allowing legitimate fresh handler work is the intended fix.

**Mandatory regression:** `g1 claim → W→B gate → B Completed/verified waiver → resolve while Plan live → same L reclaim at g3 → old-g1 reply with current project.Version` must reject without handling M, completing W, waking D or releasing Plan/token; only a `g3`-bound reply may succeed. Include stale two-client CAS, lost-ack replay and a competing terminal of Plan. Preserve the existing 52 focused tests and source-effect fence.

**Disposition:** ChangesRequested for this frozen SHA; do not integrate until a new exact source commit receives independent acceptance.
