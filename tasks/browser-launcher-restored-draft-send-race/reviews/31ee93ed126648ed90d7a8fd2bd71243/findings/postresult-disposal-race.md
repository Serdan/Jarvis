# F01 — P1: A stale page-level disposal proof can close a newly active conversation

**Review:** `31ee93ed126648ed90d7a8fd2bd71243`  
**Frozen SHA:** `7571988db320803adc73445a5c691a901e980b8e`  
**Disposition:** Blocking — changes requested.

## Exact source / violated invariant
- `browser-scripts/chatgpt/launchAgent.js:10-20,58-63` computes `safeToDisposeCreatedTab` only when composing the page result. It observes home URL, zero user-turn selectors, and no send attempted *at that moment*.
- `Client/Modules/BrowserBridge.fs:833-850,895-904` trusts that historical Boolean and calls `closeDefinitive()` asynchronously; the contradictory-reason guard covers only the status/reason returned earlier.
- `browser-extension/background.js:130-182,767-810` pins the agent-created registration, owner, generation, window, and allowed **origin**, but does not verify that the tab is *still* at the fresh home route or contains zero user turns when `chrome.tabs.remove` is issued. Same-origin `/c/<conversation>` navigation remains authorized by `assess()`.

## Concrete interleaving
1. The page returns `{status:"notSubmitted",reason:"UnexpectedSendState",safeToDisposeCreatedTab:true}` on an empty ChatGPT home tab.
2. Before the bridge's separate `closeTab` command completes, the tab develops a user turn or navigates to a new same-origin `/c/<uuid>` conversation (e.g. another actor uses the visible created tab, or late navigation completes).
3. Registration ownership/generation and origin are unchanged. The extension's final checks pass and `chrome.tabs.remove` closes that now-conversational tab.

This is a narrower but real remainder of the prior P1: `ExistingConversation`/`UnexpectedStartUrl` **reported by the page script** are now retained correctly, but the same state **appearing after the result and before cleanup** is not. The safety promise must hold at disposal, not only at the earlier observation.

## Required correction
Keep the successful new result/reason guards. Add an explicit cleanup-time fresh-home/no-user-turn guard tied to the exact pinned registration; refuse removal and surface `cleanup=retained` (or guarded cleanup failure) when the route changed, a turn exists, the inspection fails, or ownership changed. Do not retry a possible send. Add a deterministic composed bridge/extension test where a true proof is followed by same-origin conversation navigation **before close**, and assert that no `tabs.remove` occurs; also cover a user turn while the route remains home. Preserve definitive empty-tab cleanup and the default-false pre-injection behavior where genuinely safe.

## Coverage boundary
The new Node tests cover route/turn changes *during* the launcher's stabilization. The bridge's `composedLauncherDisposalHonorsFreshHomeProof` fixtures inject static script results but no subsequent route/turn mutation. The extension explicitly allows same-origin navigation. Thus passing tests do not exercise this interleaving.
