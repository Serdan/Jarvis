# Canonical project launch templates

## Contract

The project ledger owns one versioned record at `records/launch-template.json`. The record is independent of the source branch, worktree, agent role, assignment, and provider account. It contains the template, version, operator provenance, and update sequence. Updates use the existing legacy-writer fence, operation UUID idempotency, expected-version compare-and-swap, and immutable Git history. Replaying the same operation UUID and payload returns its original outcome; changed payloads cannot reuse the UUID. No new storage, source migration, background launch or new grant is introduced.

An absent template is version 0, and rendering fails closed. There is no implicit fallback to AGENTS.md, a hardcoded prompt or another project's template. A malformed record fails closed rather than behaving as absent. The first setter must explicitly specify expectedVersion 0; subsequent setters must specify the exact existing version. Only authorized local Operators may save. MCP changes always require fresh human approval at the receiving client even at FullTrust; the permission view opens an exact-body review before approval. Manual grants do not authorize this operation. The authenticated local TUI offers editing directly.

Both literal, case-sensitive tokens `{{agentId}}` and `{{projectName}}` must appear. Only these tokens are substituted; unknown or unmatched double braces fail validation, and individual braces are literal. Substitutions are single-pass and never recursively interpreted. Input must be nonblank, well-formed Unicode, at most 32 KiB UTF-8 and 16 KiB in the ledger serializer's escaped JSON string representation; control characters other than LF, CR and TAB are rejected. Target agent must be registered in the exact project's verified ledger snapshot. Project and agent identities are nonblank, at most 120 UTF-8 bytes, and may not contain control characters. Rendered prompts are bounded by 24,000 UTF-16 code units (the existing launchAgent.js ceiling), 64 KiB UTF-8 and 128 KiB escaped JSON (the ledger-response fences). The exact minimal launch argument envelope must also fit the bridge's 65,536-byte UTF-8 argument budget. The supported minimal encoding is `Client.BrowserArguments.serializeMinimalLaunchPrompt`, which uses compact `System.Text.Json` with `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` for JSON text (not HTML), and serializes only the lowercase `prompt` property. This is an exact serializer, not an estimate of raw prompt size or arbitrary metadata reserve. Rendering rejects prompts that fail any bound. Rendering reports the immutable snapshot commit, template version and blob OID, explicit project ID/name, agent ID and SHA-256 digest of the exact prompt.

## Operator setup and use

After source review and managed integration, an Operator can select an agent row in the desired project's Agents tab of JarvisClient, press **t** to open the project editor, and load the canonical record. For initial setup, paste the approved text below; for replacement, Ctrl+U clears the editor, Enter adds a newline, Backspace deletes the last character, and Up/Down scroll. F6 copies the draft, F2 saves, F5 reloads the canonical state (discarding the draft on successful reload), Esc returns. F2 retries the exact retained operation on an uncertain outcome; editing and reload are locked until resolved. A definite version conflict requires reload; copy the draft first if desired. A changed project binding is never silently followed. In a restart, reconcile uncertain operations with the existing pending-intent tools before issuing any new operation.

Alternatively use the following MCP methods, with the normal authenticated Jarvis connection and selected device. The `agentId` on each tool call is the initiating caller identity; `targetAgentId` is a separate render target.

- `get_launch_template(projectName, agentId)`: inspect record and version/blob; absent means initial version 0.
- `set_launch_template(projectName, agentId, expectedVersion, template, operationId?)`: exact CAS; use a stable operation UUID and approve the exact body at the client. The setter does not launch anything.
- `render_launch_prompt(projectName, agentId, targetAgentId)`: fetch and render on the verified canonical ledger. This is observational and does not activate the target or consume its alerts.

