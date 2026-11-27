# Context measurement and role validation

Source baseline: fresh remote main readback 291b5499051fbddfff1c76308e1a0202a5d1862f. Pinned exact ledger blobs and launch SHA in semantic-coverage.md. This measures decoded delivered instruction bodies, not estimates of tokens, complete live inbox response size or actual provider cache hits.

## Exact body measurements

Executed from isolated source checkout:
`dotnet fsi --exec docs/reviews/lead-role-consolidation-20261127/measure.fsx` (exit 0).

| Body | UTF-16 code units | UTF-8 bytes | default System.Text.Json serialized string bytes |
|---|---:|---:|---:|
| Lead v1 | 1,218 | 1,218 | 1,226 |
| Lead v17 | 15,676 | 15,684 | 15,807 |
| Lead agent v4 | 2,289 | 2,291 | 2,306 |
| Candidate body | 7,873 | 7,873 | 7,907 |
| Lead plan v354 HTML | 21,604 | 21,612 | n/a |

The proposed role saves 7,803 decoded UTF-16 units (49.8% of v17), retaining the agent v4 override unchanged. All four measured instruction bodies pass the mirrored validBody predicate. Purpose remains unchanged (196 chars); candidate is the complete body as stored in candidate-role.md, not the manifest.

## Representative bootstrap + two instruction reads

Actual selected-checkout get_agent_instructions response returned workspace AGENTS.md 1,871 units and project AGENTS.md 5,428 (no additional directory AGENTS in the leased path). Actual resolve_agent_instructions for target Lead returned role v17 15,676 and agent v4 2,289 at the expected exact blob OIDs; actual render_launch_prompt returned 604 units for Lead, template v8, SHA-256 2a1e2a684ead19ed8c79929e158c6f9c24887521dd167a3c12f6daddcf09c1f9. The source template and selected-checkout project AGENTS state that inbox and explicit instruction reads each deliver the resolved layers. Do not call another agent's inbox: representative reconstruction uses exact resolved Lead content and exact AGENTS bodies, not a fabricated live inbox or a claim to have activated Lead. Lead plan counted once as a separate task read, not claimed to be embedded in inbox.

| Decoded body component | Current | Candidate-only adoption |
|---|---:|---:|
| Launch prompt | 604 | 604 |
| Workspace + project AGENTS | 7,299 | 7,299 |
| Role + agent in inbox | 17,965 | 10,162 |
| Same role + agent in explicit read | 17,965 | 10,162 |
| Representative bootstrap subtotal | 43,833 | 28,227 |
| Lead plan HTML read once | 21,604 | 21,604 |
| Representative total | 65,437 | 49,831 |

Reduction: 15,606 decoded units, 35.6% of representative bootstrap or 23.8% including current plan read; repeated role/agent portion shrinks 43.4%. This excludes envelopes, metadata, history, review/queue/task and job data, provider context wrappers, assignment/spec reads other than Lead plan, and tokenization. A later one-copy delivery design would avoid one duplicate role/agent pair (10,162 units at candidate size), but that is not part of this proposal. Nor is removing agent v4 or shortening the plan. Full task_overview exceeded safe output in this session (940,986 bytes vs 921,600); do not infer whole-inbox size from that unrelated operation.

## Cache-prefix consequences

The v8 rendered launch prompt contains stable executor prose followed by project and agent identity; it does not embed role/config, live queue, plan or checkpoint. Candidate-only role adoption changes no launch-template bytes or launch SHA, so the existing 604-unit rendered launch is unchanged for the same Lead identity. It shortens the later two delivered role bodies; those tool responses contain snapshot/version metadata and are not guaranteed stable provider cache prefixes. Changing role version/content invalidates any cached prefix extending into the changed tool response; subsequent caching depends on actual serialized order, provider rules and tool-message stability. No measured provider cache hit rate or token saving claimed. Future prompt injection should order stable common instructions, project and role content before volatile assignment/state and avoid duplicate delivery, but requires a separate reviewed launch/delivery change. Do not promote historical plan text into stable policy.

## Actual validation limit

At exact baseline, Client/Modules/RoleInstructions.fs lines 65–93:
- validBody: non-null, nonblank unless allowEmpty, raw UTF-8 <= 32,768 bytes AND UTF-8 byte count of JsonSerializer.Serialize(value, options) <= 16 * 1024 (16,384) bytes. Default options (camelCase property naming, case insensitive) do not opt out of default JSON encoder. Includes quotes and escaping; not a 16k character limit.
- validPurpose: nonblank raw UTF-8 <= 4,096 bytes.
- validRoles: <=16 distinct valid role IDs. Source comment relates body serialization to 640 KiB explicit instruction page budget. Do not confuse per-body validation with actual page/transport size.
- v17 serialized string 15,807 bytes leaves 577 bytes under that predicate; candidate 7,907 leaves 8,477. Measured using the exact serializer configuration in measure.fsx, not a Python/JS escape approximation. No source validator change proposed.

## Reproduction and limits

The FSI script reads the exact Git ledger blobs (requires verified objects available locally) and the candidate file. Use role/config getters and resolve_agent_instructions for current versions; compare exact OIDs to those in the manifest, not just version strings. Use get_agent_instructions on the selected registered worktree/path for AGENTS lengths and render_launch_prompt for target Lead without activating it. Decode tool JSON once and measure strings by .NET String.Length or JS .length, not escaped outer text. Sum the table using the established two-read delivery contract; inspect actual serialization or provider metrics separately if wire/token/cache figures are needed. Plan and workspace inputs can change; rerun before canonical adoption if snapshots differ. Documentation-only proposal; no runtime build/test, live role edit, integration, or deployment claimed.
