# Feature: Continue safely after interruption

- **Identity:** `telos.feature.recover`
- **Systems:** [Work](../systems/work.md), [Execution](../systems/execution.md), [Evidence](../systems/evidence.md)
- **Status:** Proposed enduring Feature contract

## Desired behavior

Telos should continue useful Work across ordinary model-turn endings, browser tab closure, client disconnection, daemon restart and restoration from a backup—without losing durable intent, duplicating an externally visible action, or pretending that an uncertain operation never happened.

Execution Sessions are ephemeral. Checkpoints and authoritative Work state describe completed steps, actual evidence and the next coherent action; no permanent chat tab is needed to preserve execution. Closing a successfully submitted browser conversation must not be interpreted as canceling remote execution, nor may tab presence alone establish liveness.

Successful and uncertain external effects remain distinguishable. If a command, browser submission, Git update or integration is in doubt, Telos should use the operation's identity and independently available evidence to reconcile its outcome. Until then, only the affected effect is fenced from replay. Independent safe Work may continue.

A backup may lag local authoritative state. Restoration must identify uncertain reservations, claims, launches and effects before resuming them, rather than equating missing restored records with proof of nonexecution. Ordinary restart should not require restoring from backup when durable local state remains intact. Recovery must respect the same source, authorization, evidence and concurrency rules as ordinary execution.

## Observable satisfaction

A bounded execution ends after checkpointing its progress; a subsequent execution resumes the same Work from that state without repeating completed actions. A browser tab closes after initial submission and the remote model still completes; Telos does not treat tab disappearance as permission to resubmit. After a simulated stale-backup restore, an unresolved push remains fenced while an unrelated task is still eligible to proceed.

**Not implied:** replaying unknown effects, keeping all tabs open, pretending stale backup snapshots are current, or granting recovery a new technical implementation mandate.
