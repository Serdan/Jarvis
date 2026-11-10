#!/usr/bin/env python3
"""Black-box deployment/rollback harness; never touches a real systemd unit."""
import hashlib
import json
import os
from pathlib import Path
import shlex
import shutil
import subprocess
import tempfile
import unittest

SCRIPT = Path(__file__).with_name("jarvis-update-server.sh")
OLD = b"jarvis-old-binary-fixture\n"
NEW = b"jarvis-new-binary-fixture\n"

COMMON = """import json,os,sys,hashlib,shutil
from pathlib import Path
base=Path(os.environ['MOCK_ROOT'])
state_path=base/'state.json'
state=json.loads(state_path.read_text())
def save():
    state_path.write_text(json.dumps(state))
def running():
    return (base/'proc/4242/exe').read_bytes()
"""

SYSTEMCTL = """
import time
args=sys.argv[1:]
cmd=args[0]
if cmd=='is-active':
    delay=state.get('startup_delay_left',0)
    if delay:
        state['startup_delay_left']=delay-1
        save()
        sys.exit(1)
    sys.exit(0 if state.get('active',False) else 1)
if cmd=='show':
    print('4242')
    sys.exit(0)
if cmd=='stop':
    delay=state.get('stop_delay',0)
    if delay: time.sleep(delay)
    state['stops']=state.get('stops',0)+1
    state['active']=False
    save()
    sys.exit(0)
if cmd=='restart':
    state['restarts']=state.get('restarts',0)+1
    live=(base/'install/server/JarvisServer').read_bytes()
    state['active']=True
    if live==b'jarvis-old-binary-fixture\\n' and state.get('fail_old_restart'):
        state['active']=False
        save()
        sys.exit(1)
    if live==b'jarvis-new-binary-fixture\\n':
        if state.get('fail_new_restart'):
            state['active']=False
            save()
            sys.exit(1)
        state['startup_delay_left']=state.get('new_startup_delay',0)
        if state.get('remove_previous_on_new_start'):
            (base/'install/server/JarvisServer.prev').unlink(missing_ok=True)
        if state.get('leave_stale_process'):
            save()
            sys.exit(0)
    running_file=base/('running-'+str(state['restarts']))
    running_file.write_bytes(live)
    link=base/'proc/4242/exe'
    link.unlink()
    link.symlink_to(running_file)
    save()
    sys.exit(0)
sys.exit(4)
"""

CURL = """
args=sys.argv[1:]
url=next((arg for arg in args if arg.startswith(('https://','http://'))), '')
if '-o' not in args: sys.exit(5)
out=Path(args[args.index('-o')+1])
if url.endswith('/SHA256SUMS'):
    shutil.copyfile(base/'release/SHA256SUMS',out)
    sys.exit(0)
if url.endswith('/JarvisServer-linux-x64'):
    shutil.copyfile(base/'release/JarvisServer-linux-x64',out)
    sys.exit(0)
if '--noproxy' not in args or '-L' in args or '-X' not in args and '/mcp' in url:
    sys.exit(6)
if '-D' not in args or '-w' not in args: sys.exit(7)
headers=Path(args[args.index('-D')+1])
live=running()
status=200
body=''
auth=''
if '/.well-known/oauth-protected-resource' in url:
    body='{"resource":"https://jarvis2.kehlet.dev","authorization_servers":["https://issuer.example"]}'
elif '/mcp' in url:
    if 'Content-Type: application/json' not in args:
        status=415
    else:
        status=401
        auth='WWW-Authenticate: Bearer resource_metadata="https://jarvis2.kehlet.dev/.well-known/oauth-protected-resource"\\r\\n'
elif '/client/negotiate' in url:
    status=401
    auth='WWW-Authenticate: Bearer resource_metadata="https://jarvis2.kehlet.dev/.well-known/oauth-protected-resource", scope="client:connect"\\r\\n'
else:
    sys.exit(8)
if live==b'jarvis-new-binary-fixture\\n':
    if state.get('transient_new_failures',0)>0:
        state['transient_new_failures']-=1
        save()
        status=503
        auth=''
        body=''
    if state.get('new_fail_route') and state['new_fail_route'] in url:
        status=503
        auth=''
        body=''
headers.write_text(f'HTTP/1.1 {status} probe\\r\\n'+auth+'\\r\\n')
out.write_text(body)
sys.stdout.write(str(status))
"""


