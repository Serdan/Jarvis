# Context-efficiency registered-route harness

**Status: representative default-threshold and ceiling runs qualified; low-threshold stress run passed without promoting a handle.**

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

From the exact source checkout, with a harness project whose project
references resolve to that checkout:

    dotnet run --project scripts/perf/context-efficiency-20261127/ContextEfficiency.Harness.fsproj -c Release -- \
      --source-root "$PWD" --history 220 --transport-delay-ms 20 \
      --out /tmp/jarvis-context-baseline.json

For the representative baseline, use the full workflow and exact fixture scale:

    dotnet run --project scripts/perf/context-efficiency-20261127/ContextEfficiency.Harness.fsproj -c Release -- \
      --source-root "$PWD" --history 526 --representative --transport-delay-ms 20 \
      --out /tmp/jarvis-context-representative.json

The representative fixture is designed for 539 total tasks (526 historical, 12
frontier, one inherited setup task), 731 reviews (729 historical, two frontier), twelve observations,
and one canonical pending integration. These are intended fixture counts;
verify them against actual route evidence before accepting measurements.

The same project can be compiled against a frozen, clean candidate source:

    dotnet run --project scripts/perf/context-efficiency-20261127/ContextEfficiency.Harness.fsproj -c Release \
      -p:JarvisClientProject="/path/to/candidate/Client/Client.fsproj" \
      -p:JarvisServerProject="/path/to/candidate/Server/Server.fsproj" -- \
      --source-root /path/to/candidate --history 526 --representative --transport-delay-ms 20 \
      --out /tmp/jarvis-context-candidate.json

The 900 KiB legacy overview limit is exercised only with a disposable fixture:

    dotnet run --project scripts/perf/context-efficiency-20261127/ContextEfficiency.Harness.fsproj -c Release -- \
      --source-root "$PWD" --history 2200 --transport-delay-ms 20 --ceiling-only \
      --out /tmp/jarvis-context-ceiling.json

Summarize and compare:

    python3 scripts/perf/context-efficiency-20261127/summarize.py /tmp/jarvis-context-baseline.json
    python3 scripts/perf/context-efficiency-20261127/summarize.py /tmp/jarvis-context-representative.json /tmp/jarvis-context-candidate.json

The 220-history prototype completed successfully (19 routes, no pending handles). The revised representative and ceiling commands both completed with exit 0 and passed the strict summarizer. The representative run has 27 successful routes; the ceiling run reproduces the intended OutputTruncated error. See the baseline contract for exact receipts and metrics. The default 3-second client promotion threshold is retained for primary measurements. Separately qualify real job handles with a disposable process-only `JARVIS_TOOL_JOB_THRESHOLD_MS=250` prefix; report whether promotion occurred and never conflate its timings with default-threshold runs. Keep the report,
its SHA-256, the exact source and harness revision, and the terminal process
receipt. Do not equate an isolated projection test with a registered route
or a registered in-process test with a deployed host.

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
