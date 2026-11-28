# Headless Linux client

This describes the device-enrollment and unattended startup workflow. Use a release that includes the reviewed device authorization implementation; the older client cannot perform this workflow.

## Prerequisites

- A Linux x86-64 self-contained client built for the intended Jarvis server (see [ClientDownloads.md](ClientDownloads.md)). No local browser or .NET SDK is required.
- The existing Auth0 public client application must allow Authorization Code (for existing interactive clients), Device Code, and Refresh Token grants. Its token endpoint authentication method must permit a public client without a client secret. The Jarvis API must allow offline access and the existing `client:connect` scope. The same account, issuer, audience and client ID are used for device and browser authentication. Do not create another machine identity or install a client secret.
- The existing application settings were inspected read-only on 2026-11-28: the Authorization Code, Device Code, and Refresh Token grant checkboxes were enabled. The API offline access setting and token endpoint authentication method were not verified in that view; the foreground enrollment reports a rejected grant or missing refresh token if these prerequisites are not met.
- A dedicated non-root service account with access to the intended project workspace and a private writable home/config directory. Select the existing trust policy deliberately. An unattended process cannot respond to interactive local approval prompts; do not broaden trust as a side effect of authentication.

## Install

Download the reviewed Linux client and its matching checksum manifest using the existing release channel. Verify the checksum before installation. Example from a private temporary directory:

```sh
curl -fsSLO https://jarvis2.kehlet.dev/downloads/JarvisClient-linux-x64
curl -fsSLO https://jarvis2.kehlet.dev/downloads/SHA256SUMS
grep -E ' [*]?JarvisClient-linux-x64$' SHA256SUMS | sha256sum --check
```

Install it as `/opt/jarvis/JarvisClient` using your normal provisioning process. If using the existing in-place update capability, the service user must have the explicitly required write access to the binary and its parent directory; otherwise use operator-managed binary replacement. Keep the executable directory separate from the workspace. Do not run the client as root on the Proxmox host.

The example below assumes service user `jarvis`, home `/var/lib/jarvis`, project workspace `/srv/jarvis/projects`, and `XDG_CONFIG_HOME=/var/lib/jarvis/.config`. Adapt these to your installation and create the directories with correct ownership. For the config directory use owner-only permissions (0700).

## Enroll from a foreground terminal

Stop any service instance using this credential store before enrolling, to avoid concurrent refresh writes. Run as the same user and with the same configuration as the service. In an SSH session allocate a terminal (`ssh -t`); do not redirect enrollment output to a logfile or run it as a service:

```sh
sudo -u jarvis -H env XDG_CONFIG_HOME=/var/lib/jarvis/.config \
  /opt/jarvis/JarvisClient auth device
```

The foreground command displays a verification URL and user code. Open the URL in a browser on your other machine, enter the displayed code, and sign in to your existing Jarvis account. Verify the prompt is for the intended client. Do not share the code. The CLI polls Auth0 respecting the server's interval and slow-down responses; Ctrl+C cancels it. Denial or expiry requires starting a new enrollment. A successful enrollment prints a confirmation only after persisting the refresh token. It does not start the client or open a local browser.

The existing OAuth cache is `$XDG_CONFIG_HOME/Jarvis/oauth-refresh.json` (Linux defaults to `~/.config/Jarvis/oauth-refresh.json`). It is separate from the ordinary config and stored with owner read/write permissions (0600); access tokens and device codes are not persisted. A missing, insecure, or mismatched cache is not accepted. Never copy refresh tokens into a unit file, logs, or shell history. Keep one client process per credential directory. Auth0 issuer/audience/client ID overrides, if used, must be identical for enrollment and service startup.

## systemd example

Create an operator-reviewed unit at `/etc/systemd/system/jarvis-client.service`:

```ini
[Unit]
Description=Jarvis headless client
Wants=network-online.target
After=network-online.target

[Service]
Type=simple
User=jarvis
Group=jarvis
WorkingDirectory=/srv/jarvis/projects
Environment=HOME=/var/lib/jarvis
Environment=XDG_CONFIG_HOME=/var/lib/jarvis/.config
Environment=JARVIS_OAUTH_MODE=unattended
UMask=0077
ExecStart=/opt/jarvis/JarvisClient --path /srv/jarvis/projects
Restart=on-failure
RestartSec=30s
KillSignal=SIGINT
TimeoutStopSec=150s
StandardInput=null
StandardOutput=journal
StandardError=journal

[Install]
WantedBy=multi-user.target
```

`JARVIS_OAUTH_MODE=unattended` explicitly forbids opening a browser, even when stored credentials fail. Without this setting, the existing interactive browser flow remains the default. The unattended client restores the cached refresh token, obtains access tokens using the same account, and persists any rotated refresh token. Transient network errors can be retried by the service or the existing connection loop; missing/revoked credentials report that foreground reauthorization is required. `KillSignal=SIGINT` uses the client's existing cancellation path; the timeout allows its bounded dispatch drain.

After enrollment and explicit operator approval of the installation:

```sh
sudo systemctl daemon-reload
sudo systemctl enable --now jarvis-client.service
sudo systemctl status jarvis-client.service
sudo journalctl -u jarvis-client.service -n 100 --no-pager
```

The service should connect without opening a browser. Check the client registration under the existing account; restart the service and verify it restores the session without enrollment. A real device enrollment and service smoke test are deployment validation steps, not part of fake-endpoint tests.

If the logs say reauthorization is required, stop the service, repeat foreground enrollment with the same user and configuration, and start the service again. If storage permissions are rejected, repair the service user's private directory and file ownership; do not relax token file permissions. Do not delete a previously valid cache in response to a transient network failure.

No Auth0 settings or host configuration are changed by implementing or testing this feature.
