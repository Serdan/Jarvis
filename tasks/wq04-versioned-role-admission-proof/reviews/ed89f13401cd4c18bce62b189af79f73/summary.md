# Reviewer 2 — ChangesRequested (independent frozen WQ04 role authority)

Task `wq04-versioned-role-admission-proof`; review `ed89f13401cd4c18bce62b189af79f73`; exact `1cd0b88db631dc4258601e5e531f95c02a34ebd4` from current-main base `e837d2d67277af8eb4b634ff58d5ba9e1b59d19e`.

**ChangesRequested, inert role-adapter scope only.** See `findings/reviewer2-role-historical-provenance-audit.md` for source-line evidence and fixes.

1. **Retroactive role authority:** `initializeCatalog` can be run after legacy launches exist, then `configure` can issue a matching v1 after their admission. `loadAt`/`FirstConnect`/`ClaimWork` verify the numeric `RoleVersion` against the *current reconstructed catalog*, not original admission-time issuance. Require an empty-project/proven-migration fence and a durable pinned admission receipt.
2. **History is not truly immutable/proven across Git heads:** `decode` validates the newest JSON blob's shape and canonical bytes, but not that prior version bytes/receipts were unchanged from their issuance. A shape-valid rewrite of an old version plus receipt is accepted at a fresh head. Anchor immutable per-version OIDs or verify append-only lineage; add adversarial two-clone substitution negative.
3. **Claim/admission two-clone proof is incomplete:** `ClaimWork` preflights any eligible supplied work ID rather than proving the exact `Contracts.claimNext` result and credit under the same planner. Tests show stale catalogue and a stale marker-transaction admission, not a simultaneous actual launch/claim outcome. Add fail-closed two-client races and exact chosen-work binding before combining.

**Positive checks:** isolated four-file change, direct-parent ancestry, clean worktree, `git diff --check` clean; fresh-head project/catalog Git CAS preflight and semantic UUID replay; no Scheduler, Manager, account, source, or host effects activated. `.NET 11.0.100` Release focused role tests **6/6**, Server suite **233/233** after actual restore/test. Full Client run was still in progress at this writing and is not represented as passed.

No reduction of WQ04/WQ05/Worker10 source high-water/account gates, no live activation. Correct in a new isolated commit, submit a new task-bound frozen independent review.