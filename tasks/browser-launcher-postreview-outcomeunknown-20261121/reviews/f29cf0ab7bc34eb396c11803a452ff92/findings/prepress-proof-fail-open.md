# P2 (offline prototype): pre-press identity and geometry gates fail open

## Exact frozen source and scope
- Review `f29cf0ab7bc34eb396c11803a452ff92`, commit `291df40751acb57adc7771cfa6cdc92ad6c3fa87`, parent `b7033b4b7bcdee0936cf7239a625a4ecaccf3257`.
- Files: `browser-extension/tests/fixtures/trusted-send-prototype.cjs`, `browser-extension/tests/trusted-send-prototype.test.cjs`, `docs/analysis/browser-trusted-send-offline-20261121.md`.
- **This is a test-only, default-off design. No production integration or authenticated browser send is asserted.** The defect concerns the prototype's claimed fail-closed demonstration, not a currently deployed exploit.

## Reproducible finding
In the frozen fixture, `makeIntent` stores `registrationGeneration:3`, but `validTab` compares only tab ID, window ID, URL and readiness; it ignores registration generation. A tab object with `registrationGeneration:999` passes. Likewise `validProbe` does not require non-empty `documentToken` or `buttonToken`, so their absence on both probes passes equality in `dispatchOnce`. Non-finite viewport bounds such as `viewportWidth:NaN` also pass the range comparisons.

Independent read-only `node --eval` probe at exact commit (no file edit, credential, browser or Send):
- `staleRegistrationAccepted: true`
- `absentIdentityTokensAccepted: true`
- `nanViewportAccepted: true`
- With all three conditions, `dispatchOnce` returns `inputDispatched` and calls `mouseMoved`, `mousePressed`, `mouseReleased` in the mock transport.

This contradicts the proposed protocol's pin to exact registration generation, document identity/nonce and valid hit-test coordinates, and can make offline tests green without actually exercising those gates. The code correctly returns `providerSubmitted:false`, and nothing here establishes an actual provider-side Send.

## Bounded candidate correction
Within the existing offline-only prototype, require an authoritative injected registration generation to equal `intent.registrationGeneration`; require non-empty renderer document/button tokens and finite positive viewport bounds; add negative fixtures where each missing/mismatched value blocks before `mousePressed`. Distinguish test-only modeling from durable CAS, permission authority and real-site acceptance, which the document correctly identifies as **not implemented**. Do not activate CDP transport, add broad debugger privileges, perform live canaries, change the launcher or require production implementation under this review.

## Test evidence and disposition
Independent tests on clean registered worktree at `291df407`: prototype 17/17, bridge 83/83, popup 6/6; `git diff --check b7033b4b 291df407` passed. The negative probes above expose coverage gaps despite those passing suites. Recommend **ChangesRequested** limited to these offline fail-closed proof defects. No claim that the unimplemented production design must be built for this review.