For a manual launch, render for the exact logical agent and project intended for the new conversation. Pass the returned **Prompt** unchanged in the existing `browser_run_in_new_tab` tool's `argsJson` object alongside only permitted launcher metadata (`timeoutMs`, `confirmedSubmittedPrompts`, `selectionOnly`). The operator may construct `JSON.stringify({ prompt: rendered.Prompt })` (adapt property casing to the actual tool response), or use the supported .NET serializer. The renderer guarantees the minimal envelope for that documented .NET encoding, not arbitrary alternative encodings or additional fields. Before invoking, measure the complete serialized argument object with UTF-8 byte counting (e.g. `new TextEncoder().encode(argsJson).byteLength` in JavaScript); it must be at most 65,536 bytes, including escaping and metadata. Remaining budget is 65,536 minus the actual minimal envelope's UTF-8 byte count; all added fields, separators and escaping share that budget. The operator then invokes the ordinary project-owned `browser-scripts/chatgpt/launchAgent.js` on an explicitly registered window and approved ChatGPT URL; retain the existing permitted launcher parameters. The script's source project may differ from the prompt's target project: render against the **target**, not the script's source checkout. Do not synthesize a replacement prompt on the caller or script side. See [ChatGPTAgentLauncher.md](ChatGPTAgentLauncher.md) for the generic browser contract. Record the render snapshot, blob, version and digest alongside the original launch evidence. A later template update cannot rewrite that historical prompt or authorize resending an uncertain launch.

## Approved initial Jarvis configuration

This is reviewed seed content for the explicit authorized version-0 setter, **not** a file read by the runtime and not an automatic migration. No live record is initialized by this source change. Do not overwrite an existing template; inspect the exact version and obtain explicit approval.

```text
Your agent ID is {{agentId}}. Your project is {{projectName}}.

You are an autonomous engineering agent. Your primary output is completed work through tools, not conversational responses.

Call agent_inbox and execute your assignment.

## Execution

Continue working until completion or genuinely blocked. After each tool result, take the next concrete action.

Do not end your turn to report intermediate progress. If one action is blocked, pursue other available avenues of progress.

## Engineering

Prioritize long-term correctness, architecture, maintainability, performance and usability. Do not compromise quality for speed.

Address root causes rather than symptoms. Seek local simplicity and global coherence. Preserve semantics and respect evidence boundaries.

## Coordination

Use canonical Jarvis state for coordination. Use notifications only when exceptional action is required and no structured mechanism exists.

## Lifecycle

When finished, record completion through Jarvis. If genuinely blocked or forced to stop, checkpoint and yield with actionable continuation instructions.

Never voluntarily end your turn while Working. Verify the lifecycle transition before responding.
```

## Native Scheduler integration boundary (default OFF)

The common deterministic renderer is `Client.RoleInstructions.RoleInstructions.renderLaunchPromptAt binding snapshot projectName agentId`; the explicit MCP render calls the same function after fetching. Its pinned snapshot must already have come from the verified ledger path. Tests exercise equivalence between explicit rendering and the shared pinned call, historical replay, and foreign-project rejection. This does **not** demonstrate a native provider launch or native send-path equivalence.

The current source baseline `9f6b9760955f0acb2673e2cab8b8a852ec4dd2ac` does not contain the accepted native reservation/effect source from `d312f1cc83685e781ce69e57ad0d4bf613bfb843`. Canonical tasks `scheduler-native-account-reservation-cas-20261121`, `automation-native-account-project-ingress-terminal-20261123` (Worker 16, explicit ingress source lease and accepted corrections), and `automation-provider-host-trust-contract-20261122` establish the remaining dependency. The native ingress is fixture/default-off and provider-host contract is not an authenticated production issuer. Do not graft a template into the legacy generic one-shot scheduler or manufacture a provider host, launch credential, or effect permission to simulate completion.

After managed integration of the native ingress and authenticated provider host, the owner of the native pre-send path must invoke the shared pinned renderer for the exact verified project snapshot and registered candidate agent, fail closed on missing/invalid template, and bind its returned snapshot/version/blob/digest and exact text to the existing durable original launch intent before any browser effect. Revalidate the original project/role/source/account authority fences. An uncertain operation must retain the original rendered prompt; never re-render it against a newer template and replay. Compose the actual argument JSON from only the exact prompt and permitted non-secret launcher metadata. Before native effect readiness, verify `Client.BrowserArguments.fits` on that complete serialization (not the prompt alone); keep the existing script and response-size fences. The generic bridge remains the final argument-budget gate. Keep all native execution default OFF. Add a focused native ingress/host fixture comparing its exact outgoing prompt and immutable evidence to the explicit render, including missing template, version changes and cross-project mismatch. No launch path is enabled and no duplicate AGENTS.md guidance is removed until separate verification and Operator approval.
