# Client Downloads Deployment

Jarvis clients are distributed as self-contained single-file executables. The client has the SignalR server URL embedded at publish time, so each public build must be stamped with the production hub URL.

The self-update facility requires both the exact SHA-256 in `SHA256SUMS` and the corresponding platform identity in `latest.json`; mismatched, malformed, or dirty-release manifests fail closed. Whenever a deployment changes the client/shared protocol, publish and upload the matching download artifacts before relying on `UpdateClient`.

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

Expected stable update aliases and release metadata:

```text
JarvisClient-linux-x64
JarvisClient-osx-arm64
JarvisClient-win-x64.exe
latest.json
SHA256SUMS
releases/<app-version>-<git-sha12>/
```

Each immutable release directory contains platform-specific binaries. `latest.json` pins the full source commit, application version, clean/dirty status, release ID, each RID, file path, size, and SHA-256. Existing immutable files cannot be silently replaced with different bytes. Stable aliases remain for the current Linux/macOS update workflow and the manual Windows workflow.

The client executable prints application version, full Git commit, separate protocol version, embedded RID and dirty/development state offline with `--version`. TUI headers display app version and protocol even when disconnected. During client registration, the server receives the **running** version plus Git/RID/dirty identity without changing supported-command negotiation.

Only deploy **clean, reviewed, approved** release artifacts. Before publishing, confirm `git status --porcelain` is empty and the selected source commit is the intended reviewed commit. Publishing never upgrades or restarts either Operator-controlled client.

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
https://jarvis2.kehlet.dev/downloads/latest.json
```

## Smoke test

On Linux:

```bash
curl -I https://jarvis2.kehlet.dev/downloads/JarvisClient-linux-x64
curl -fsSL https://jarvis2.kehlet.dev/downloads/SHA256SUMS
curl -fsSL https://jarvis2.kehlet.dev/downloads/latest.json
```

Then download and run:

```bash
curl -fsSLO https://jarvis2.kehlet.dev/downloads/JarvisClient-linux-x64
chmod +x JarvisClient-linux-x64
./JarvisClient-linux-x64 --version
./JarvisClient-linux-x64 --path ~/Projects
```

The client should show a key and log a successful connection to `https://jarvis2.kehlet.dev/client`.
