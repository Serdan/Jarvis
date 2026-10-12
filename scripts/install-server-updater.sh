#!/bin/sh
set -eu

# Install as root on the production host after reviewing this source.
install -o root -g root -m 0755 /dev/stdin /usr/local/sbin/jarvis-update-server <<'HELPER'
#!/bin/sh
set -eu
umask 022
URL=https://jarvis2.kehlet.dev/downloads
ROOT=/opt/jarvis
LIVE=$ROOT/server/JarvisServer
PREV=$LIVE.prev
NEXT=$ROOT/deploy-next/JarvisServer
STATUS=/var/lib/jarvis/server-update-status
WORK=$(mktemp -d /tmp/jarvis-server-update.XXXXXXXX)
trap 'rm -rf "$WORK"' EXIT HUP INT TERM
printf 'running\n' > "$STATUS"
fail() { printf 'failed: %s\n' "$1" > "$STATUS"; exit 1; }
curl -fsSL --retry 3 "$URL/SHA256SUMS" -o "$WORK/SHA256SUMS" || fail manifest
expected=$(awk '$2 == "JarvisServer-linux-x64" && $1 ~ /^[0-9a-fA-F]+$/ && length($1) == 64 { print tolower($1) }' "$WORK/SHA256SUMS")
[ "${#expected}" -eq 64 ] || fail checksum-entry
curl -fsSL --retry 3 --max-filesize 300000000 "$URL/JarvisServer-linux-x64" -o "$WORK/JarvisServer" || fail download
actual=$(sha256sum "$WORK/JarvisServer" | cut -d ' ' -f 1)
[ "$actual" = "$expected" ] || fail checksum-mismatch
# Only touch live files after the entire download verifies.
install -o jarvis -g jarvis -m 0755 "$WORK/JarvisServer" "$NEXT" || fail staging
cp -a "$LIVE" "$PREV" || fail backup
mv -f "$NEXT" "$LIVE" || fail replace
if ! systemctl restart jarvis.service; then
    cp -a "$PREV" "$LIVE"
    systemctl restart jarvis.service || true
    fail restart
fi
sleep 3
if ! systemctl is-active --quiet jarvis.service; then
    cp -a "$PREV" "$LIVE"
    systemctl restart jarvis.service || true
    fail health-check
fi
printf 'success %s\n' "$actual" > "$STATUS"
HELPER
install -o root -g root -m 0440 /dev/stdin /etc/sudoers.d/jarvis-server-update <<'SUDOERS'
jarvis ALL=(root) NOPASSWD: /usr/bin/systemd-run --unit=jarvis-server-update --collect /usr/local/sbin/jarvis-update-server
SUDOERS
visudo -cf /etc/sudoers.d/jarvis-server-update
touch /var/lib/jarvis/server-update-status
chown jarvis:jarvis /var/lib/jarvis/server-update-status
chmod 0644 /var/lib/jarvis/server-update-status
