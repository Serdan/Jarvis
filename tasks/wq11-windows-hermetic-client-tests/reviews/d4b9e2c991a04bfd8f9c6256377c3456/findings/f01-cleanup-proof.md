# F01 — P1: Cleanup reports success when the scratch root is inaccessible

**Frozen source:** `25a452e0ada1b99e286d40051d8438e905df4abd`  
**Location:** `scripts/test-client-hermetic.fsx:197-217, 430-438`

`cleanDisposableTree` gates both cleanup and its final proof on `Directory.Exists(path)`. On Windows/.NET that API returns `false` for access-denied paths, not only missing paths. If the testhost or an adversarial fixture changes the root ACL so that the parent loses access, `remove()` skips the root identity check and traversal, the final check passes, and the outer `finally` prints `scratch cleanup verified = true` while private scratch may still exist. This violates the required fail-closed cleanup and evidence boundary, especially after test failure/timeout.

The newly introduced recovery script already recognizes this exact class of error and verifies absence with `File.GetAttributes` (`scripts/recover-hermetic-client-scratch.fsx:68-75`).

**Required correction:** Use a missing-path proof that distinguishes `FileNotFoundException`/`DirectoryNotFoundException` from `UnauthorizedAccessException` and other failures, both before attempting removal and for its final postcondition. Never print cleanup success after unverified absence. Add a Windows disposable-root negative test with access denied/ACL tampering that verifies nonzero failure, no false success, and safe recovery behavior.