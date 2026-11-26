# Git-call baseline harness

The harness is intentionally isolated from production code. It adds one focused
NUnit fixture to `Client.Tests` only when the comparison script creates its
temporary worktrees; it does not alter either candidate commit.

Run a paired comparison from any Jarvis checkout containing this directory:

```sh
python3 scripts/tests/git-call-baseline-20261126/compare.py \
  --candidate-sha <exact-candidate-commit>
```

The default baseline is the freshly fetched `origin/main` commit
`9dc95184896118c96f34493da28ce40bf5b3d8d6`. The script creates detached
temporary worktrees for both SHAs, injects the same test file into each,
runs the Release harness, verifies the report source SHAs, compares semantic
digests and durable ledger effects, then removes the worktrees.

Use `--include-focused-suite` to additionally run the existing focused
TaskOverview, AgentInbox population, mutation-stage, Worktree, and JobManager
suites. This is still a set of focused filters, not a full combined suite.

Use `--allow-cleanliness-unknown` only when reviewing an intentional
`WorktreeInfo.IsClean: Some bool -> None` contract change. The comparison
still requires identical worktree identity/head/branch/flags. Returning
`None` is acceptable as unknown; returning an unverified `true` is not.

Reports include:
- AgentInbox and TaskOverview cold/warm result digests, runner counters, and
  actual Git Trace2 process/fetch counts;
- uncertain publication, operation-status reconciliation, idempotent replay,
  operation-reuse rejection, and normalized canonical ledger tree digest;
- real worktree discovery process fanout plus parser populations 0/220/2200;
- a POSIX local `file://` transport case with a 50 ms delayed
  `git-upload-pack` wrapper, proving the measurement includes actual remote
  helper execution rather than only in-process counters.

Wall-clock timings are reported, not used as cross-host gates. Semantic result
classes, normalized payload digests, canonical ref identity/tree effects,
negative-control outcomes, and actual process/fetch/push counts are the
comparison evidence.
