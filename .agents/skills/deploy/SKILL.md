---
name: deploy
description: Deploy or redeploy Jarvis safely to production, including publishing the server, replacing the production systemd binary with rollback protection, publishing compatible client builds when needed, verifying protocol compatibility, and publishing downloadable clients when explicitly requested. Use when the user asks to deploy Jarvis, update the production Jarvis server/client, restart production after code changes, or publish Jarvis client downloads.
---

# Jarvis deployment

Deploy conservatively and preserve rollback. Treat the local Jarvis client differently from the production server: the server is a systemd service; the user's local client is an interactive terminal process and must never be started, stopped, or restarted by the agent.

## Before deploying

1. Inspect git status and the relevant diff. Deploy only the intended code. Do not stage, commit, or discard unrelated user changes.
2. Run the canonical test workflow:
   `dotnet scripts/build.cs test`
3. If tests fail, stop and report the failure rather than deploying.
4. Note whether the change affects `Common/`, `Client/`, the agent protocol version, or client/server command definitions. If so, a compatible client build is required after the server deploy.

## Deploy the production server

Production currently uses:

- host: `root@jarvis2.kehlet.dev`
- service: `jarvis.service`
- live binary: `/opt/jarvis/server/JarvisServer`
- staging binary: `/opt/jarvis/deploy-next/JarvisServer`
- rollback binary: `/opt/jarvis/server/JarvisServer.prev`

First publish:

```bash
dotnet scripts/build.cs publish-server --rid linux-x64
```

The output binary is:

`artifacts/server/linux-x64/JarvisServer`

Before replacement, inspect the remote service if anything about the deployment layout may have changed:

```bash
ssh root@jarvis2.kehlet.dev 'systemctl cat jarvis.service'
```

Upload to staging:

```bash
scp artifacts/server/linux-x64/JarvisServer root@jarvis2.kehlet.dev:/opt/jarvis/deploy-next/JarvisServer
```

Replace atomically while preserving the previous live binary:

```bash
ssh root@jarvis2.kehlet.dev 'set -e;   chown jarvis:jarvis /opt/jarvis/deploy-next/JarvisServer;   chmod 755 /opt/jarvis/deploy-next/JarvisServer;   cp -a /opt/jarvis/server/JarvisServer /opt/jarvis/server/JarvisServer.prev;   systemctl stop jarvis.service;   mv /opt/jarvis/deploy-next/JarvisServer /opt/jarvis/server/JarvisServer;   systemctl start jarvis.service'
```

Restarting the production service interrupts the MCP/SignalR connection that is carrying the deployment command. An `UNAVAILABLE` or connection-loss result at this exact step does not by itself mean deployment failed. Wait for the client to reconnect, then verify explicitly.

Verify after reconnection:

```bash
ssh root@jarvis2.kehlet.dev 'systemctl is-active jarvis.service; systemctl show -p ActiveEnterTimestamp -p MainPID jarvis.service; ls -l /opt/jarvis/server/JarvisServer /opt/jarvis/server/JarvisServer.prev'
```

Also use `GetConnectionDiagnostics` to confirm the local client has re-registered.

If the server fails to start, restore `JarvisServer.prev` and restart `jarvis.service`.

## Publish the local Linux client when compatibility changed

When protocol or client/shared command code changed, publish a matching Linux client:

```bash
dotnet scripts/build.cs publish-clients   --server https://jarvis2.kehlet.dev/client   --rid linux-x64
```

The expected binary is:

`artifacts/client/linux-x64/JarvisClient`

**Never launch, kill, restart, detach, daemonize, or otherwise manage the user's local Jarvis client process.** It runs in the context of the user's open terminal. After publishing, tell the user the new binary is ready and let them restart it from that terminal themselves.

Once the user says they restarted it, call `GetConnectionDiagnostics` and confirm the client protocol matches the server protocol with no mismatch in the registration reason.

## Publish downloadable clients only when requested

For a public client release, build all production download artifacts:

```bash
dotnet scripts/publish-client-downloads.cs
```

Then upload them:

```bash
ssh root@jarvis2.kehlet.dev 'mkdir -p /var/www/jarvis/downloads'
rsync -av --delete artifacts/downloads/ root@jarvis2.kehlet.dev:/var/www/jarvis/downloads/
```

Smoke-test at least:

```bash
curl -I https://jarvis2.kehlet.dev/downloads/JarvisClient-linux-x64
curl -fsSL https://jarvis2.kehlet.dev/downloads/SHA256SUMS
```

Do not rebuild or upload all public client downloads during an ordinary server-only deploy unless the user asked for it or client compatibility changed and the downloads are intended to be updated.

## Completion checks

A deployment is complete only after:

- tests passed before deployment;
- production `jarvis.service` is active on the new binary;
- the previous server binary remains available as `JarvisServer.prev`;
- the local client has been republished if compatibility changed;
- after the user restarts the local client, server and client protocol versions match;
- public download artifacts were updated and smoke-tested if that was part of the requested release.

Report what was deployed, what was verified, and whether any user-side client restart is still required.
