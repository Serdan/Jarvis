# P2 — Shared activity JSONL writers have no inter-process append/rotation coordination

**Frozen commit:** `6d1f003aec6f097dae2d35b347f51263a46dd9db`.

**Source:** `Client/Modules/ActivityLog.fs:31-35` configures one machine-shared `LocalApplicationData/Jarvis/activity` directory; `:141-149` creates only a *process-local* `syncRoot`. `:185-233` prunes and selects/rotates the shared `activity-YYYY-MM-DD[-NNN].jsonl` segments without an inter-process lease; `:83-97` opens `FileMode.Append` with `FileShare.ReadWrite`, and `:293-305` writes through each process's independent `StreamWriter`. `Client.Tests/ActivityLogTests.fs:495-515` now covers the separately locked `agent-seen.json` merge, **not** concurrent JSONL writers.

**Risk:** With two live clients, both can select the same segment based on stale size, write records independently, and rotate/prune without knowing about the other's open writer. Neither the segment-size bound nor whole JSONL records are protected as one inter-process operation. On some environments multiple physical writes for a large record may interleave, and other platforms' append/prune semantics can lose a record or segment. The reverse replay path silently tolerates malformed JSONL; an activity history can therefore be incomplete without reliable audit visibility. This remains an unproven runtime race, not a witnessed corruption.

**Required correction:** Designate either one inter-process owner/append+rotation lock or independent instance-specific append-only segments with deterministic replay and bounded retention. Add a genuine two-process test with many near-limit activity entries plus overlapping rotation, ensuring no record loss/corrupt JSONL and safe pruning while another process keeps a writer open. Preserve current privacy, size and replay bounds.

**Evidence limit:** Source inspection; no stress test or multi-process activity run was executed in this review.
