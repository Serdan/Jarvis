# P2 — Production server update runbook still instructs the obsolete probe port and protocol

Frozen source: `aef4da9e97bd27682bd85e42f18d635c25cf4ebe`, `refs/heads/fix/server-updater-prod-parity`.

## Evidence

The corrected updater `scripts/jarvis-update-server.sh:16` explicitly probes `http://127.0.0.1:5095`, matching the verified production systemd/nginx listener; its black-box fixture now enforces 5095 and fails closed when the helper probes 5000. However the **operator-facing** `docs/ServerUpdates.md:10` still states that the helper uses `http://127.0.0.1:5000`, immediately instructing administrators to **verify these production-specific constants before installation**. Line 22 again describes an unrelated listener on port 5000. Line 54 describes a pending **protocol 3.62** rollout even though this review is specifically reconciling deployed **protocol 3.63**.

These are operationally material contradictions, not harmless terminology: the user following this installation or emergency-recovery runbook is directed to validate or investigate the wrong port, precisely the class of mismatch this task resolves, and cannot tell whether its described protocol rollout already happened. The emergency recovery procedure at lines 58–67 refers back to these protocol probes.

## Required correction

Update `docs/ServerUpdates.md` to describe loopback 5095 consistently (including the socket-owner check), the applicable JSON `Content-Type` header for POST /mcp, the 100-second bounded graceful stop vs the production 90-second systemd policy, the canonical installer that creates the mode-0700 root-owned pending directory, and the verified **3.63** deployment state. Preserve the explicit caution to verify production-specific constants before installing, but do not assert known-false defaults. Consider a lightweight static consistency check so the port/docs drift cannot recur.

**Severity: P2 operational correctness / production runbook acceptance.** The *updater code and tests* reviewed here appear sound; this finding does not claim a live runtime failure or propose changing the verified 5095 probe.
