#!/bin/sh
# Root-owned, noninteractive updater. The sudoers entry supplies no arguments.
set -eu
umask 077

RELEASE_BASE=https://jarvis2.kehlet.dev/downloads
ROOT=/opt/jarvis
LIVE=$ROOT/server/JarvisServer
PREVIOUS=$LIVE.prev
STAGE=$ROOT/server/.JarvisServer.next
STATUS=/var/lib/jarvis/server-update-status
PENDING=/var/lib/jarvis-update-state/pending
LOCK=/run/jarvis-server-update.lock
SERVICE=jarvis.service
# Matches jarvis.service ASPNETCORE_URLS and nginx's local upstream.
PROBE_BASE=http://127.0.0.1:5095
PROBE_HOST=jarvis2.kehlet.dev
PROC_ROOT=/proc
PROBE_WINDOW_SECONDS=35
# Allow systemd's configured 90-second stop timeout, with a bounded margin.
STOP_TIMEOUT_SECONDS=100
ARTIFACT=JarvisServer-linux-x64

# Refuse concurrent runs. The lock inode is persistent, unlike atomic status.
exec 9>"$LOCK"
flock -n 9 || exit 1

WORK=$(mktemp -d /tmp/jarvis-server-update.XXXXXXXX)
failure_stage=setup
transaction=0
status_done=0
previous_sha=
last_probe=not-started
backup_stage=

atomic_record () {
    target=$1
    mode=$2
    value=$3
    temp=$(mktemp "$target.tmp.XXXXXXXX") || return 1
    if ! printf '%s\n' "$value" > "$temp" ||
       ! chmod "$mode" "$temp" ||
       ! mv -f "$temp" "$target"; then
        rm -f "$temp"
        return 1
    fi
}

record_status () {
    atomic_record "$STATUS" 0644 "$1"
}

valid_hash () {
    [ "$(printf %s "$1" | wc -c)" -eq 64 ] || return 1
    case "$1" in *[!0123456789abcdef]*) return 1 ;; esac
}

file_sha () {
    sha256sum "$1" 2>/dev/null | cut -d ' ' -f 1
}

# /proc/<pid>/exe refers to the running inode after a path replacement.
active_process () {
    service_time=$((probe_deadline - $(date +%s)))
    if [ "$service_time" -le 0 ]; then last_probe=deadline; return 1; fi
    [ "$service_time" -le 3 ] || service_time=3
    timeout -k 1 "$service_time" systemctl is-active --quiet "$SERVICE" || return 1
    service_time=$((probe_deadline - $(date +%s)))
    if [ "$service_time" -le 0 ]; then last_probe=deadline; return 1; fi
    [ "$service_time" -le 3 ] || service_time=3
    current_pid=$(timeout -k 1 "$service_time" systemctl show "$SERVICE" -p MainPID --value 2>/dev/null) || return 1
    case "$current_pid" in ''|0|*[!0-9]*) return 1 ;; esac
    [ -r "$PROC_ROOT/$current_pid/exe" ] || return 1
}

# Fixed Host, direct loopback, no proxy/redirect and a global time budget.
probe_http () {
    expected_code=$1
    endpoint=$2
    method=$3
    left=$((probe_deadline - $(date +%s)))
    if [ "$left" -le 0 ]; then
        last_probe=deadline
        return 1
    fi
    [ "$left" -le 3 ] || left=3
    if [ "$method" = POST ]; then
        code=$(curl -sS --noproxy '*' --connect-timeout 1 --max-time "$left" --max-filesize 16384 \
            -H "Host: $PROBE_HOST" -H 'Accept: application/json, text/event-stream' \
            -H 'Content-Type: application/json' \
            -X POST --data-binary '{}' -D "$WORK/headers" -o "$WORK/body" \
            -w '%{http_code}' "$PROBE_BASE$endpoint" 2>/dev/null) || return 1
    else
        code=$(curl -sS --noproxy '*' --connect-timeout 1 --max-time "$left" --max-filesize 16384 \
            -H "Host: $PROBE_HOST" -D "$WORK/headers" -o "$WORK/body" \
            -w '%{http_code}' "$PROBE_BASE$endpoint" 2>/dev/null) || return 1
    fi
    [ "$code" = "$expected_code" ]
}

