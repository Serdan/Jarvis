# Reviewer 1 — WQ10 prepared-migration r2

**Verdict: Changes requested** on frozen `7305fc733185cd14367b972a30b1294b047703de` against `944bca7fa38cb5e973b04ce7829966cca351202b`.

The previous review's two findings are corrected: evidence output uses fixed `project-1`/`project-2`/`account` filenames with explicit path/symlink checks; abort requires exact recorded staged head for every authority before deleting any ref. Reconstructed deterministic commits and additional negative cases strengthen recovery. The adapter remains disabled by default, fixture-only, and explicitly `ADAPTER_ONLY_NOT_PREPARED` with missing production bindings (no live cutover).

**Blocking new finding — P1:** `findings/symlink-record-path-escape.md`. `stage_commit` checks out a supplied bare Git repository, follows a committed `records` directory symlink on `target.parent.mkdir`/`target.write_text`, and writes an adapter JSON file *outside* the marked disposable fixture root before `git add` rejects traversal beyond the symlink. Independently reproduced on the exact frozen branch using two fresh temporary directories: `write_outside_checkout=True` and `outside_fixture_root=True`. This violates the required no-external-effects boundary even though the overall command fails. Correct the checkout write path before integration.

**Verification:** `python3 -m unittest discover -s tools/Wq10MigrationPreview/tests -v` ran 48 tests, all passed. The new adversarial symlink-in-Git-tree case is not covered by those tests. Existing positive fixtures do not establish that untrusted checked-out record paths stay inside the disposable root.

The verdict concerns this submitted WQ10 fixture adapter only, not production migration authorization.
