# Independent review — Request changes

**Frozen source:** `ef1756a99c12392febacc6810fce2dc1b030343d` against `6ab793928fa7f998b9d5796061cf66d3c0b02e40`. **Task:** browser-agent-launcher-reliability, operator-approved task v4/spec v3.

## Verified

- Inspected `Client/Modules/BrowserBridge.fs`, `browser-scripts/chatgpt/launchAgent.js`, `browser-extension/background.js`, changed NUnit/Node test fixtures, and bridge/launcher documentation.
- Ran `node --test browser-extension/tests/chatgpt-launcher.test.cjs` against the frozen worktree: **29/29 passed** (exit 0). Includes arbitrary restored drafts, exact insertion, disabled controls, existing conversations, uncertain Send, and no double-click.
- Initiated independent `dotnet test Client.Tests/Client.Tests.fsproj --configuration Release --no-restore` against the same frozen worktree (Jarvis job `d2e72842b3ca495787c7934650b46a58`); **result still pending at review write**. No pass claimed.
- Reviewed simulated two-connection socket tests covering union listings, target owner dispatch, revoked registrations, colliding aliases, in-flight connect and pinned connection during composite launch. They do not exercise tab-generation turnover or a window-alias reassignment race.

## Blocking findings

1. **P1: Created-tab identity/generation not pinned through launch** — `findings/created-tab-generation-not-pinned.md`. A manually re-registered agent-created tab retains its ID but changes generation and `agentCreated=false`; the readiness poll accepts it and Execute binds its new generation, allowing bootstrap injection after ownership changes. Need original-ID/generation/created-state assertions and regression.
2. **P1: Window alias is resolved twice without generation pin** — `findings/open-tab-window-identity-not-pinned.md`. Bridge selects a ready window but sends the caller's potentially mutable alias; extension resolves it again immediately before creating the tab, so a reassignment can silently select a different window. Send immutable selected window ID + expected generation and test fail-closed reassignment.

## Remaining acceptance gate

The docs expressly say the three-distinct-bootstrap live Edge sequence and adverse real connection flips are not yet verified because using the modified client requires authorized installation/restart. This remains unproven; **do not deploy or restart without operator authorization**. Code approval, integration, and deployment are distinct.

**Verdict:** RequestChanges. Preserve the frozen review boundary; no code changed or merged by reviewer.