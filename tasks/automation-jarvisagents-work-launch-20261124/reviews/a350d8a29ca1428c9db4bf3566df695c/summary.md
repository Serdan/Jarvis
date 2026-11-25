# Independent review — accept

**Review:** `a350d8a29ca1428c9db4bf3566df695c`  
**Frozen source:** `e057c190ea257e531808ec80e9f923a6b13c6412`  
**Base:** `2fcd3241f9db5ab33af50088dcf48f407448d33d`  
**Target ref:** `refs/heads/main`

## Scope and implementation

The frozen diff changes only:
- `browser-scripts/chatgpt/launchAgent.js`
- `browser-extension/tests/chatgpt-launcher.test.cjs`

It preserves the three prerequisite reviewed launcher commits in its ancestry (`a4729ab`, `5e34514`, `b6f2d05`) and adds the Chat-first quota gate.

The default order is Chat / Latest / High, then Work / GPT-6.1 Terra / Medium, then Chat / Latest / Instant. Work is reached only when the current Chat model or High-effort control is disabled and that same target carries an explicit `Usage limit reached` aria description. Missing controls, ordinary model/effort unavailability, mode-selection failure, and failed verification do not authorize Work; those paths stop before Send. The legacy Work override also attempts verified Chat High first.

The project-composer handling scopes visible picker controls to the composer where form association is available, ignores hidden and provably unrelated picker controls, and rejects competing relevant controls or contradictory visible Work model/effort evidence. The existing prompt, destination, one-Send, and uncertain-effect protections remain in place.

## Independent verification

- Exact frozen worktree was clean at the reviewed head.
- `git diff --stat`: 2 files, 321 insertions and 45 deletions.
- `git diff --check 2fcd3241..e057c190`: exit 0, no whitespace errors.
- Ran `node --test browser-extension/tests/chatgpt-launcher.test.cjs` from the frozen worktree: **162 passed, 0 failed**.
- Ran the frozen launcher in `selectionOnly` mode on the exact JarvisAgents project URL. It returned `status: selectionOnly`, selected Chat / Latest / High, and reported no fallback attempts. No prompt was submitted and no agent turn was consumed. The created tab was closed afterward.

The live account currently had Chat High available, so this live probe confirms the Chat-first path; it does not claim a live Work fallback. The offline suite covers the explicit quota-evidence Work path, ambiguous/unavailable controls, Work-only project composers, competing pickers, contradictory labels, and single-Send/no-send fences.

## Findings

No blocking or material correctness findings. **Accept** the exact frozen source. This review does not authorize deployment or unattended automation.
