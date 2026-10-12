# Server self-update

The production server is a systemd service running as the unprivileged `jarvis` user. `UpdateServer` is a server-local MCP tool authorized with `process:execute`. It schedules an independent transient systemd unit via one exact sudoers rule. Its response means *scheduled*, not *successfully deployed*. After reconnecting, call `GetServerUpdateStatus`.

First-time bootstrap (from a trusted administrator session):

1. Review and run `sudo sh scripts/install-server-updater.sh` **on the server** (copy the script there first). The installed updater script and sudoers entry are owned by root.
2. Build and publish the release: `dotnet scripts/publish-server-download.cs`. Upload `artifacts/downloads/JarvisServer-linux-x64` and `SHA256SUMS` into `/var/www/jarvis/downloads/` together, with the checksums updated only after the new binary is in place.
3. Deploy a server containing `UpdateServer` using the existing deployment procedure and verify its startup. Then invoke `UpdateServer` for later releases.

The helper fetches a fixed HTTPS release endpoint, checks SHA-256 before changing the live binary, stages the result, keeps `JarvisServer.prev`, restarts `jarvis.service`, and restores that backup if restart or the immediate active-state check fails. Check `/var/lib/jarvis/server-update-status` or `GetServerUpdateStatus` for the last result. This is a quick active-state check, not a comprehensive health check; a subsequent manual rollback remains possible.

Do not allow arbitrary URLs, command arguments, or shell fragments into the privileged update path. Publishing a new server binary is a separate release action, not part of `UpdateServer`.
