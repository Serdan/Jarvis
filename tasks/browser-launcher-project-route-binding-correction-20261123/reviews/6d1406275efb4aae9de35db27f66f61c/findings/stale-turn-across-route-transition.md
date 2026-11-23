# A prior turn can still bind an unrelated route before its DOM hydrates

**Severity:** blocking source-correctness defect against the existing causal-route requirement (not a provider-observed incident). **Frozen source:** 90acadead16c8e0708ad4da323bccca538e2c907; task spec f05690d43b43cc775ce9c778447d4cdb5364b111.

## Reproduction

On independent clean exact checkout `reviewer4-route-90acadead`, run the existing synthetic fixture harness and `runProjectRoute` from `browser-extension/tests/chatgpt-launcher.test.cjs` with this additional event sequence, using the same virtual clock and no-legacy-marker configuration as the author's fixture:

```js
const {h, result} = await runProjectRoute([
  { afterMs: 75, bubble: projectPrompt },
  { afterMs: 300, href: "https://chatgpt.com" + projectRouteB },
  { afterMs: 375, bubble: "Different prompt" }
]);
assert.equal(result.status, "outcomeUnknown");
```

Independent execution: extracted the exact existing harness and helper into a transient Node module in memory (no source edits); assertion **failed**, exit 1. Actual result: `status:"submitted"`, `conversationUrl:"https://chatgpt.com/g/g-p-abc123/c/00000000-0000-0000-0000-000000000002"`, evidence `["newUserTurn","matchingUserBubble","conversationRoute"]`, one send, zero launcher navigations. At 300 ms, the visible route has changed to unrelated B, but the previous matching bubble persists until B's DOM hydration at 375 ms. The function returns before the next event. This is only a synthetic possible SPA ordering; no claim that live ChatGPT exhibited it.

## Original obligation and impact

Stage B requires a genuine same-tab exact scoped durable route plus a matching new turn or evidenced continuity, and an unknown null URL if unbound. The new documentation explicitly requires a matching turn **on that route** and says an earlier matching turn cannot bind a later route showing a different turn. `launchAgent.js:727–745` samples location and DOM in one synchronous poll, but neither associates the previously observed DOM with that route. The existing negative test changes URL and bubble in the same virtual event, so it misses the interval of non-atomic SPA route/DOM replacement. The correction removes sidebar false positives but still returns authoritative submitted URL B and the bridge may close the tab before the intended route is established.

## Candidate remedy (subject to independent bounded adjudication)

Consider a route-scoped fresh turn identity/DOM readiness or evidenced continuity that is demonstrably associated with the durable route; otherwise return the approved acknowledged-project-route-unknown result with null URL and retain the tab. A fixed grace interval alone does not prove association; do not invent provider guarantees or demand a new provider integration as an implicit requirement. Add the staggered route/DOM mismatch fixture and preserve genuine positive evidence, deadline, one Send, and no guessed navigation. Lead/independent adjudicator should decide the proportional correction and any needed additional provider evidence; this finding does not create new corrective Work or prescribe a mandatory implementation.