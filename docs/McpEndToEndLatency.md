# End-to-end MCP latency: opt-in stage diagnostics and pinned fixtures

## Scope and safety

This work is source-only. It does not enable production diagnostics, deploy a client/server, activate a scheduler, or alter main. The existing performance regression harness remains the baseline; this adds stage attribution and other hot-path goldens without BenchmarkDotNet or cross-host absolute latency gates.

Set `JARVIS_MCP_STAGE_TRACE=1` **on both the server and selected client process** before launching them to emit stage logs. Unset it (the default) to retain the existing slow-only telemetry. Do not attach request or response bodies, project paths, Git arguments, actor IDs, credentials, URLs, or source documents to a trace.

The normal **server** dispatch histogram and log already expose bounded-cardinality `command`/`outcome` and `routeMs`, `signalrSendMs`, `replyWaitMs`, and `totalMs`. The opt-in enables per-request emission regardless of duration. A separate `MCP envelope` log measures the authenticated route plus response materialization and uses the same server-issued UUID where available. It is deliberately named **fullRequestMs**, not auth-only or network RTT.

The **client** reports the bounded command queue wait, execution and reply, JSON command parse, `client.execute_or_promote`, `signalr.reply_ack`, and aggregated Git stages (`git.fetch`, `git.ls-tree`, `git.batch`, `git.cat-file`, `git.show`, `git.other`, `git.worktree`). Stage names and outcomes are whitelisted and never built from caller text. It records each stage's invocation count and monotonic elapsed milliseconds, with an opaque, UUID-validated correlation ID and fixed outcome labels. Stage measurement and JSON reporting are disabled by default. The original command/task/review authority and typed response are not changed.

**Interpretation:** compare stages within a host using monotonic timestamps; never subtract client time from server time to infer one-way latency because clocks are independent and dispatch/ack overlaps. The request envelope includes authorization and response materialization; the client execution stage includes policy, guidance, and ledger work, not Git alone. The 3-second client promotion threshold can return a **pending** job: its first response is not the terminal execution duration; later `GetJobResult` polling has a distinct tool correlation. Log ingestion, browser-to-MCP overhead and hardware/OS comparisons require separately controlled measurements.

## Reproducible .NET 11 Release goldens

Run the `Client.Tests` and `Server.Tests` suites with `-c Release` on a clean isolated checkout. The reviewer queue fixture uses an entirely disposable local Git repository, pinned immutable OIDs, record populations of 0/220/2200, cold/warm reads and a published synthetic head advance. It exercises `GetTaskWithSpec` and `GetBranchReviewAt` at a pinned snapshot and checks exact ordered reviewer-queue parity before/after an unrelated task head advance. The compile-source SHA and fixture head SHAs appear in the test report. The 30 warm repetitions produce **informational** p50/p95/p99 and memory/size stats; only deterministic counts are gates: reviewer queue at most 12 Git child starts, one remote fetch, one blob batch, one individual blob read, and a size-proportional stdout bound per call; GetTaskWithSpec at most 16 Git child starts and one fetch. These controls are not calibrated to live MCP endpoint latency.

The reviewer queue now selects exactly review-ending paths into a snapshot-pinned blob batch, preserving malformed selected records as errors instead of doing a Git fetch or read per review. The bulk `ListWorktrees` discovery now uses a single Git `worktree list --porcelain`, leaving `IsClean=None` (unknown) rather than pretending cleanliness. Explicit `GetWorktree` still inspects exactly the selected tree, and `InspectWorktreeRemoval` always performs an independent current `git status` and ancestry/job safety check. Do not treat an unknown status as clean.

The disposable Git child control runs real `git -c alias.pause=!sleep ... pause` processes alongside independent real `git status`, and cancels the delayed process. It is POSIX-only; Windows parity is skipped until a separately authorized KRUSO run. Cancellation/termination are correctness gates, not cross-host latency comparisons.

No claim of causal improvement to live GetTask/Review (~0.8–1.3 seconds observed previously) or ListWorktrees (~3 seconds observed previously) is justified by the synthetic fixtures alone. A future authorized deployment can compare same-host before/after interleaved traces with equivalent workloads, command counts and network conditions.


## Independent-review P1 correction

The original ChangesRequested review 8a981fa4cc894b29951879fd69350288 on
fcadb38ed2312e961e1329f54ca1bbf6e56df7cb identified two P1 gaps.

TaskOverviewBatchPerformanceTests.OtherHotPathsHavePopulationPinnedBoundedGoldens
uses synthetic populations of 0/220/2200 task, review and notification records
with cold/warm/repeated and a published unrelated synthetic head advance.
Selected GetTaskWithSpec is bounded to 16 Git processes and one fetch;
pinned ReviewDetails to four processes and zero fetch; ChallengeList and
notification AgentInbox to twenty processes and one fetch; default-off
publication preflight to one process and one fetch; and unknown JobResult
polling to zero Git starts, fetches or stdout. All have bounded batch/single
blob and population-scaled stdout ceilings. Two independent disposable clones
concurrently read the same bare remote with one fetch each; they do not mutate
live ledger data. Existing ReviewerInbox 0/220/2200 controls remain.

WorktreeTests.source_pinned_worktree_bulk_read_has_one_git_command_across_synthetic_populations
checks 0/220/2200 synthetic porcelain records with the real parser and one
real disposable Git worktree list command using the opt-in stage trace. This
is a parser workload, not a claim to have created 2200 physical worktrees.
Production bulk output retains the fixed 256 KiB cap.

ActualDelayedRemoteFetchAndCancellationDoNotBlockPinnedReadsOrRetry uses a
REAL Git fetch from a local bare remote with a controlled upload-pack
executable. A file handshake verifies the remote child is blocked before
the pinned LedgerStore read; the latter must succeed with zero extra fetches.
The delayed read must perform one remote fetch, and cancellation of a second
actual remote fetch must leave no stage ref or repeated transport attempt.
Subsequent recovery fetch is independently checked. This POSIX test is
separate from the older local alias sleep control. A one-second remote delay
and file synchronization are used without absolute wall-time CI thresholds.

The acceptance evidence must be rerun against the committed exact .NET 11
Release checkout SHA, with synthetic fixture heads and operation counts.
It does not prove live MCP wall-clock improvement, full-system privacy
coverage, or KRUSO/Windows parity.
