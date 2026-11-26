import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import qualify
import inspect_evidence



# Shape observed in retained Linux VSTest TRX (including zero outcome counters).
VSTEST_COUNTERS = dict(total="1", executed="1", passed="1", failed="0",
    error="0", timeout="0", aborted="0", inconclusive="0", passedButRunAborted="0",
    notRunnable="0", notExecuted="0", disconnected="0", warning="0",
    completed="0", inProgress="0", pending="0")

def fixture_trx(outcomes=("Passed",), summary="Completed", changes=None, missing=()):
    counters = dict(VSTEST_COUNTERS)
    counters.update(changes or {})
    for key in missing:
        counters.pop(key)
    attrs = " ".join(f'{k}="{v}"' for k, v in counters.items())
    rows = "".join(f'<UnitTestResult outcome="{o}"/>' for o in outcomes)
    summary_attr = "" if summary is None else f' outcome="{summary}"'
    return (f'<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
            f'<Results>{rows}</Results><ResultSummary{summary_attr}>'
            f'<Counters {attrs}/></ResultSummary></TestRun>')

def wait_marker(path, seconds=5):
    import time
    end = time.monotonic() + seconds
    while not path.exists() and time.monotonic() < end:
        time.sleep(0.02)
    return path.exists()

# Same-group child with an externally controlled release and a finite backstop.
GATED_CHILD = """
import pathlib, sys, time
ready, release, late = map(pathlib.Path, sys.argv[1:])
ready.touch()
end = time.monotonic() + 8
while not release.exists() and time.monotonic() < end:
    time.sleep(0.02)
if release.exists():
    late.write_text("finished")
"""

