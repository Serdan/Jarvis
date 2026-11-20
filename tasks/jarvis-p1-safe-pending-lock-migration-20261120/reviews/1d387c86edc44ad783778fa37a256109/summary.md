# Independent review — ChangesRequested

Reviewed frozen head `92c0b1b4be19b2e177594dc2a3985f4854d69116` against base `95a471fc565b57fe309c65fc85f371693dbe9067`.

The shared-stripe/exact-file protocol is sound for cooperating legacy exclusive-stripe and current per-UUID-file clients. Linux .NET 11 Release regression subset passed: **8/8** (`FullyQualifiedName~PendingIntent`). The task spec records a successful native Windows standalone smoke, but I did not independently reproduce it; native NUnit setup is Linux-only.

Changes requested for five gaps:
1. The exact-file exclusivity probe closes before unlink, leaving a TOCTOU race for non-cooperating openers on Unix.
2. One non-blocking pass processes at most 32 files; concurrent same-prefix bursts can leave an unbounded backlog with no scheduled drain.
3. The candidate is not interoperable with the named-mutex client generation; both can hold the shared stripe while using unrelated per-UUID locks.
4. The wildcard directory scan can scale with the whole `.locks` directory while holding the exclusive stripe; large-directory latency is unmeasured.
5. Unexpected cleanup exceptions can escape into the outer handler and replace a completed operation result.

Detailed evidence, scenarios, and requested corrections are in `findings/exact-lock-reclamation.md`.

**Do not treat this commit as satisfying the full mixed-client/bounded-growth acceptance until F1-F3 are resolved or the threat model and rollout scope are explicitly narrowed and enforced.**