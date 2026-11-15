# P2 — An active unclassified project agent is omitted from capacity accounting

**Location:** `scripts/manager-heartbeat.mjs:71-77,174-183`

`validateSnapshot` accepts `kind: "other"`, but the existing-live-agent loop only counts `worker` and `reviewer` kinds. An active or unknown `other` agent is silently ignored even though the canonical task requires any additional live/uncertain project agents to be included in the hard-bound decision. The Manager can thus consume the full configured launch budget while there is existing, potentially worker-equivalent activity not categorized into either bucket.

**Independent reproduction:** configuration worker budget 1, one configured Ready worker plus an extra `kind: "other", state: "Working"` agent. The planner still reserves the configured worker and reports only `worker:1`.

**Required correction:** fail closed if there is live/uncertain project activity whose worker/reviewer capacity impact cannot be positively classified, or account for it against a separately enforced project-wide maximum. Add a negative fixture for unclassified live/unknown observations.
