# Server self-update: verified readiness and recovery

The production server is a single systemd service running as the unprivileged `jarvis` account. `UpdateServer` schedules an **independent** privileged transient systemd unit through the fixed sudoers entry; its MCP response means **scheduled**, never **successfully deployed**. `GetServerUpdateStatus` reads the last atomic status file after the initiating server connection drops. No public MCP API shape changes.

## Bootstrap and prerequisites

From a trusted administrator session on the server:

1. Copy **both** `scripts/install-server-updater.sh` and `scripts/jarvis-update-server.sh` from the same reviewed repository revision. Run `sudo sh scripts/install-server-updater.sh`. It atomically installs the root-owned helper at `/usr/local/sbin/jarvis-update-server` and the existing narrow sudoers rule.
2. The helper assumes `/opt/jarvis/server/JarvisServer` is the **single-file Linux executable** used directly as systemd `MainPID` (not `dotnet JarvisServer.dll` or a shell wrapper). It uses a direct HTTP listener at `http://127.0.0.1:5000`, with `Host: jarvis2.kehlet.dev`. Verify these production-specific constants before installation. The loopback HTTP endpoint must not redirect to HTTPS; TLS terminates at the reverse proxy.
3. Publish `dotnet scripts/publish-server-download.cs`. Atomically publish `JarvisServer-linux-x64` before the new `SHA256SUMS` on the release host. The updater fetches **only** the fixed HTTPS release origin and matches the exact SHA-256 manifest entry.
4. The destination directory `/opt/jarvis/server`, state directory `/var/lib/jarvis`, and root-owned `/run` must be available. The status file remains world-readable (0644), but the rollback journal is created as root-private (0600) in a root-owned mode-0700 directory.

The new server eagerly resolves/initializes its SQLite feedback/operation store before listening. A bad database configuration therefore cannot produce apparently healthy OAuth metadata during startup. This startup check is not a continuous, per-request database health probe.

## Readiness is more than systemctl is-active

Before replacing the previous executable and after restarting the candidate, the updater verifies:

- `systemctl is-active` and an actual positive numeric `systemctl show -p MainPID`.
- `sha256sum /proc/<MainPID>/exe` equals the **expected** verified binary checksum. This checks the kernel's running inode even if an executable path has been replaced while the previous process was still alive.
- A bounded `ss -lntp` check confirms the same MainPID **owns the configured loopback TCP listener/address**, not just that a different service responds on port 5000 (requires Linux iproute2 `ss`).
- A direct **unproxied loopback** GET to `/.well-known/oauth-protected-resource` responds 200 with the configured audience and `authorization_servers`.
- A direct POST to `/mcp` responds HTTP 401 with a Bearer challenge. This confirms the route is mapped while authorization is **not bypassed**.
- A direct POST to `/client/negotiate?negotiateVersion=1` responds HTTP 401 with a Bearer challenge advertising `client:connect`.
- The service remains active under the **same MainPID** after all probes.

The HTTP checks never follow redirects or use proxies, and they never send bearer tokens. The fixed localhost/Host headers and status codes are checked; unknown content and error details are not persisted. Every loopback response body is capped at 16 KiB; the release checksum manifest is capped at 128 KiB. The systemd PID operations and executable hash are also bounded by the shared probe deadline. Each readiness attempt has per-request timeouts bounded by a shared **35-second deadline**, with one-second retries. A brief startup delay or transient HTTP error may recover inside the budget. An active but non-serving process fails.

If the release checksum equals the verified current executable, the updater reports `success unchanged=<sha>` without a restart.

## Atomic status, rollback and interrupted updates

The helper holds a persistent `flock` on a root-owned lock inode so concurrent updates cannot mutate the same service. Each update writes its status through a temporary file and atomic rename, keeping `GetServerUpdateStatus` readable even if the updater's initiating MCP connection is lost.

The successful sequence is: preflight the existing service, fetch and verify release, stage the candidate, create and publish a complete verified `JarvisServer.prev`, write a private rollback-intent marker, stop the service, replace the executable, restart, verify the **candidate** executable hash and protocol routes, clear the intent marker, and **only then** report success.

A failure after writing rollback intent stops the candidate, restores the exact prior executable, restarts it, and independently performs the same full readiness probes against the previous hash. A failure is recorded as one of:

- `failed: stage=<phase> rollback=not-needed`: no live mutation was attempted (for example bad manifest, missing live binary, checksum mismatch, or unhealthy preflight).
- `failed: stage=<phase> rollback=recovered previous=<sha>`: the previous executable and protocol endpoints were verified after rollback.
- `failed: stage=<phase> rollback=<reason>`: recovery failed (missing/corrupt prior binary, stop/replace/restart error, unresponsive prior service, or journal cleanup). This is never reported as successful recovery.

A process killed without a catchable signal or a power loss can leave `server-update-pending`. On its next invocation the helper **first attempts recovery** from the independently verified prior binary and exits with a recorded failed/interrupted status; it does not overwrite the recovery backup or start a fresh update. The operator can then choose whether to invoke a new update.

Both success and recovery status record only fixed stage names and executable SHA-256 hashes, never authorization tokens, network response bodies, or signed URLs.

## Tests and production safety boundary

Run `python3 scripts/test-server-updater.py` for the isolated black-box harness. It rewrites only a **temporary copy** of the fixed-path updater to use a temporary checkout, synthetic `/proc`, fake systemd and fake HTTP; it never runs `systemctl` on production or touches `/opt/jarvis`. The harness tests healthy updates, unchanged versions, checksum/manifest rejection, an active-but-unhealthy service, stale process identity, short transients, delayed startup, failed rollback, missing previous executable, and interrupted-update recovery. The real Server.Tests suite separately verifies the HTTP 401 behavior and that unusable SQLite prevents server startup.

Known boundary: the updater verifies the server's own Kestrel loopback listener, not the external reverse proxy, DNS, or remote Auth0 discovery. External end-to-end user connectivity should still be checked after a coordinated release.

A server/client protocol 3.62 rollout is **separate** and must be coordinated because SearchFiles/SearchText return a new typed response schema. Passing source tests does not authorize deployment.

## Emergency manual recovery

If status reports `rollback=...failed` or the service remains unhealthy, use a trusted root session. Inspect `systemctl status jarvis.service` and `journalctl -u jarvis.service -n 100`, then confirm the SHA-256 of `/opt/jarvis/server/JarvisServer.prev`. If the backup is verified and known good:

```sh
sudo systemctl stop jarvis.service
sudo cp -p /opt/jarvis/server/JarvisServer.prev /opt/jarvis/server/.JarvisServer.next
sudo mv -f /opt/jarvis/server/.JarvisServer.next /opt/jarvis/server/JarvisServer
sudo systemctl restart jarvis.service
```

Check `systemctl is-active`, `systemctl show -p MainPID`, `sha256sum /proc/<MainPID>/exe`, and the three localhost protocol probes listed above. **Only after independently verifying recovery** should an administrator clear the pending marker `/var/lib/jarvis-update-state/pending`. Do not delete the verified previous executable merely to clear an update failure.
