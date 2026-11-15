# P1 — Completed GetJobResult drops and consumes new operator alerts

**Frozen revision:** `29bb8f5bcc352b8fd43bc7f7f7efbc9d72b0b4c0`

## Defect

A pending response alert is **durably consumed but never included in the MCP response** when `get_job_result` polls a job that already has a completed `ToolResponse`.

- `Client/SignalR/Client.fs:1118–1123`: the `GetJobResultCommand` response runs `checkResponseAlerts`, which prepends fresh alert documents to the *poll* response's `Instructions`.
- `Client/Modules/ReviewWorkflow.fs:1579–1601`: `takeResponseAlerts` commits an empty pending queue before returning the documents.
- `Server/McpTools.fs:482–493`: `completedToolEvidence` replaces the poll response with `job.ToolResponse` if a completed original response exists; it does **not** merge the poll's freshly supplied `Instructions`.
- `Server/McpTools.fs:922–930`: MCP `attachInstructions` uses `evidence.Instructions`, now the old original job instructions, not the just-consumed STOP.

## Reproduction scenario

1. Let a tool operation complete with no custom alert during its original execution, but do not poll its finished job yet.
2. Queue `STOP — preserve safe work, yield and end this turn` for the same project/agent.
3. Have that agent call `get_job_result` on the completed job.
4. The client clears the pending STOP from the Git ledger while constructing the poll response. The server then substitutes the completed job's older `ToolResponse` and omits the STOP.
5. A later call cannot recover it: the alert is no longer pending.

This is an internal drop on a successful delivery path, not an uncertain/lost transport response. It violates the core job-polling/next-response acceptance requirement.

## Required correction

Preserve **both** the completed job's original tool result/error and state-change evidence **and** the poll envelope's newly injected alert instructions. STOP must remain prominent before other instructions. Alternatively, delay consuming alerts until the final MCP response path can present them.

Add a server integration/regression test with a **successful completed** `GetJobResultCommand` containing `Some job.ToolResponse`, with a newly queued alert at poll time. Assert the actual MCP content includes the STOP, while the original job payload and state evidence remain intact. Existing `ResponseAlertsReachErroredPollAndUnrelatedToolResponsesWithoutActivity` covers a *missing-job error*, not this completed-job case.