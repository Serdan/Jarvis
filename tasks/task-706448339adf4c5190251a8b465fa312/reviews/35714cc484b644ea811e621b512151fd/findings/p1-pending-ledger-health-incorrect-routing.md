# P1 — Pending ledger-health job incorrectly authorizes cross-project device routing

**Frozen source:** `2349cb9c1e5b9ccdd15817224146882534039988`, `refs/heads/feature/auto-promote-tool-jobs`.

## Precise control-flow proof

1. `Server/Services/ClientService.fs:13–17` defines the per-device **verified destination ledger health probe** as `LedgerReadCommand { Action = "Health"; ProjectName = destination }`. `SendCommandToProject` at lines **239–249** sends this probe before choosing a device for a cross-project command (especially `NotifyAgent`). However its success branch matches **only** `health.Result, health.Error` with `| Some _, None ->` and dispatches the potentially mutating command. It never checks `health.PendingJob` or parses the result as an actual verified ledger health outcome.
2. `Client/SignalR/Client.fs:777–788` does **not** exclude `LedgerReadCommand` from automatic promotion. At **3 seconds** the same module (lines **823–839**) returns `Result = Some(JsonSerializer.Serialize handle)`, `Error = None`, `PendingJob = Some handle`, with the ledger-health operation **still running**.
3. Consequently, an ordinary slow destination health probe is accepted as a successful verified ledger binding **before it can determine whether that device owns the destination project's verified ledger**. The pending handle is just as syntactically successful as a completed health result in this branch.

This defeats the explicit cross-project routing safeguard. For two eligible connected devices, a selected device whose ledger probe is merely slow (or would eventually fail binding validation) is chosen instead of waiting for proof or moving to an actually healthy candidate; a mutation can be sent to an unverified device. The latter client's own checks may reject some invalid projects, but they do not substitute for the server's required verified ledger binding selection.

## Required correction

Treat `PendingJob.IsSome` as **not yet validated**. Resolve the precise original ledger-health job **on the same candidate device and actor**, using bounded polling and its typed terminal response; verify its real health success/error before routing the command. Do not blindly issue another independent health probe or select a different device after an uncertain write has begun. If the health job cannot be resolved, fail closed with an actionable candidate/device error rather than using its pending handle as proof.

Add a routed Server regression with two connected candidates in which the preferred candidate's health probe returns a **Running tool-job handle**, then later returns an unhealthy ledger response. Assert that **zero** destination writes are sent to the unverified candidate and that fallback/denial occurs only from a final verified result; also test a pending probe that eventually succeeds.

**Severity: P1** — authorization/routing correctness regression introduced by extending automatic promotion to the very health probe that guards cross-project notification dispatch. No claim of a production exploit or actual incorrect ledger write; this is a deterministic code-path defect.