MV = """
args=sys.argv[1:]
if len(args)<2: sys.exit(4)
source=Path(args[-2])
destination=Path(args[-1])
if destination==base/'status/server-update-status' and state.get('fail_final_status_once',False):
    content=source.read_text()
    if content.startswith('success sha256='):
        state['fail_final_status_once']=False
        save()
        sys.exit(1)
os.execv('/usr/bin/mv',['mv']+args)
"""


SS = """
if 'sport = :5095' not in sys.argv[1:]:
    sys.exit(9)
active_pid='777' if (running()==b'jarvis-new-binary-fixture\\n' and state.get('wrong_listener_owner')) else '4242'
local_address='192.168.1.10:5095' if (running()==b'jarvis-new-binary-fixture\\n' and state.get('wrong_listener_address')) else '127.0.0.1:5095'
print(f'LISTEN 0 512 {local_address} 0.0.0.0:* users:(("JarvisServer",pid={active_pid},fd=12))')
"""

class Fixture(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix="jarvis-updater-harness-")
        self.base = Path(self.tmp.name)
        self.root = self.base / "install"
        (self.root / "server").mkdir(parents=True)
        (self.root / "deploy-next").mkdir(parents=True)
        (self.base / "release").mkdir()
        (self.base / "bin").mkdir()
        (self.base / "proc/4242").mkdir(parents=True)
        (self.base / "status").mkdir()
        self.live = self.root / "server/JarvisServer"
        self.previous = self.root / "server/JarvisServer.prev"
        self.pending = self.base / "status/server-update-pending"
        self.status = self.base / "status/server-update-status"
        self.live.write_bytes(OLD)
        self.live.chmod(0o755)
        original = self.base / "running-original"
        original.write_bytes(OLD)
        (self.base / "proc/4242/exe").symlink_to(original)
        self.write_release(NEW)
        self.state = dict(active=True, starts=0, stops=0, restarts=0)
        self.update_state()
        for name, body in [("systemctl", SYSTEMCTL), ("curl", CURL), ("mv", MV), ("ss", SS)]:
            target = self.base / "bin" / name
            target.write_text("#!/usr/bin/env python3\n" + COMMON + body)
            target.chmod(0o755)

        script = SCRIPT.read_text()
        changes = {
            "ROOT=/opt/jarvis": "ROOT=" + shlex.quote(str(self.root)),
            "STATUS=/var/lib/jarvis/server-update-status": "STATUS=" + shlex.quote(str(self.status)),
            "PENDING=/var/lib/jarvis-update-state/pending": "PENDING=" + shlex.quote(str(self.pending)),
            "LOCK=/run/jarvis-server-update.lock": "LOCK=" + shlex.quote(str(self.base / "status/server-update.lock")),
            "PROC_ROOT=/proc": "PROC_ROOT=" + shlex.quote(str(self.base / "proc")),
            "PROBE_WINDOW_SECONDS=35": "PROBE_WINDOW_SECONDS=4"
        }
        for old, new in changes.items():
            self.assertEqual(script.count(old), 1, old)
            script = script.replace(old, new, 1)
        self.runner = self.base / "updater-under-test.sh"
        self.runner.write_text(script)
        self.runner.chmod(0o755)

    def tearDown(self):
        self.tmp.cleanup()

    def update_state(self, **changes):
        self.state.update(changes)
        (self.base / "state.json").write_text(json.dumps(self.state))

    def read_state(self):
        return json.loads((self.base / "state.json").read_text())

    def write_release(self, content, manifest_override=None):
        (self.base / "release/JarvisServer-linux-x64").write_bytes(content)
        digest = hashlib.sha256(content).hexdigest()
        (self.base / "release/SHA256SUMS").write_text(
            manifest_override if manifest_override is not None
            else f"{digest}  JarvisServer-linux-x64\n"
        )
        return digest

    def run_update(self):
        env = os.environ.copy()
        env["MOCK_ROOT"] = str(self.base)
        env["PATH"] = str(self.base / "bin") + os.pathsep + env["PATH"]
        return subprocess.run(
            ["/bin/sh", str(self.runner)], cwd=self.base, env=env,
            capture_output=True, text=True, timeout=50
        )

    def assert_no_transaction(self):
        self.assertFalse(self.pending.exists())
        self.assertFalse(list((self.root / "server").glob("*.staging.*")))

    def test_wrong_probe_port_fails_closed_before_stopping_service(self):
        script = self.runner.read_text()
        expected = "PROBE_BASE=http://127.0.0.1:5095"
        self.assertEqual(script.count(expected), 1)
        self.runner.write_text(script.replace(expected, "PROBE_BASE=http://127.0.0.1:5000"))
        result = self.run_update()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("stage=preflight-unhealthy-listener-owner rollback=not-needed", self.status.read_text())
        self.assertEqual(self.live.read_bytes(), OLD)
        self.assertEqual(self.read_state()["stops"], 0)
        self.assert_no_transaction()

    def test_mcp_probe_without_json_media_type_fails_closed(self):
        script = self.runner.read_text()
        expected = "-H 'Content-Type: application/json'"
        self.assertEqual(script.count(expected), 1)
        self.runner.write_text(script.replace(expected, ""))
        result = self.run_update()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("stage=preflight-unhealthy-mcp-auth rollback=not-needed", self.status.read_text())
        self.assertEqual(self.live.read_bytes(), OLD)
        self.assertEqual(self.read_state()["stops"], 0)
        self.assert_no_transaction()

    def test_stop_budget_allows_graceful_shutdown_and_covers_rollback(self):
        script = self.runner.read_text()
        # Source default covers production systemd's 90-second stop policy.
        self.assertEqual(script.count("STOP_TIMEOUT_SECONDS=100"), 1)
        self.assertEqual(
            script.count('timeout -k 2 "$STOP_TIMEOUT_SECONDS" systemctl stop'), 2
        )
        # Scale the timeout and simulated graceful stop for fast fixture testing.
        self.runner.write_text(script.replace("STOP_TIMEOUT_SECONDS=100", "STOP_TIMEOUT_SECONDS=3"))
        self.update_state(stop_delay=1)
        result = self.run_update()
        self.assertEqual(result.returncode, 0, self.status.read_text() + result.stderr)
        self.assertEqual(self.live.read_bytes(), NEW)
        self.assertEqual(self.read_state()["stops"], 1)
        self.assert_no_transaction()

    def test_success_confirms_live_binary_and_protocol_then_keeps_previous(self):
        result = self.run_update()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.live.read_bytes(), NEW)
        self.assertEqual(self.previous.read_bytes(), OLD)
        self.assertIn("success sha256=", self.status.read_text())
        self.assertEqual(self.status.stat().st_mode & 0o777, 0o644)
        self.assertEqual(self.read_state()["restarts"], 1)
        self.assert_no_transaction()

    def test_same_version_does_not_restart(self):
        self.write_release(OLD)
        result = self.run_update()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("success unchanged=", self.status.read_text())
        self.assertEqual(self.read_state()["restarts"], 0)
        self.assertFalse(self.previous.exists())

    def test_manifest_rejected_before_live_changes(self):
        self.write_release(NEW, "invalid  JarvisServer-linux-x64\n")
        result = self.run_update()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("stage=checksum-entry rollback=not-needed", self.status.read_text())
        self.assertEqual(self.live.read_bytes(), OLD)
        self.assertFalse(self.previous.exists())
        self.assertEqual(self.read_state()["stops"], 0)

    def test_payload_checksum_mismatch_is_non_destructive(self):
        self.write_release(NEW, hashlib.sha256(OLD).hexdigest() + "  JarvisServer-linux-x64\n")
        result = self.run_update()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("stage=checksum-mismatch", self.status.read_text())
        self.assertEqual(self.live.read_bytes(), OLD)
        self.assert_no_transaction()

    def test_active_but_mcp_broken_rolls_back_and_verifies_previous(self):
        self.update_state(new_fail_route="/mcp")
        result = self.run_update()
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.live.read_bytes(), OLD)
        self.assertIn("stage=readiness-mcp-auth rollback=recovered", self.status.read_text())
        self.assertGreaterEqual(self.read_state()["restarts"], 2)
        self.assert_no_transaction()

    def test_stale_running_executable_never_passes_new_hash_check(self):
        self.update_state(leave_stale_process=True)
        result = self.run_update()
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.live.read_bytes(), OLD)
        self.assertIn("stage=readiness-executable-mismatch rollback=recovered", self.status.read_text())
        self.assert_no_transaction()

    def test_short_transient_failures_are_retried(self):
        self.update_state(transient_new_failures=2)
        result = self.run_update()
        self.assertEqual(result.returncode, 0, self.status.read_text() + result.stderr)
        self.assertEqual(self.live.read_bytes(), NEW)
        self.assert_no_transaction()

    def test_delayed_startup_passes_within_budget(self):
        self.update_state(new_startup_delay=2)
        result = self.run_update()
        self.assertEqual(result.returncode, 0, self.status.read_text() + result.stderr)
        self.assertEqual(self.live.read_bytes(), NEW)

    def test_missing_rollback_binary_reports_failure_and_preserves_journal(self):
        self.update_state(new_fail_route="/mcp", remove_previous_on_new_start=True)
        result = self.run_update()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("rollback=missing-or-invalid-previous", self.status.read_text())
        self.assertTrue(self.pending.exists())

    def test_rollback_restart_failure_is_not_reported_recovered(self):
        self.update_state(new_fail_route="/mcp", fail_old_restart=True)
        result = self.run_update()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("rollback=restart-failed", self.status.read_text())
        self.assertTrue(self.pending.exists())

    def test_candidate_restart_failure_triggers_verified_rollback(self):
        self.update_state(fail_new_restart=True)
        result = self.run_update()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("stage=restart rollback=recovered", self.status.read_text())
        self.assertEqual(self.live.read_bytes(), OLD)
        self.assert_no_transaction()

    def test_broken_signalr_negotiation_rolls_back(self):
        self.update_state(new_fail_route="/client/negotiate")
        result = self.run_update()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("stage=readiness-signalr-auth rollback=recovered", self.status.read_text())
        self.assertEqual(self.live.read_bytes(), OLD)

    def test_duplicate_manifest_entries_fail_closed(self):
        digest=hashlib.sha256(NEW).hexdigest()
        self.write_release(NEW, f"{digest}  JarvisServer-linux-x64\n{digest}  JarvisServer-linux-x64\n")
        result = self.run_update()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("stage=checksum-entry rollback=not-needed", self.status.read_text())
        self.assertEqual(self.live.read_bytes(), OLD)

    def test_failed_final_success_status_rolls_back_instead_of_misreporting(self):
        self.update_state(fail_final_status_once=True)
        result = self.run_update()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("stage=success-status rollback=recovered", self.status.read_text())
        self.assertEqual(self.live.read_bytes(), OLD)
        self.assert_no_transaction()

    def test_candidate_cannot_borrow_an_unrelated_local_listener(self):
        self.update_state(wrong_listener_owner=True)
        result = self.run_update()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("stage=readiness-listener-owner rollback=recovered", self.status.read_text())
        self.assertEqual(self.live.read_bytes(), OLD)
        self.assert_no_transaction()

    def test_candidate_cannot_claim_non_loopback_socket_on_same_port(self):
        self.update_state(wrong_listener_address=True)
        result = self.run_update()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("stage=readiness-listener-owner rollback=recovered", self.status.read_text())
        self.assertEqual(self.live.read_bytes(), OLD)

    def test_interrupted_transaction_is_recovered_before_any_new_download(self):
        self.previous.write_bytes(OLD)
        self.live.write_bytes(NEW)
        existing = self.base / "running-before-recovery"
        existing.write_bytes(NEW)
        (self.base / "proc/4242/exe").unlink()
        (self.base / "proc/4242/exe").symlink_to(existing)
        self.pending.write_text(hashlib.sha256(OLD).hexdigest() + "\n")
        result = self.run_update()
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.live.read_bytes(), OLD)
        self.assertIn("stage=interrupted-update rollback=recovered", self.status.read_text())
        self.assert_no_transaction()

    def test_preexisting_unhealthy_service_is_not_used_as_rollback_baseline(self):
        self.update_state(active=False)
        result = self.run_update()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("stage=preflight-unhealthy-service-inactive", self.status.read_text())
        self.assertEqual(self.live.read_bytes(), OLD)
        self.assertFalse(self.previous.exists())

if __name__ == "__main__":
    unittest.main(verbosity=2)
