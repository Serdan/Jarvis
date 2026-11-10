# P2 — Same-ledger duplicate fetches silently discard the fresher canonical snapshot

**Frozen source:** `ffa3aeb584f916046e31f4442928d0820f6684ac`

**Requirement:** Per-project counters come from canonical ledger data; duplicated ledgers are counted once; stale/unavailable state is indicated rather than showing misleading counts.

**Evidence:** `AgentDashboard.load` (lines 254–265) fetches **every** project concurrently with `Array.Parallel.map`, including multiple bindings for the same logical ledger. Each `projectSnapshot` has a verified immutable `snapshot.Commit`, but the new return shape at lines 113–192 drops that commit and returns only `(identity, agentRows, pendingReviewCount)`. Then `snapshotFromResults` (lines 217–225) keeps **the first result** whose identity passes `seen.Add`, silently discarding later results without checking whether their canonical commits differ.

**Reproduction scenario:** `owner` and `reviewer` are two configured aliases of one ledger. The `owner` fetch sees commit H1 with one Ready agent and 0 unread. A state transition and notification arrive; the `reviewer` fetch sees descendant H2 with one Working agent and 1 unread. Parallel result ordering selects the `owner` snapshot H1, discards H2, and publishes a fresh `RefreshedAt` with stale Ready/0 counts, even though this refresh already fetched canonical H2. The new tests only deduplicate copies with identical state and review counts, so they do not exercise this case.

**Fix:** Deduplicate aliases by canonical identity **before fetching** the ledger, obtaining one verified snapshot per logical project per refresh; or preserve each `snapshot.Commit` and deterministically select the latest verified descendant while explicitly marking incompatible/divergent states. Never silently pick by enumeration order. Add a regression where two aliases report differing heads/counts and ensure the newest known valid view wins or the result is flagged as inconsistent, not silently presented as current.

**Severity:** P2 freshness/correctness gap in the new deduplication path. It is intermittent under concurrent updates but directly affects the counts and state labels the feature was added to provide.
