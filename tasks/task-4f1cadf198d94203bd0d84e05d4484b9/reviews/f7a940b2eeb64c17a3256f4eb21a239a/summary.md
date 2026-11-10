# Independent re-review — server updater production runbook parity

**Verdict: Accept. No blocking findings.**

Frozen source: `d5544d9a614f313a45ec58bbfc8842239db386f3` on `refs/heads/fix/server-updater-prod-parity`, reviewed base `28fd0c3d2fa887a8cd7289e07cb65b476de351f9`, target `main`. Corrects previously rejected `aef4da9e97bd27682bd85e42f18d635c25cf4ebe` (prior review `e86a91a9631e40de8f705a52c596a407`).

## Verified correction

Exactly one file changed since prior rejected commit: `docs/ServerUpdates.md`; the previously independently tested updater/installer code is unchanged.

- **Production route:** runbook lines 9–10 explicitly instruct operators to install both reviewed installer and updater helper, describe single-file systemd MainPID assumptions, and give **http://127.0.0.1:5095** with `Host: jarvis2.kehlet.dev`, consistent with `scripts/jarvis-update-server.sh:16–17`. Socket-owner guidance at line 22 now uses **5095**, eliminating stale 5000 assertions.
- **MCP authentication media type:** line 24 states `Content-Type: application/json` and payload `{}` are required for HTTP **401** Bearer readiness, while no media type gives **415**; mirrors `probe_http` POST at lines 87–92, `probe_once` /mcp 401 check at lines 136–138 and the fixture's negative-space test.
- **Bounded graceful stop:** line 38 correctly documents **100-second** stop budget for both forward update and rollback, above production systemd's 90 seconds, distinct from bounded restart and 35-second readiness; matches `STOP_TIMEOUT_SECONDS=100` at line 21 and both `systemctl stop` paths.
- **Private rollback journal:** lines 12 and 46,69 reflect exact `/var/lib/jarvis-update-state/pending`, mode-0700 directory and private journal semantics, matching script and canonical installer; emergency recovery requires independent readiness verification before deleting journal.
- **Current protocol:** line 56 identifies server **3.63** as of 10 Nov 2026 and correctly describes separately coordinated client transitions including an older 3.62 Windows client, rather than falsely labeling protocol 3.62 as a future rollout.
- MainPID, running executable checksum, listener ownership, OAuth/SignalR route checks, no redirects/proxies, preflight failure safety, verified rollback and journal retention are unchanged from prior independent source review. No new safety regression introduced by this documentation-only commit.

## Independent verification

- **21/21 black-box updater tests passed**, job `c0decd890372434791b48273a7731c3a`, Python suite duration 81.651 seconds; no production systemctl or live binary touched.
- Exact frozen HEAD `d5544d9a...` verified, worktree clean.
- `git diff --check 28fd0c3..d5544d9` exits 0.
- `git merge-tree --write-tree main d5544d9` exits 0, conflict-free.
- `git diff --name-status aef4da9..d5544d9`: only `docs/ServerUpdates.md`.
- No source edits, merge, integration or production deployment by reviewer.

**Decision: Accept.** The previous P2 operational documentation issue is closed. Integration and deployment remain Lead/operator responsibilities.
