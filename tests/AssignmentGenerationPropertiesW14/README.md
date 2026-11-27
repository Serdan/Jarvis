# Assignment-generation histories (Worker 14)

## Contract and gap

The contract is in `docs/OrchestrationMigration.md` (§4, steps 1–7 and the
strict nonzero version/generation assertions later in that document). A task
can have a new canonical HTML revision without silently changing a previously
issued generation. Explicit queued reissue or adoption issues a new generation
and records its pinned spec; starting that generation acknowledges the
issuance atomically. Yield and resume retain that generation; administrative
recovery issues a fresh generation for the same attempt. An exact UUID/argument
retry returns its original receipt, while a changed request with the same UUID
must not execute.

The pinned production baseline is remote main
`291b5499051fbddfff1c76308e1a0202a5d1862f`.
Existing `Client.Tests/ReviewWorkflowTests.fs` includes deterministic examples
for explicit reissue/adoption, owner transitions and individual recovery.
The existing challenge oracle, account CAS, publication-lineage and mixed
Health/Challenge/Scheduler property histories address different surfaces. This
suite explores repeated assignment issuance and separate spec revision/adoption
in interleaved two-owner histories, not policy switching or registered
Scheduler dispatch.

## Experiment

Two disposable Git clones share one local bare remote; the actual compiled
Client ledger workflow performs all mutations. The independent model describes
the generation as immutable data, a separate editable canonical HTML value,
one acknowledgment per started generation and an independent execution slot.
The model generates exact identity and version arguments; it does not use a
successful implementation read to choose the next transition. The verified
pre-operation ledger sequence supplies expected durable sequence numbers.

Both agents have distinct tasks and attempts. The raw record oracle checks
canonical HTML and its independently computed SHA-1 Git blob OID, exact task,
attempt and agent identities and versions, assignment history, acknowledgment
presence and contents, and yield/recovery checkpoints. Historical issue,
acknowledgment and checkpoint blob OIDs must remain unchanged, as must the
unrelated owner's raw records and the unchanged records of the target owner.
Rejections and exact receipt replays must leave the entire verified ledger head
and next sequence intact. A stale-generation test deliberately pairs an old
generation with *current* versions; stale attempt and agent versions are
separate partitions. Wrong owner, wrong phase, pending adoption and operation
UUID collisions have distinct negative paths.

The guided prefix guarantees the major interaction patterns in each ordinary
trial. Two seed-varied suffixes use different operation mixes (revision and
issuance versus execution and negative/replay cases). Coverage is measured from
actual outcomes and required tags, not merely generated commands. The suite
logs the realized trace and per-outcome counts. Failures report the first
divergent step and the concrete executed prefix.

## Reproduction

From an isolated checkout of the exact source SHA:

```sh
dotnet test tests/AssignmentGenerationPropertiesW14/AssignmentGenerationPropertiesW14.fsproj -c Release --filter FullyQualifiedName~GeneratedAssignmentHistories --logger 'console;verbosity=detailed' --logger 'trx;LogFileName=assignment-generation.trx'
```

Ordinary seeds are 1401 and 1402. A concrete trace can be replayed via
`JARVIS_W14_TRACE`; each semicolon-separated command is
`stepId,lane,verb,arg`. Replay and collision arguments reference earlier
successful logical step IDs. The input is explicit rather than dependent on
the implementation. Reduce a failing prefix by deleting unrelated operations
while keeping stable step IDs and retaining referenced successful operations;
a missing prerequisite is a fixture error, not evidence of the original
divergence. Replay mode intentionally does not require the full coverage set.

For example, the generation-fence calibration witness is
`1,0,reissue-new,0;2,0,stale-generation,0`.

```sh
python3 tests/AssignmentGenerationPropertiesW14/calibrate.py <exact-commit-sha>
```

The calibration driver archives the exact source locally into a temporary
directory, runs the minimized trace on unmodified production code, removes
only the generation comparison in `resumeAgentCore`, and runs the same trace.
It requires a red failure at the intended semantic assertion rather than a
build error or an unrelated exception. It restores the original file and
checks its SHA-256 before deleting the disposable copy. The registered
worktree's production files are not changed.

## Evidence status

Initial test-only executable commit:
`11a602ba7c7b2b39b7fe6b0acb57f83cd37e8c65`.

First actual Release test job: `d743723818cd4e468cf1adcb05e3c85d` using
the command above. Terminal evidence has not yet been retrieved. No pass,
realized coverage, mutant result or defect is claimed at this checkpoint.
The calibration script syntax and uniqueness of the mutation needle have been
checked; its actual production-code perturbation has not yet been executed.

## Boundaries

Sequential histories with alternating clients are not concurrent Git-CAS
schedules, transport/session-authentication, provider launches, or uncertain
network outcomes. Exact retries are executed, but a lost response is modeled
by ignoring the prior response rather than induced at the network layer.
The suite exercises the actual internal ledger methods used by the task tools;
it does not assert MCP wrapper authorization or live behavior. It has a bounded
two-owner domain, finite traces, and no claim of exhaustive correctness.
No production edit, deployment, activation, real provider effect or shared
fixture/catalog change is part of the deliverable.