probe_once () {
    expected_sha=$1
    last_probe=service-inactive
    active_process || return 1
    observed_pid=$current_pid
    last_probe=executable-mismatch
    # A stalled filesystem read remains inside the shared readiness deadline.
    hash_time=$((probe_deadline - $(date +%s)))
    if [ "$hash_time" -le 0 ]; then last_probe=deadline; return 1; fi
    [ "$hash_time" -le 5 ] || hash_time=5
    observed_sha=$(timeout -k 1 "$hash_time" sha256sum "$PROC_ROOT/$observed_pid/exe" 2>/dev/null | cut -d ' ' -f 1) || return 1
    [ "$observed_sha" = "$expected_sha" ] || return 1

    # The service MainPID must own the listener receiving these HTTP probes.
    # A stale unrelated process on the configured listener port must not qualify.
    last_probe=listener-owner
    listener_time=$((probe_deadline - $(date +%s)))
    if [ "$listener_time" -le 0 ]; then last_probe=deadline; return 1; fi
    [ "$listener_time" -le 3 ] || listener_time=3
    probe_port=$(printf '%s' "$PROBE_BASE" | sed 's/.*://')
    case "$probe_port" in ''|*[!0-9]*) return 1 ;; esac
    timeout -k 1 "$listener_time" ss -H -lntp "sport = :$probe_port" > "$WORK/listener" 2>/dev/null || return 1
    # The owned socket must accept the configured loopback port, not merely share its port
    # on a different interface while another process handles the HTTP request.
    awk -v pid="$observed_pid" -v port="$probe_port" '
        ($4 == "127.0.0.1:" port || $4 == "0.0.0.0:" port ||
         $4 == "*:" port || $4 == "[::]:" port || $4 == ":::" port) &&
        index($0, "pid=" pid ",") > 0 { matched = 1 }
        END { exit(matched ? 0 : 1) }' "$WORK/listener" || return 1

    last_probe=oauth-metadata
    probe_http 200 '/.well-known/oauth-protected-resource' GET || return 1
    grep -Eq '"resource"[[:space:]]*:[[:space:]]*"https://jarvis2[.]kehlet[.]dev"' "$WORK/body" || return 1
    grep -Eq '"authorization_servers"[[:space:]]*:' "$WORK/body" || return 1

    last_probe=mcp-auth
    probe_http 401 '/mcp' POST || return 1
    grep -Eiq '^www-authenticate:[[:space:]]*Bearer' "$WORK/headers" || return 1

    last_probe=signalr-auth
    probe_http 401 '/client/negotiate?negotiateVersion=1' POST || return 1
    grep -Eiq '^www-authenticate:[[:space:]]*Bearer' "$WORK/headers" || return 1
    grep -Fq 'client:connect' "$WORK/headers" || return 1

    last_probe=pid-changed
    active_process || return 1
    [ "$current_pid" = "$observed_pid" ] || return 1
    return 0
}

wait_ready () {
    wanted_sha=$1
    probe_deadline=$(($(date +%s) + PROBE_WINDOW_SECONDS))
    informative_failure=deadline
    while [ "$(date +%s)" -lt "$probe_deadline" ]; do
        if probe_once "$wanted_sha"; then return 0; fi
        if [ "$last_probe" != deadline ]; then
            informative_failure=$last_probe
        fi
        [ "$(date +%s)" -lt "$probe_deadline" ] || break
        sleep 1
    done
    last_probe=$informative_failure
    return 1
}

# Recheck recovery using the old executable and all protocol endpoints.
# Failed recovery leaves PENDING intact for operator-assisted intervention.
restore_previous () {
    # If final status publishing failed after clearing the old journal,
    # restore rollback intent before mutating service state again.
    if [ ! -e "$PENDING" ] && valid_hash "$previous_sha"; then
        if ! atomic_record "$PENDING" 0600 "$previous_sha"; then
            record_status "failed: stage=$failure_stage rollback=journal-create-failed" || :
            status_done=1
            return 1
        fi
    fi
    if ! valid_hash "$previous_sha" ||
       [ ! -f "$PREVIOUS" ]; then
        record_status "failed: stage=$failure_stage rollback=missing-or-invalid-previous" || :
        status_done=1
        return 1
    fi
    prev_actual=$(file_sha "$PREVIOUS") || prev_actual=
    if [ "$prev_actual" != "$previous_sha" ]; then
        record_status "failed: stage=$failure_stage rollback=checksum-mismatch" || :
        status_done=1
        return 1
    fi
    if ! timeout -k 2 "$STOP_TIMEOUT_SECONDS" systemctl stop "$SERVICE" >/dev/null 2>&1; then
        record_status "failed: stage=$failure_stage rollback=stop-failed" || :
        status_done=1
        return 1
    fi
    if ! cp -p "$PREVIOUS" "$STAGE" || ! mv -f "$STAGE" "$LIVE"; then
        record_status "failed: stage=$failure_stage rollback=restore-failed" || :
        status_done=1
        return 1
    fi
    if ! timeout -k 2 30 systemctl restart "$SERVICE" >/dev/null 2>&1; then
        record_status "failed: stage=$failure_stage rollback=restart-failed" || :
        status_done=1
        return 1
    fi
    if ! wait_ready "$previous_sha"; then
        record_status "failed: stage=$failure_stage rollback=unhealthy-$last_probe" || :
        status_done=1
        return 1
    fi
    if ! rm -f "$PENDING"; then
        record_status "failed: stage=$failure_stage rollback=journal-cleanup-failed" || :
        status_done=1
        return 1
    fi
    transaction=0
    record_status "failed: stage=$failure_stage rollback=recovered previous=$previous_sha" || :
    status_done=1
    return 0
}

