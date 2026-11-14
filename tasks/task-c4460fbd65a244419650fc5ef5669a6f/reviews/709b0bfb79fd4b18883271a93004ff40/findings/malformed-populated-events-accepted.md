# [P2] Malformed extra populated records are silently ignored

**Frozen head:** `44c854f45cf1e43d2baeac401b97835a08c49bda`

**Location:** `Client/Modules/LinuxJobScope.fs:121-136`.

`inspectEvents` uses `Array.choose` and only retains lines with *exactly two* tokens whose key is `populated`. An additional malformed `populated` record is discarded, so the function reports `Empty` for `"populated 0\npopulated 1 extra\n"` and `"populated 0\npopulated\n"` rather than `Unverified`. This is a pure static consequence of `parts.Length = 2` inside the `Array.choose`: the valid `0` survives, the malformed second line is ignored, and `parsed.Length = 1`. The submitter's new tests check duplicate *well-formed* lines and malformed *single* lines, but not valid plus malformed duplicates.

**Impact:** The parser claims definite kernel occupancy for malformed/ambiguous `cgroup.events` content, contradicting the stated fail-closed evidence contract. It currently does not directly authorize administrative correction or server terminal liveness, so this is lower severity than the cleanup failure.

**Required:** Reject *any* malformed `populated` line and any duplicate key, without ignoring malformed entries while scanning. Preserve tolerance for legitimate unrelated kernel keys such as `frozen`. Add the two mixed valid/malformed cases above to `linuxNativeJobScopeParsesOnlyExplicitKernelPopulation`. No worktree edits were made.