class HarnessTests(unittest.TestCase):
    def test_isolation_and_git_commit_tree(self):
        with tempfile.TemporaryDirectory() as d, patch.dict(os.environ, {
            "GIT_CONFIG_COUNT": "1", "GIT_DIR": "/bad", "OPENAI_API_KEY": "fake",
        }):
            root = Path(d)
            env = qualify.isolated_env(root)
            for key in ("GIT_CONFIG_COUNT", "GIT_DIR", "OPENAI_API_KEY"):
                self.assertNotIn(key, env)
            qualify.checked(["git", "init", "-q"], root, env)
            tree = qualify.checked(["git", "mktree"], root, env)
            commit = qualify.checked(["git", "commit-tree", tree, "-m", "fixture"], root, env)
            self.assertRegex(commit, r"^[0-9a-f]{40}$")

    def test_terminal_and_no_replay(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            env = qualify.isolated_env(root)
            args = [sys.executable, "-c", "print('evidence'); raise SystemExit(7)"]
            self.assertFalse(qualify.run_stage("red", args, 5, root, env, root))
            terminal = json.loads((root / "red/terminal.json").read_text())
            self.assertEqual(terminal["exit_code"], 7)
            self.assertEqual(terminal["sha256"]["console.log"],
                             qualify.sha(root / "red/console.log"))
            self.assertEqual(terminal["trx"]["state"], "missing")
            with self.assertRaises(FileExistsError):
                qualify.run_stage("red", args, 5, root, env, root)

    def test_deadline_is_not_green(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            env = qualify.isolated_env(root)
            self.assertFalse(qualify.run_stage("timeout",
                [sys.executable, "-c", "import time; time.sleep(30)"],
                0.1, root, env, root))
            terminal = json.loads((root / "timeout/terminal.json").read_text())
            self.assertEqual(terminal["state"], "timeout")

    def test_trx_raw_counter_disagreement_preserved(self):
        with tempfile.TemporaryDirectory() as d:
            path = Path(d) / "raw.trx"
            path.write_text('<TestRun><Results><UnitTestResult outcome="Passed"/>'
                            '<UnitTestResult outcome="NotExecuted"/></Results>'
                            '<ResultSummary outcome="Completed"><Counters total="1" '
                            'passed="1" notExecuted="0"/></ResultSummary></TestRun>')
            summary = qualify.trx_summary(path)
            self.assertEqual(summary["rows"], 2)
            self.assertEqual(summary["counters"]["notExecuted"], "0")
            self.assertEqual(summary["outcomes"]["NotExecuted"], 1)
            self.assertFalse(qualify.consistent_trx(summary))


    def test_vstest_summary_and_counter_contract(self):
        with tempfile.TemporaryDirectory() as d:
            path = Path(d) / "test.trx"
            cases = [
                ("completed", {}, True),
                ("consistent skip", {"outcomes": ("Passed", "NotExecuted"),
                     "changes": {"total": "2", "notExecuted": "1"}}, True),
                ("failed summary", {"summary": "Failed"}, False),
                ("error", {"changes": {"error": "1"}}, False),
                ("failed and error", {"summary": "Failed", "changes": {"error": "1"}}, False),
                ("timeout", {"changes": {"timeout": "1"}}, False),
                ("aborted", {"changes": {"aborted": "1"}}, False),
                ("passed but aborted", {"changes": {"passedButRunAborted": "1"}}, False),
                ("disconnected", {"changes": {"disconnected": "1"}}, False),
                ("unsupported counter", {"changes": {"futureOutcome": "0"}}, False),
                ("unknown row", {"outcomes": ("Unknown",)}, False),
                ("executed mismatch", {"changes": {"executed": "0"}}, False),
                ("row mismatch", {"changes": {"total": "2"}}, False),
                ("missing executed", {"missing": ("executed",)}, False),
                ("missing error", {"missing": ("error",)}, False),
                ("missing summary", {"summary": None}, False),
                ("unknown summary", {"summary": "Unknown"}, False),
                ("missing skip counter", {"missing": ("notExecuted",)}, False),
                ("warning outcome unsupported", {"changes": {"warning": "1"}}, False),
                ("failed row", {"outcomes": ("Failed",),
                     "changes": {"passed": "0", "failed": "1"}}, False),
                ("negative", {"changes": {"executed": "-1"}}, False),
                ("noninteger", {"changes": {"executed": "1.0"}}, False),
                ("historical client shape", {"summary": "Failed",
                     "outcomes": ("Passed", "NotExecuted"),
                     "changes": {"total": "2"}}, False),
            ]
            for label, args, expected in cases:
                with self.subTest(label=label):
                    raw = fixture_trx(**args)
                    path.write_text(raw)
                    self.assertEqual(qualify.consistent_trx(qualify.trx_summary(path)), expected)
                    self.assertEqual(path.read_text(), raw)

    def test_same_group_survivor_cannot_publish_stable_manifest(self):
        for code in (0, 7):
            with self.subTest(leader_exit=code):
                with tempfile.TemporaryDirectory() as d:
                    root = Path(d)
                    env = qualify.isolated_env(root)
                    ready, release = root / "ready", root / "release"
                    stage = root / "survivor"
                    late = stage / "late.txt"
                    leader = """
import pathlib, subprocess, sys, time
child, ready, release, late, trx, code = sys.argv[1:]
subprocess.Popen([sys.executable, "-c", child, ready, release, late])
end = time.monotonic() + 3
while not pathlib.Path(ready).exists() and time.monotonic() < end:
    time.sleep(0.02)
assert pathlib.Path(ready).exists()
pathlib.Path(late).with_name("survivor.trx").write_text(trx)
raise SystemExit(int(code))
"""
                    try:
                        result = qualify.run_stage("survivor",
                            [sys.executable, "-c", leader, GATED_CHILD, str(ready),
                             str(release), str(late), fixture_trx(), str(code)], 5, root, env, root)
                        self.assertTrue(ready.exists())
                        terminal = json.loads((stage / "terminal.json").read_text())
                        self.assertEqual(terminal["exit_code"], code)
                        self.assertFalse(result)
                        self.assertNotEqual(terminal["state"], "exited")
                        self.assertNotIn("sha256", terminal)
                        self.assertFalse(late.exists())
                    finally:
                        release.touch()
                        # A bounded natural drain is permitted; fixture cannot wait indefinitely.
                        wait_marker(late, 3)

    def test_timeout_with_same_group_child_is_non_candidate(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            env = qualify.isolated_env(root)
            ready, release, late = root / "ready", root / "release", root / "late"
            leader = """
import pathlib, signal, subprocess, sys, time
signal.signal(signal.SIGTERM, signal.SIG_IGN)
child, ready, release, late = sys.argv[1:]
subprocess.Popen([sys.executable, "-c", child, ready, release, late])
end = time.monotonic() + 8
while time.monotonic() < end:
    time.sleep(0.02)
"""
            try:
                self.assertFalse(qualify.run_stage("timeout-child",
                    [sys.executable, "-c", leader, GATED_CHILD, str(ready),
                     str(release), str(late)], 1, root, env, root))
                self.assertTrue(ready.exists())
                terminal = json.loads((root / "timeout-child/terminal.json").read_text())
                self.assertNotEqual(terminal["state"], "exited")
                release.touch()
                self.assertFalse(wait_marker(late, 0.3))
            finally:
                release.touch()

    def test_plan_is_nonexecuting_and_requires_exact_commit(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            output = root / "output"
            cmd = [sys.executable, str(Path(qualify.__file__)), "--source",
                   str(root / "source"), "--output", str(output), "--commit"]
            result = subprocess.run(cmd + ["a" * 40], capture_output=True, text=True)
            self.assertEqual(result.returncode, 0)
            self.assertEqual(len(json.loads(result.stdout)["matrix"]), len(qualify.PROJECTS))
            self.assertFalse(output.exists())
            self.assertNotEqual(subprocess.run(cmd + ["main"], capture_output=True).returncode, 0)
            self.assertNotEqual(subprocess.run(cmd + ["a" * 40, "--execute"],
                                               capture_output=True).returncode, 0)
            self.assertFalse(output.exists())

    def test_reconnect_observations_do_not_replay(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            env = qualify.isolated_env(root)
            evidence = root / 'evidence'
            evidence.mkdir()
            qualify.receipt(evidence / 'run-started.json', {'matrix': [
                {'name': 'complete'}, {'name': 'interrupted'}, {'name': 'later'}]})
            (evidence / 'interrupted').mkdir()
            qualify.run_stage('complete', [sys.executable, '-c', 'print(1)'],
                              3, root, env, evidence)
            report = inspect_evidence.inspect_run(root)
            self.assertIsNone(report['run_terminal'])
            states = {k: v['state'] for k, v in report['stage_observations'].items()}
            self.assertEqual(states, {'complete': 'receipt_readback',
                                     'interrupted': 'unknown_no_terminal_receipt',
                                     'later': 'not_started'})
            (evidence / 'complete' / 'console.log').write_text('tampered')
            self.assertEqual(inspect_evidence.inspect_stage(evidence / 'complete')['state'],
                             'hash_mismatch')
            self.assertFalse((evidence / 'later').exists())

    def test_restart_probe_release_and_no_replay(self):
        import time
        import uuid
        with tempfile.TemporaryDirectory() as d:
            root = Path(d) / 'probe'
            nonce = str(uuid.uuid4())
            args = [sys.executable, '-B', str(Path(qualify.__file__).with_name('restart_probe.py')),
                    '--output', str(root), '--nonce', nonce, '--deadline', '5']
            with subprocess.Popen(args, stdout=subprocess.PIPE, stderr=subprocess.PIPE) as child:
                try:
                    end = time.monotonic() + 3
                    while not (root / 'probe-started.json').exists() and time.monotonic() < end:
                        time.sleep(0.02)
                    self.assertTrue((root / 'probe-started.json').exists())
                    qualify.receipt(root / 'release.json', {'nonce': nonce})
                    out, err = child.communicate(timeout=5)
                    self.assertEqual(child.returncode, 17, err)
                    self.assertIn(nonce.encode(), out)
                finally:
                    if child.poll() is None:
                        child.kill()
            original = qualify.sha(root / 'probe-exit-intent.json')
            self.assertNotEqual(subprocess.run(args, capture_output=True, timeout=3).returncode, 0)
            self.assertEqual(qualify.sha(root / 'probe-exit-intent.json'), original)


    def test_serial_matrix_with_fake_dotnet_and_exact_clone(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            source = root / 'source'
            source.mkdir()
            prep = root / 'prep'
            prep.mkdir()
            env = qualify.isolated_env(prep)
            qualify.checked(['git', 'init', '-q'], source, env)
            for name in ('one.fsproj', 'two.fsproj'):
                (source / name).write_text('fake project')
            qualify.checked(['git', 'add', '.'], source, env)
            qualify.checked(['git', 'commit', '-qm', 'fake source'], source, env)
            commit = qualify.checked(['git', 'rev-parse', 'HEAD'], source, env)
            binpath = root / 'bin'
            binpath.mkdir()
            dotnet = binpath / 'dotnet'
            dotnet.write_text('#!' + sys.executable + '\n'
                'import sys, pathlib\n'
                'args = sys.argv[1:]\n'
                'if args[0] in ("--version", "--info"):\n'
                '    print("11.0.100 fake"); sys.exit(0)\n'
                'assert args[0] == "test"\n'
                'out = pathlib.Path(args[args.index("--results-directory") + 1])\n'
                'name = args[args.index("--logger") + 1].split("=")[1]\n'
                'assert pathlib.Path(args[1]).is_file()\n'
                '(out / name).write_text(' + repr(fixture_trx()) + ')\n')
            dotnet.chmod(0o700)
            output = root / 'run'
            argv = ['qualify.py', '--source', str(source), '--commit', commit,
                    '--output', str(output), '--execute', '--approval', 'self-test-only']
            with patch.object(sys, 'argv', argv), patch.object(qualify, 'PROJECTS', [
                    ('one', 'one.fsproj', 5), ('two', 'two.fsproj', 5)]), patch.dict(os.environ, {
                    'PATH': str(binpath) + os.pathsep + os.environ['PATH']}):
                self.assertEqual(qualify.main(), 0)
                with self.assertRaises(FileExistsError):
                    qualify.main()
            report = inspect_evidence.inspect_run(output)
            self.assertEqual(report['source']['commit'], commit)
            self.assertEqual(report['run_terminal']['state'], 'review_required')
            self.assertTrue(all(s['state'] == 'receipt_readback' for s in report['stage_observations'].values()))
            self.assertEqual(qualify.checked(['git', 'status', '--porcelain'], source, env), '')

            # The fake first leader exits 0 with good TRX, but the original
            # group still contains a marker-gated child. Never launch stage two.
            ready, release = root / "matrix-ready", root / "matrix-release"
            output = root / "run-survivor"
            late = output / "evidence/one/late.txt"
            with dotnet.open("a") as script:
                script.write(
                    "if args[1] == 'one.fsproj':\n"
                    "    import subprocess, time\n"
                    "    ready, release, late = map(pathlib.Path, " +
                    repr([str(ready), str(release), str(late)]) + ")\n"
                    "    subprocess.Popen([sys.executable, '-c', " +
                    repr(GATED_CHILD) + ", str(ready), str(release), str(late)])\n"
                    "    end = time.monotonic() + 3\n"
                    "    while not ready.exists() and time.monotonic() < end:\n"
                    "        time.sleep(0.02)\n"
                    "    assert ready.exists()\n")
            argv[argv.index('--output') + 1] = str(output)
            try:
                with patch.object(sys, 'argv', argv), patch.object(qualify, 'PROJECTS', [
                        ('one', 'one.fsproj', 5), ('two', 'two.fsproj', 5)]), patch.dict(os.environ, {
                        'PATH': str(binpath) + os.pathsep + os.environ['PATH']}):
                    self.assertEqual(qualify.main(), 1)
                self.assertTrue(ready.exists())
                report = inspect_evidence.inspect_run(output)
                self.assertEqual(report['run_terminal']['state'], 'red_or_incomplete')
                self.assertEqual(report['stage_observations']['one']['state'], 'terminal_without_hashes')
                self.assertEqual(report['stage_observations']['two']['state'], 'not_started')
                self.assertFalse(late.exists())
            finally:
                release.touch()
                wait_marker(late, 3)


if __name__ == "__main__":
    unittest.main()
