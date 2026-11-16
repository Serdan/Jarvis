# Reviewer 1 — changes requested

**Frozen source:** `377782ae44c14db8a9467abf105bf69a88e4215f` against `944bca7fa38cb5e973b04ce7829966cca351202b`; only `tools/Wq10MigrationPreview/{prepare.py,README.md,tests/test_prepare.py}` changed.

**Independent verification:** `.NET 11 Release` inspector build succeeded, 0 warnings/errors. `python3 -m unittest discover -s tools/Wq10MigrationPreview/tests -v` passed **40/40**. The new adapter remains default-off and only emits inert sidecars on disposable bare Git fixture refs; it does *not* claim canonical WQ02/WQ07 Prepared, enable Scheduler, allocate tokens, change epochs or launch browsers. The explicit missing-bindings disclosure is appropriate.

**Request changes (two independently reproduced defects):**

1. **P1 — Untrusted project name escapes the preparation write boundary.** Project display names are checked by the accepted inspector only for uniqueness/nonblank, but `prepare` interpolates them unescaped into `<operation-dir>/<name>.bundle` and `<name>.diff`. Using `../../escaped` generated a `PREVIEW_READY_FOR_REVIEW` report, completed the adapter successfully and wrote a bundle outside the operation directory. More traversal can escape the marked disposable root, and `.diff` writes can replace existing files. Use fixed safe authority filenames, strict resolved-path containment, and tests for traversal/reserved names.
2. **P2 — Abort deletes unrecognized staged evidence.** If another actor moves a prepared ref to a sibling commit with the same parent and original operation/input IDs but changed sidecar content, `abort` deletes it anyway without comparing to the receipt's exact `preparedHead`. Reproduced: unexpected ref was removed and command reported `ABORTED`. Refuse deletion of an unknown tip; preserve it for inspection/reconciliation and regression-test this path.

See `findings/unsanitized-project-name-escapes-disposable-root.md` and `findings/abort-deletes-unknown-sibling-commit.md` for reproductions, code locations and correction guidance. **Verdict: RequestChanges.** Do not integrate this frozen commit pending corrections and new independent review.