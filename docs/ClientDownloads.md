# Client Downloads Deployment

Jarvis clients are distributed as self-contained single-file executables. The client has the SignalR server URL embedded at publish time, so each public build must be stamped with the production hub URL.

The self-update facility treats these files and `SHA256SUMS` as the authoritative update channel. Whenever a deployment changes the client/shared protocol, publish and upload the matching download artifacts before relying on `UpdateClient`.

On Linux and macOS, activation replaces the running process with the verified executable using `exec`, preserving the client PID and terminal session. If process replacement fails, Jarvis restores the previous executable from the `.prev` backup and reports the activation failure without exiting the current client.

For the current production host, clients should connect to:

```text
https://jarvis2.kehlet.dev/client
```

## Build download artifacts

From the repository root:

```bash
dotnet scripts/publish-client-downloads.cs
```

This publishes and copies stable download files into:

```text
artifacts/downloads/
```

Expected files:

```text
JarvisClient-linux-x64
JarvisClient-osx-arm64
JarvisClient-win-x64.exe
SHA256SUMS
```

## Pending-intent lock protocol compatibility gate

The deployed exact-file protocol (introduced by source commit `95a471fc5`) holds the
legacy UUID-prefix stripe shared and each exact-UUID file exclusively for the
entire operation, including uncertain remote Git effects. Older clients take the
stripe exclusively. These two file generations can coexist.

The separate experimental `fix/named-uuid-lock-20261120` generation uses a
named OS mutex instead of the exact-UUID file. **It is not compatible with
the deployed exact-file generation:** two processes can share the stripe while
both believe they exclusively own the same UUID. That branch has not been
approved as a published client and MUST NOT be released, installed, rolled
back into, or run alongside file-based clients.

**Fail closed before any lock-protocol change or rollback:**
- Identify the exact binaries/source hashes on *every* affected machine, not
  just a nominal assembly version (currently often `1.0.0`) or the unrelated
  `AgentProtocol.version` (`3.68` at the time of this note).
- Obtain explicit Operator approval for the protocol-family cutover. Stop and
  verify retirement of all incompatible clients, including disconnected
  processes capable of resuming. Drain or reconcile uncertain in-flight effects
  by their saved UUID; never replay a write just because a process disconnected.
- Prevent old clients from reconnecting or restarting with a different lock
  protocol until an enforced compatibility admission gate exists. Keep
  incompatible rollback artifacts quarantined.
- Prove exact-UUID exclusion for every allowed mixed generation on Linux and
  native Windows *before* release. Do not infer coexistence from both using the
  same shared prefix stripe.

The client build-identity/versioning task
`jarvis-p1-client-build-identity-version-protocol-ui-20261120` owns the
enforceable identification and compatibility-admission gate. Until that gate
and a cutover procedure have passed independent review, remain on the existing
file-based lock family. Opportunistic file cleanup is correct only for
cooperating clients that acquire the shared stripe **before** opening exact
lock files. Arbitrary non-cooperating openers are not a supported guarantee.

## Upload to the droplet

Recommended static file location on the Ubuntu droplet:

```text
/var/www/jarvis/downloads/
```

Example upload:

```bash
ssh root@jarvis2.kehlet.dev 'mkdir -p /var/www/jarvis/downloads'
rsync -av --delete artifacts/downloads/ root@jarvis2.kehlet.dev:/var/www/jarvis/downloads/
```

Use a non-root deploy user if one is configured.

## Nginx configuration

Serve the downloads directly from nginx, separate from the ASP.NET Jarvis server.

Example location block inside the `jarvis2.kehlet.dev` server block:

```nginx
location /downloads/ {
    alias /var/www/jarvis/downloads/;
    autoindex on;
    default_type application/octet-stream;

    add_header X-Content-Type-Options nosniff;
    add_header Cache-Control "public, max-age=300";
}
```

The SignalR hub should continue to proxy to the Jarvis server. The exact port depends on the server deployment, but the shape should be similar to:

```nginx
location /client {
    proxy_pass http://127.0.0.1:5000/client;
    proxy_http_version 1.1;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection "upgrade";
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
}
```

After editing nginx:

```bash
sudo nginx -t
sudo systemctl reload nginx
```

## Public URLs

```text
https://jarvis2.kehlet.dev/downloads/JarvisClient-linux-x64
https://jarvis2.kehlet.dev/downloads/JarvisClient-osx-arm64
https://jarvis2.kehlet.dev/downloads/JarvisClient-win-x64.exe
https://jarvis2.kehlet.dev/downloads/SHA256SUMS
```

## Smoke test

On Linux:

```bash
curl -I https://jarvis2.kehlet.dev/downloads/JarvisClient-linux-x64
curl -fsSL https://jarvis2.kehlet.dev/downloads/SHA256SUMS
```

Then download and run:

```bash
curl -fsSLO https://jarvis2.kehlet.dev/downloads/JarvisClient-linux-x64
chmod +x JarvisClient-linux-x64
./JarvisClient-linux-x64 --path ~/Projects
```

The client should show a key and log a successful connection to `https://jarvis2.kehlet.dev/client`.
