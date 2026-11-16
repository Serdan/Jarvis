# P1 — Contradictory canonical source-writer target ref still produces PREPARED_FOR_REVIEW

**Frozen source:** `62d9b0f400a1e1c01519f73af1a6faeae963b2e7`  
**Locations:** `tools/Wq10MigrationPreview/provenance.py:104-132`; independent legacy attempt refs are exposed by `tools/Wq10MigrationPreview/Program.cs:295-298`.  
**Task criterion:** positive agreement of selected work/source-writer lineage with the inspected legacy inventory and versioned origin before `PREPARED_FOR_REVIEW`.

`verify_candidate` only checks that `sourceLineage.repositoryId`, `targetRef`, and `lineageId` are nonempty. It checks `sourceLineage.taskId` against the legacy task, but **does not compare the source-writer target ref against independently inspected legacy attempt refs**. A candidate with contradictory lineage is marked `sourceProvenanceVerified=true`.

**Reviewer-run adversarial reproduction** (disposable two-project/two-clone fixtures, no project or production source modified):

1. Start the shipped green Alpha/Beta rehearsal fixture on a private disposable remote.
2. In Alpha's pinned `records/work-queue/project.json`, set `state.works.W-Alpha.sourceLineage.targetRef = "refs/heads/unrelated"`. Record the independent Alpha legacy attempt's `targetRef = "refs/heads/main"` in `tasks/T-alpha/attempts/111...json`.
3. Commit the fixture on the disposable Git remote; regenerate the WQ10 manifest and WQ08-shaped previews with matching exact new project heads. Keep the authentic source-origin blob, candidate UUID/hash, account cap, and required `AccountNotActivated` / `DispatchDisabled` diagnostics consistent.
4. Execute the unchanged frozen `rehearsal.py` through the fixture's `run_rehearsal` helper.

**Observed result (exit 0):** `rehearsalVerdict=PREPARED_FOR_REVIEW`, `blockerCodes=[]`, Alpha `sourceProvenanceVerified=true`, `sourceProvenanceFindings=[]`. Yet Alpha's queued writer targets `refs/heads/unrelated` while its separately inventoried source attempt targets `refs/heads/main`.

This is a false green source-lineage proof, not merely an untrusted-capture concern. The report explicitly claims same-head stage/source-writer consistency that its algorithm has not established. The affected tuple fields must be treated as identity, not shape-only text.

**Required correction:** Verify the selected stage's source-writer lineage, including target ref and repository/lineage identity where available, against the independently inventoried original task/attempt evidence (and relevant immutable origin). Where that evidence is insufficient or contradictory, emit a source-provenance blocker / NO_GO, not a prepared verdict. Add a two-project negative regression for this exact target-ref mismatch. Preserve the offline/read-only boundary and `productionCutover=NOT_AUTHORIZED`.