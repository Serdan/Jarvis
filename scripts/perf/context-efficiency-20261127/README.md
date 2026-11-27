# Context-efficiency registered-route harness

**Status: original numeric representative and ceiling evidence retained with source-level qualification only. Corrected comparator and controlled provenance contract require independent review before release use.**

The reference runtime source is main commit
`291b5499051fbddfff1c76308e1a0202a5d1862f`.
This harness derives the disposable transport from the earlier
`scripts/perf/git-call-optimization-20261126/Program.fs` benchmark.
Only an in-process test hub, a synthetic user/device/project, a temporary
workspace, and a temporary local bare Git remote are used. Production MCP
methods, ClientService, bounded dispatch queue and client command processor
run against that fixture. There is no live ledger, browser, deployment,
provider, external remote or genuine user data.

## Reproduce

Use a clean committed harness checkout and clean runtime repository. The controlled
wrapper snapshots both exact commits into a new local evidence directory, builds
with explicit references to the runtime snapshot and fresh output, executes that
assembly directly, checks identities and output digests, and retains commands,
logs, reference paths, build/terminal exit receipts and report SHA-256. No live
ledger or deployment is involved. Keep the entire evidence directory (including
its local Git snapshots); missing or changed evidence is explicitly unqualified.
The local shared clone relies on the retained source repository's Git objects.

From the corrected harness checkout:

    python3 -B scripts/perf/context-efficiency-20261127/run_evidence.py \
      --source-root "$PWD" --source-sha 291b5499051fbddfff1c76308e1a0202a5d1862f \
      --history 526 --representative --transport-delay-ms 20 \
      --evidence-dir /tmp/jarvis-context-bound-baseline

Repeat sequentially with a different unused evidence directory and identical
parameters, or compare the same harness revision to a clean candidate checkout
by changing `--source-root` and `--source-sha` (defaults to that checkout's HEAD).
The representative fixture is 539 total tasks (526 historical, 12 frontier,
one inherited setup task), 731 reviews (729 historical, two frontier), twelve
observations and one canonical pending integration. Smaller runs omit
`--representative` and use e.g. `--history 220`; the ceiling-only legacy route
uses `--history 2200 --ceiling-only` in a separate disposable run.
For a distinct low-threshold run pass `--job-threshold-ms 250`; do not compare
its timings to default-threshold runs. A missing inner job promotion is an
explicit coverage gap.

    python3 -B scripts/perf/context-efficiency-20261127/summarize.py \
      /tmp/jarvis-context-bound-baseline/report.json
    python3 -B scripts/perf/context-efficiency-20261127/summarize.py \
      /tmp/jarvis-context-bound-baseline/report.json /tmp/jarvis-context-bound-repeat/report.json
    python3 -B scripts/perf/context-efficiency-20261127/test_summarize.py -v

The comparator verifies the explicit ordered route matrix per cycle (18/4/4/1
representative, 10/4/4/1 smaller, one ceiling route), expected terminal classes,
exact handle polls, and per-run activation head correlation before normalizing
the two validated run-specific receipt identities. Changes in assignment,
generation, spec, actor, state, flag or other stable semantic fields are not
masked. Direct `dotnet run` reports and original historical artifacts remain
available for diagnostics; the summarizer marks them unqualified for controlled
build provenance. It likewise marks stale or mismatched receipts unqualified.
The comparison-provenance flag requires both valid receipts, identical harness
revision and threshold. It is not a release verdict; no independent review,
provider telemetry, or deployed benefit is inferred from this flag.

## Metric boundaries

* Raw result bytes: UTF-8 bytes of the actual client response Result string.
* Serialized response bytes: UTF-8 length of the production typed
  AgentCommandResponse converter output (including escape expansion).
* Delivered text and MCP JSON bytes: sum of text blocks and the serialized
  CallToolResult object respectively. Excludes HTTP/RPC framing and any
  provider-side formatting. No provider token/cache telemetry is claimed.
* Terminal payload bytes: innermost terminal Result after resolving an actual
  tool-operation job, counted once; the surrounding job envelope is still
  counted in the raw/delivered measures. A truncation has no terminal payload;
  its reported pre-guard byte size is in the captured error, not a zero-size
  successful response.
* Instruction/spec segments: exact string content and its JSON-escaped bytes,
  by field path and SHA-256. The repeated-content metric counts every
  occurrence after the first identical content per cycle. It excludes
  protocol-level role caching and is a byte proxy only.
* Calls include every initial method and every GetJobResult poll of its exact
  returned handle; polls are never counted as new logical routes. Elapsed
  time is sequential end-to-end harness wall time, including controlled
  artificial one-way command delay and 50 ms poll delay where applicable,
  not real SignalR RTT or model inference.
* The comparator is deliberately strict for unchanged route shapes. A
  projection or prompt-first route replacement needs an independently
  reviewed workload-equivalence adapter: no favorable metric may be called
  a release win merely because the candidate returns less data.

## Evidence and remaining gate

See `docs/perf/context-efficiency-20261127/baseline-contract.md` for measured source-level baseline and exact terminal receipts. The separate low-threshold run completed successfully but produced zero handles or polls; actual inner job-wrapper coverage remains unqualified. Independent review remains required. Do not equate any source-level evidence with deployed benefit.