on_exit () {
    result=$?
    trap - EXIT HUP INT TERM
    set +e
    if [ "$result" -ne 0 ]; then
        if [ "$transaction" -eq 1 ]; then
            restore_previous
        elif [ "$status_done" -eq 0 ]; then
            record_status "failed: stage=$failure_stage rollback=not-needed"
        fi
    fi
    [ -z "$backup_stage" ] || rm -f "$backup_stage"
    rm -rf "$WORK"
    exit "$result"
}
trap on_exit EXIT
trap 'failure_stage=interrupted; exit 130' HUP INT TERM

# SIGKILL/power loss cannot run traps; recover before replacing old backup.
if [ -e "$PENDING" ]; then
    previous_sha=$(cat "$PENDING" 2>/dev/null) || previous_sha=
    failure_stage=interrupted-update
    transaction=1
    exit 1
fi

failure_stage=status
record_status 'running stage=preflight' || exit 1
failure_stage=preflight-missing-live
[ -f "$LIVE" ] || exit 1
mkdir -p "$(dirname "$STAGE")" || exit 1

failure_stage=preflight-checksum
previous_sha=$(file_sha "$LIVE") || exit 1
valid_hash "$previous_sha" || exit 1

failure_stage=preflight-unhealthy
if ! wait_ready "$previous_sha"; then
    failure_stage="preflight-unhealthy-$last_probe"
    exit 1
fi

failure_stage=manifest
record_status 'running stage=download' || exit 1
curl -fsSL --proto '=https' --proto-redir '=https' --retry 2 \
    --max-time 30 --max-filesize 131072 "$RELEASE_BASE/SHA256SUMS" -o "$WORK/SHA256SUMS" || exit 1
failure_stage=checksum-entry
expected=$(awk '$2 == "JarvisServer-linux-x64" {
    count++
    if (count != 1 || length($1) != 64 || $1 !~ /^[0-9a-fA-F]+$/) exit 2
    hash = tolower($1)
} END { if (count != 1) exit 2; print hash }' "$WORK/SHA256SUMS") || exit 1
valid_hash "$expected" || exit 1

failure_stage=download
curl -fsSL --proto '=https' --proto-redir '=https' --retry 2 \
    --max-time 60 --max-filesize 300000000 \
    "$RELEASE_BASE/$ARTIFACT" -o "$WORK/candidate" || exit 1
failure_stage=checksum-mismatch
actual=$(file_sha "$WORK/candidate") || exit 1
[ "$actual" = "$expected" ] || exit 1

if [ "$actual" = "$previous_sha" ]; then
    record_status "success unchanged=$actual" || exit 1
    status_done=1
    exit 0
fi

failure_stage=staging
install -m 0755 "$WORK/candidate" "$STAGE" || exit 1
sync -f "$STAGE" || exit 1
failure_stage=backup
backup_stage=$(mktemp "$PREVIOUS.staging.XXXXXXXX") || exit 1
cp -p "$LIVE" "$backup_stage" || exit 1
[ "$(file_sha "$backup_stage")" = "$previous_sha" ] || exit 1
sync -f "$backup_stage" || exit 1
mv -f "$backup_stage" "$PREVIOUS" || exit 1

failure_stage=journal
atomic_record "$PENDING" 0600 "$previous_sha" || exit 1
transaction=1

failure_stage=stop
timeout -k 2 "$STOP_TIMEOUT_SECONDS" systemctl stop "$SERVICE" >/dev/null 2>&1 || exit 1
failure_stage=replace
mv -f "$STAGE" "$LIVE" || exit 1
failure_stage=restart
timeout -k 2 30 systemctl restart "$SERVICE" >/dev/null 2>&1 || exit 1

failure_stage=readiness
record_status 'running stage=readiness' || exit 1
if ! wait_ready "$actual"; then
    failure_stage="readiness-$last_probe"
    exit 1
fi
failure_stage=journal-cleanup
rm -f "$PENDING" || exit 1
failure_stage=success-status
record_status "success sha256=$actual previous=$previous_sha" || exit 1
transaction=0
status_done=1
