# Source revision is not bound to the scheduled execution

**Severity: Major / changes requested before promoting the cross-project recurring execution path.**

Frozen commit: `67427229bb05fdb3100655ca8f2568049f0b9431`; baseline: `a83549a6897a4e26aa4834ff81dc38b6e16ee85e`.

The new persisted `ScheduledAction` in `Client/Modules/BrowserSchedules.fs` stores `SourceProjectName` and `ScriptPath`, but no immutable script revision/digest. `Client/Program.fs:252-264` resolves `action.SourceProjectName` and reads the current contents of that path on *each* recurring occurrence. This can execute changed Servitor launcher bytes under an existing schedule revision/approval, even though the task contract explicitly says to preserve the exact script version/provenance. The new source permission prompt is project/path-scoped, not a proof of a specific source version.

**Requested:** Define and enforce the intended source-version boundary for this default-off path: pin/check a source revision or digest at registration/rebind and fail closed (or require explicit reauthorization) if it changes. If the intended design is deliberately live-following script content, obtain an explicit contract clarification from Lead and document that version/provenance semantics, rather than claiming an exact pinned version. No live schedule mutation or rollout is authorized by this finding.