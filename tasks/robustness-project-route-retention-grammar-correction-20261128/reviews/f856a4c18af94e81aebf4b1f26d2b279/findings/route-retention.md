# Independent source review

## Scope and provenance

Reviewed the exact frozen source `678657eda146512ba139df69c39c20c27be6685e` against target base `89d9e38531869ca6f4fa4e514f9d2f4ed13e51a1` (`refs/heads/main`). The claimed review metadata and clean source worktree agree on that head. The composed diff changes only `Client/Modules/BrowserBridge.fs` and `Client.Tests/BrowserBridgeTests.fs`; no author source was edited, no branch was pushed, and no deployment or live send was performed.

## Findings

No actionable defects found in the reviewed scope.

The bridge's `projectEntryPath` accepts only absolute HTTPS `chatgpt.com` URLs on the default port, without user information, query, or fragment. Its path grammar now matches the existing launcher producer: `/g/g-p-` followed by one or more nonempty lowercase alphanumeric segments separated by single hyphens, then `/project`, with one optional trailing slash. Empty segments remain rejected. The parsed entry path is normalized by removing the optional trailing slash, and retention requires the normalized path from the bridge URL and `args.startUrl` to match exactly. This accepts the producer's `abc123-jarvis-agents` form without weakening origin, scheme, port, credential, query, fragment, or project-binding checks.

Retention remains narrowly gated inside the `outcomeUnknown` branch: exact `conversationRoute` / `ConversationUrlUnavailable` classification; a string marker equal to `matchingUserBubble` or `matchingLegacyUserTurn`; an explicitly present JSON-null `conversationUrl`; and a bounded evidence array containing both `newUserTurn` and that same marker. Only `closeAfterExecution=true` changes cleanup to `retained`. Generic, malformed, mismatched, wrong-stage, wrong-reason, unbound, and successful outcomes preserve their prior cleanup behavior. The bridge does not turn the unknown outcome into submission success or replay the send.

The deterministic matrix covers both marker types, single- and multi-segment hyphenated slugs, trailing-slash normalization on each bound URL, empty first/middle/last segments, a different but syntactically valid project slug, mismatched marker/evidence, malformed or oversized evidence, missing/non-null URL, generic unknowns, incorrect stage/reason, wrong origin, missing/mismatched `startUrl`, and successful submissions. Negative cases assert cleanup; qualifying unknowns assert the created tab remains open and no close frame is sent.

## Verification and limits

- Independent filtered Release run on this exact worktree: `dotnet test Client.Tests/Client.Tests.fsproj --no-restore --configuration Release --filter FullyQualifiedName~BrowserBridgeTests.projectRouteAcknowledgementRetainsOnlyBoundUnknown`; terminal result exit code 0, 32 passed, 0 failed, 0 skipped.
- Existing full `BrowserBridgeTests` Release TRX in the same frozen worktree: 84 executed, 84 passed, 0 failed; all 32 route-retention cases passed.
- The full Client test suite was not rerun on this source. The change is confined to the bridge branch and its focused tests; this is recorded as a verification limit, not a blocking defect under the task's focused-test acceptance scope.
- The independent filtered build emitted one existing `FS0760` recommendation in `Client/Program.fs:834`; no build or test failure resulted.

The original review `78a6d472716d4c88b9306bea477c7c34` remains `ChangesRequested`; this decision concerns the separately claimed corrected composed source and does not rewrite that historical verdict.