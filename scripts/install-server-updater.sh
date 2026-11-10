#!/bin/sh
set -eu

# Install this wrapper, not just the helper binary: deployment requires the
# root-owned rollback journal directory and the dedicated sudoers entry.
# Install from the checked-out, reviewed sources as root on the server.
# The privileged helper has fixed paths and does not accept arguments or
# environment-provided overrides from the unprivileged MCP caller.
here=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
install -o root -g root -m 0755 "$here/jarvis-update-server.sh" /usr/local/sbin/jarvis-update-server.new
mv -f /usr/local/sbin/jarvis-update-server.new /usr/local/sbin/jarvis-update-server
install -o root -g root -m 0440 /dev/stdin /etc/sudoers.d/jarvis-server-update <<'SUDOERS'
jarvis ALL=(root) NOPASSWD: /usr/bin/systemd-run --unit=jarvis-server-update --collect /usr/local/sbin/jarvis-update-server
SUDOERS
visudo -cf /etc/sudoers.d/jarvis-server-update
install -d -m 0755 /var/lib/jarvis
install -d -o root -g root -m 0700 /var/lib/jarvis-update-state
if [ ! -e /var/lib/jarvis/server-update-status ]; then
    printf 'not-run\n' > /var/lib/jarvis/server-update-status
fi
chown jarvis:jarvis /var/lib/jarvis/server-update-status
chmod 0644 /var/lib/jarvis/server-update-status
