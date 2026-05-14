# Client Downloads Deployment

Jarvis clients are distributed as self-contained single-file executables. The client has the SignalR server URL embedded at publish time, so each public build must be stamped with the production hub URL.

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
