# P2 advisory: WQ07 Held is a reservation, not proof of WQ02 admission; WQ04's normal entry point is default-off

**Reviewed source:** `95410cc5eec2383f80160b5aaeb9b15660b45b59`, `docs/scheduler/capacity-authority-audit.md:7,19`.

## Evidence
- Line 7 describes a WQ07 `Held` token as one "Work Queue launch admitted" under WQ02. `AccountCapacityLedger.fs:898-931` establishes `Held` by account CAS *before* the project CAS. `AccountAdmissionAdapter.fs:382-442` reserves first, and returns `TokenRetained` if project load/commit is uncertain; therefore the token can remain Held even when no WQ02 project launch was admitted. The document correctly describes the two-step boundary elsewhere (lines 19, 43), so this is a terminology inconsistency rather than a protocol hole.
- Line 19's "WQ04 admits a Work Queue CreateTask" should distinguish design/opt-in behavior from deployed authority. The normal `AccountAdmissionAdapter.admit` returns fail-closed `TokenRetained` (`AccountAdmissionAdapter.fs:444-449`). The narrow source-proven `WorkQueuePracticalAdmission.admitExplicit` is explicitly enabled only by offline/test callers and not registered with a live Scheduler (`WorkQueuePracticalAdmission.fs:15-18,238-247,319-329`; `docs/WorkQueueWQ04PracticalAdmission.md:1-17`).

## Suggested precise wording
Describe a Held WQ07 token as **reserved account credit for a versioned WQ02 launch identity, not evidence that project admission or an external launch succeeded**. Qualify WQ04 admission as **explicit opt-in, default-off, CreateTask-only practical admission**, with the ordinary adapter's production entry point inert.

## Severity / disposition
**P2 advisory, nonblocking.** No material safety flaw in the proposed native architecture was found from this inconsistency: the audit otherwise requires a separate native type, project pre-send CAS, account capacity retention across uncertain outcomes, independent terminal evidence, and operator-gated activation. Correct these two phrases in a subsequent documentation edit, not by activating or repurposing any WQ07 path.
