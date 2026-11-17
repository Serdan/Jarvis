# P1 — Ambiguous legacy-machine selection can silently target the wrong concurrent instance

**Frozen commit:** `6d1f003aec6f097dae2d35b347f51263a46dd9db`.

**Source:** `Server/Services/UserService.fs:260-286` `migrateDisconnectedLegacySelections` queries *all* active `jarvis2:` routes matching a disconnected legacy machine UUID, sorts by `(RegisteredAt, DeviceId)`, takes `Seq.tryHead`, and writes the chosen instance into both user default and all conversation selections previously bound to the legacy ID. It runs during v2 registration (`:410-412`) and legacy transport disconnect (`:444-453`). `Server.Tests/DeviceRouteIdentityTests.fs` tests only one matching v2 instance; no ambiguous case.

**Impact:** Concurrent A and B on the same physical machine are now intentional. With legacy client L selected for a conversation, register A and B, then disconnect L: the old implicit conversation affinity silently moves to whichever v2 process registered first (and can vary across server restarts). There is no provenance establishing that A rather than B owns the legacy command stream/workspace or has the intended local process resources. This violates exact routing intent; a state-mutating command can execute in an unintended live client.

**Required correction:** Treat a migration with two or more eligible live instances as ambiguous, *not* an implicit tie-break. Fail closed or require an explicit user selection; when exactly one genuinely compatible instance exists migration can be allowed under a documented policy. Include positive single-candidate and negative multiple-candidate tests; ensure explicit `deviceId` overrides remain exact and never migrate, and that no in-flight job handle is reassigned to a different process.

**Evidence limit:** Deterministic source-level inference. The full two-instance legacy cutover is not exercised by the existing tests or by this review's bounded test runs.
