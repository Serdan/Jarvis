# Nonblocking bounded-coverage limits

**Severity:** Information / independent review boundary, no requested source edits.

The standalone F# executable calls production `WorkQueueSchedulerHostLoop.Host` with real locks, cancellation, step state and TCS joins. It does not copy a Host mock. Its `seed` only rotates five predefined scenario classes; process scheduling is not deterministically controlled or exhaustively enumerated, and early external-wrapper observations are intentionally schedule-dependent. The minified failing trace prints seed, iteration, scenario, reason; it is sufficient to localize a failed scenario but not to replay exact thread interleavings. No claim of exhaustive property/state-space proof is warranted.

The negative injection intentionally appends a second callback accounting item at the inert `Wake` port and exits 1 under the exact-once oracle. This validates one oracle but does not mutate actual Host production code or prove every possible duplicate-side-effect failure would be caught.

The tests use safe in-process fake ports and Host disposal as shutdown exercise; they do not execute full client application shutdown, real account/project CAS, browser operations, or third-party sends. No new JS/Node files or scripts are in this frozen delta. Treat passing native Host cases as a **bounded test-only regression/diagnostic gate**, not authorization for production scheduler activation or proof of all failure modes. The report truthfully states those boundaries, so this is nonblocking for the original narrow assignment.