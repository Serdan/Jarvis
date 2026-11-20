# Independent review — exact-UUID lock reclamation

**Verdict: ChangesRequested**

Reviewed frozen head: `92c0b1b4be19b2e177594dc2a3985f4854d69116`  
Declared base: `95a471fc565b57fe309c65fc85f371693dbe9067`  
Task: `jarvis-p1-safe-pending-lock-migration-20261120`  
Review request: `1d387c86edc44ad783778fa37a256109`

## What is sound

The shared-stripe/exact-file protocol is coherent for the two file-based client generations:

- A current client holds a shared stripe handle and an exclusive exact-UUID handle for the entire operation.
- A legacy client holds the stripe exclusively, so it excludes current shared readers.
- Cleanup requests the stripe exclusively only after the operation's stripe and exact handles have been disposed. That excludes cooperating current holders and waiters, as well as legacy exclusive-stripe clients.
- Same-UUID current clients still serialize on the exact file; different UUIDs sharing a prefix can execute concurrently.

The Linux tests I ran on the frozen worktree passed: `dotnet test Client.Tests/Client.Tests.fsproj --no-restore --configuration Release --filter FullyQualifiedName~PendingIntent` — **8 passed, 0 failed, 0 skipped** (.NET 11, Release). This includes the three exact-lock/reclamation tests.

The task spec also records a native Windows standalone smoke on this exact commit: quiescent reclamation; same-UUID serialization (3,624 ms); unrelated UUID progress (4 ms); active shared-gate deferral (2 ms); later cleanup; and legacy exclusive-stripe waiting (2,366 ms). I did not independently reproduce that Windows run. Native NUnit execution is unavailable from this Linux host because the existing `HermeticClientTestEnvironment.BeforeAll` setup is Linux-only.

## Findings

### F1 — High: exact-file probe and unlink have a TOCTOU race on Unix

**Location:** `Client/Modules/OrchestrationApi.fs:682-687`

`probe()` opens the candidate exact file with `FileShare.None`, but its `use` scope closes the handle before `File.Delete(path)`. A process that does not participate in the stripe protocol can open the path in that interval. On Linux, unlinking the pathname succeeds even while that process retains the old inode; a later opener can create the same pathname and acquire a lock on a new inode. Two processes can then believe they own the same UUID lock.

The exclusive stripe proves quiescence only for clients that take that stripe. The probe does not close the race for non-cooperating openers, despite the adjacent comment claiming it conservatively protects against their handles. The existing tests cover a holder already present when the probe runs and a cooperating shared-stripe holder; they do not cover an opener arriving between probe close and unlink.

Either make the safety contract explicitly cooperative-client-only (and remove the non-cooperating-handle guarantee), or provide a protocol that all possible openers must participate in. Do not claim arbitrary open-handle safety from this check-then-delete sequence.

### F2 — High: the 32-file cap does not provide bounded growth or guaranteed eventual cleanup

**Location:** `Client/Modules/OrchestrationApi.fs:658-675, 744-746`

There is one non-blocking cleanup attempt after each operation, and each successful pass processes at most 32 paths. Consider 100 distinct UUIDs with the same three-character prefix, all holding the shared stripe concurrently. The first 99 cleanup attempts fail while another shared holder remains. The final operation can acquire the exclusive gate, but its single pass deletes at most 32 files, leaving at least 68 exact-lock files. No continuation or background sweep is scheduled. If no later operation uses that stripe, those files remain indefinitely; repeated bursts can grow the backlog by roughly the burst size minus 32.

The active-gate test demonstrates deferral followed by cleanup on a later operation, but only with three files. It does not establish a hard storage bound or eventual reclamation under sustained contention. Add a deterministic test with more than 32 same-prefix concurrent operations and define a guaranteed drain mechanism or explicitly weaken the bounded-growth acceptance criterion.

### F3 — High: this candidate is not interoperable with the named-mutex client generation

**Location:** candidate `Client/Modules/OrchestrationApi.fs:651-746`; named-mutex experiment `1be1c1cdd`, `Client/Modules/OrchestrationApi.fs:697-716`

This candidate deliberately retains the per-UUID file lock. The named-mutex experiment takes the same shared stripe but serializes a UUID using a named OS mutex instead of the exact file. A file-based client and a mutex-based client can therefore both hold the shared stripe and execute the same UUID concurrently: neither primitive excludes the other.

The candidate is compatible with legacy exclusive-stripe and deployed per-UUID-file clients, but it does not satisfy the task's stated mixed-client outcome covering named-mutex clients. If the named-mutex branch is to remain permanently non-deployable, that must be an explicit, enforced rollout/rollback gate and the acceptance scope must say so. Otherwise, the generations need a common exclusion protocol or a verified retirement/cutover sequence. This commit adds neither a gate nor rollout/rollback documentation.

### F4 — Medium: the bounded item count does not bound scan time or stripe hold time

**Location:** `Client/Modules/OrchestrationApi.fs:660-675`

`Seq.truncate 32` limits the number of candidates processed, not necessarily the number of directory entries examined. On Linux, wildcard enumeration still walks the directory and applies the pattern while enumerating. If the directory is large and a given prefix has fewer than 32 matches, the scan can visit the entire `.locks` directory. The exclusive stripe remains held for the full enumeration and deletion pass, blocking all same-prefix operations during that work.

No large-directory or sustained-load latency evidence is included. Measure at realistic and adversarial directory sizes, and avoid a full shared-directory scan under the exclusive gate if latency grows with unrelated UUIDs.

### F5 — Medium: an unexpected cleanup exception can replace the completed operation result

**Location:** `Client/Modules/OrchestrationApi.fs:688-693, 746-749`

The cleanup helper swallows `IOException` and `UnauthorizedAccessException`, but other exceptions escape into `withIntentLock`'s outer catch, which returns `ValidationError`. Since cleanup runs after `operation()` has returned, that can turn an already-applied remote write into an apparent failed call. This contradicts the helper's stated invariant that cleanup must not change the write result. Isolate all non-fatal cleanup failures inside the best-effort helper and always return the captured `outcome`.

## Decision boundary

The Linux Release regression evidence is positive for the exercised cooperative file-lock cases. It does not prove the unlink race safe, establish bounded growth under high concurrency, cover named-mutex coexistence, or establish large-directory latency. Windows smoke evidence is reported in the task spec but was not independently reproduced here.

Do not merge or deploy this candidate as satisfying the full task acceptance until F1-F3 are resolved or the relevant compatibility/threat-model scope is explicitly narrowed and enforced.