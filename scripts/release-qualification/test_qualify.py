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
                '(out / name).write_text(\'<TestRun><Results><UnitTestResult outcome="Passed"/>\''
                ' \'</Results><ResultSummary><Counters total="1" passed="1" failed="0" notExecuted="0"/>\''
                ' \'</ResultSummary></TestRun>\')\n')
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

if __name__ == "__main__":
    unittest.main()
