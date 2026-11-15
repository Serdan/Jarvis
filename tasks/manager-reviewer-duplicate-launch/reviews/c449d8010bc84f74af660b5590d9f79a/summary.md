# Independent frozen re-review — Changes Requested (P1)

**Task:** `manager-reviewer-duplicate-launch`  
**Frozen source:** `70c6916225a4dce99a4baeda9983cdf21fe15ded` (base `7a14651c1cd620bf707130c26e446b9f9921418f`).

Reviewed the Manager heartbeat planner implementation, authoritative claimed-review snapshot contract, journal migration/restart logic, capacity calculation, dry-run parity, tests, and role/runbook changes. The H1 activity-read → H2 claim-head race is correctly treated conservatively: a claim disappearing does **not** itself end a browser turn. Journal terminal markers and newer attributed activity are guarded against stale and contradictory evidence. Executed the frozen Node suite: **47 passed, 0 failed**.

**Blocking finding:** `findings/p1-taskless-reviewer-yield-producer.md`. The new automatic resumption mechanism is contingent on a `reviewerYieldAtUtc` event with positive session attribution; the current Jarvis taskless reviewer workflow cannot produce one. `yield_agent` requires an assigned Working task, whereas shared reviewers hold independent review claims while remaining unassigned/Stopped. Tests fabricate the yield timestamp, so they do not demonstrate end-to-end reviewer lifecycle recovery. Following normal review completion, the planner safely suppresses duplicates but leaves the reviewer capacity occupied until an operator manually clears the observed session.

**Verdict:** **ChangesRequested**. Supply an actual verifiable taskless reviewer session-end producer integrated with the Manager snapshot and test its claim→decision→yield→restart→relaunch lifecycle. No deployment, launch, or scheduling action was performed.