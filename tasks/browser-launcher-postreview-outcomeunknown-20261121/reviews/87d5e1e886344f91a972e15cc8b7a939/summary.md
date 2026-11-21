# Independent review — explicit launcher fallback order

**Proposed verdict: Accepted (scope-limited).** Review `87d5e1e886344f91a972e15cc8b7a939` on frozen commit `a9e758fa1274b6e036708beb6da055721644fdab`, against base `d702352ef705f9991ac1cb14dc15a13cce78aa44`. Detached review worktree HEAD independently matched the exact reviewed SHA. Parent `1fabb066c` already provided the ordered selector; the frozen child adds a missing negative-space fixture and corrects stale Astra commentary.

## Source verification
- `browser-scripts/chatgpt/launchAgent.js` explicitly orders preferences **Chat GPT-6 High → Work GPT-6.2 Sol Medium → Chat GPT-6 Instant**, re-evaluated per fresh launch; the selection loop advances only after a failed confirmation.
- The Work candidate is the `GPT-6.2 Sol` model, not Astra. The code requires the Work Sol UI label before accepting that selection. The first candidate retains priority whenever Chat High can be confirmed.
- `selectionOnly` returns after configuration selection, before any composer or Send action.
- Frozen child adds a test asserting that when High and Work Medium are unselectable, the two failed attempts appear in the right order, Chat Instant is selected, and `h.clicks === 0`. Existing targeted fixture covers High unavailable → Work Sol Medium.

## Executed evidence on exact detached SHA
- `git rev-parse HEAD`: `a9e758fa1274b6e036708beb6da055721644fdab`.
- `git diff --check d702352e..a9e758fa`: exit 0, no whitespace defects.
- `node --test --test-name-pattern='fall back|falls back' browser-extension/tests/chatgpt-launcher.test.cjs`: **2/2 passed**.
- `node --test --test-name-pattern='new Chat tab selects preferred model' browser-extension/tests/chatgpt-launcher.test.cjs`: **4/4 passed**.
- `node --test browser-extension/tests/chatgpt-launcher.test.cjs`: **77/77 passed, 0 failed**, exit 0, ~58.3 seconds.

## Evidence boundary / findings
No in-scope source or deterministic fixture defect found. These are local simulated-DOM tests, **not** proof of live provider quota detection, remote acceptance, browser deployment, or resolution of the separate post-Send OutcomeUnknown issue in the parent task. The cumulative branch also contains earlier background-tab/bridge changes; this verdict covers the **requested exact fallback-order correction**, not a new independent approval of every preceding feature. No live prompts, source edits, main integration, deployment, or schedules were performed.
