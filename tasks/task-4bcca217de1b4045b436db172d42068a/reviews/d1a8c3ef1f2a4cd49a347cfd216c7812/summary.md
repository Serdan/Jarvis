# Reviewer 3 — ChangesRequested (independent frozen review)

**Review:** `d1a8c3ef1f2a4cd49a347cfd216c7812` / task `task-4bcca217de1b4045b436db172d42068a`.
**Exact frozen commit:** `49e3c6cd9bda7094db6fb03b0ba045a7a0095ed2` (parent/base `aa2e46ff2927b8e9fd0af25204377c299f2ec576`).

Verified two-file-only QA addition (Markdown native Windows smoke report plus `native-windows-bounded-process-smoke.fsx`), with no production modifications. Inspected exact script, report, baseline `BoundedProcess.fs` Windows helper/drain path and `HermeticHarnessTests.fs:85-90` native NUnit guard. The report correctly retains Windows HOLD for a normal-parent-exit timeout and blocked native NUnit, and macOS HOLD with no native runner. The reporter's Windows test executions were not independently reproduced in this turn; local read-only source verification is **not** native runtime acceptance.

**Required QA corrections**, detailed in `findings/native-smoke-assertion-gates.md`:

1. **P2:** `descendant_held_both_pipes_then_timeout` can PASS after clean exit without asserting actual timeout or that both inherited pipes were present. Require both stream markers and the deadline/exit/reaping contract; separate a no-hold successful path into its own case.
2. **P3:** The hardcoded `W23_SMOKE_SUITE_PASS checks=12` is wrong for 13 defined cases and false for filtered single-case runs. Report the actual executed count.

**Verdict:** ChangesRequested on QA evidence rigor/precision, **not** a rejection of the useful release-blocker observation. Do not weaken the Windows profile-safety guard or claim platform GO; no implementation, integration, release, or cutover authorized.