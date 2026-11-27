# Joint adoption measurements

Executed from the bound isolated checkout on verified base 291b5499051fbddfff1c76308e1a0202a5d1862f: `dotnet fsi --exec docs/reviews/lead-instruction-adoption-20261127/measure.fsx`, exit 0. Script reads immutable ledger blobs and exact candidate files, verifies the role body equals the accepted frozen source, computes SHA-256 and uses the same System.Text.Json options as the actual validator.

| Body | UTF-16 code units | UTF-8 bytes | STJ serialized string bytes | validBody |
|---|---:|---:|---:|---|
| Role v1 (historical) | 1,218 | 1,218 | 1,226 | true |
| Role v17 | 15,676 | 15,684 | 15,807 | true |
| Agent v4 | 2,289 | 2,291 | 2,306 | true |
| Accepted candidate role | 7,873 | 7,873 | 7,907 | true |
| Joint candidate override | 903 | 905 | 915 | true |

Role is unchanged from previous review; combined body is 8,776 UTF-16 units versus 17,965 (51.1% reduction). Override alone falls 60.6%. Decoded bodies, not wire bytes or token estimates.

## Actual source validator and small append rejection

Client/Modules/RoleInstructions.fs at 291b5499 lines 65–93 uses raw UTF-8 <=32,768 and serialized JSON string UTF-8 <=16,384 (including quotes and default encoder escaping); role must be nonblank, agent override may be empty but non-null. Purpose nonblank and <=4,096 UTF-8 bytes, <=16 distinct valid attached role IDs. Lines 156–170 apply body rules also on persisted record reads. Lines 225–253 reject invalid role arguments and version conflicts separately; lines 298–315 check override body and prior config CAS, preserving membership. This is not a character limit or the full transport/page limit. Source comment describes 640 KiB explicit instruction page budget for up to 16 roles plus one override.

Existing role has only 577 escaped bytes headroom. Script's synthetic append of 578 ASCII 'a' bytes gives 16,385 (fails); 100 '<' characters gives 16,407 (fails due to escaping). Thus a small append can cross the canonical bound even below raw UTF-8 cap. Parent v4 Phase C records prior small append InvalidBinding, but exact rejected request/receipt not available here; the observed validator and synthetic reproduction establish the mechanism, not conclusive historical attribution among all possible invalid arguments. Candidate role headroom 8,477; override headroom 15,469. No live mutation or limit increase attempted.

## Representative delivered context

Actual selected-worktree `get_agent_instructions` returned workspace AGENTS.md 1,871 and project AGENTS.md 5,428 decoded units, no extra hierarchy for docs/reviews. Actual `resolve_agent_instructions` for Lead (snapshot 1f3824722f7cd409ac644567f8b060336d71f055) returned v17 and v4 exact blobs, lengths and SHA values in adoption.md. Actual render_launch_prompt at 98a7fd3fdbde1653b8bc8822f8d7dafebf196a84 returned 604 units for Lead, template v8 blob d772b48e74919a2902414dfa2ed74c79a3f069c6, prompt SHA-256 2a1e2a684ead19ed8c79929e158c6f9c24887521dd167a3c12f6daddcf09c1f9. The project AGENTS explicitly says inbox and explicit instruction reads each deliver the role/agent layers. Thus use actual decoded bodies and that delivery contract to reconstruct representative bootstrap; no activation of another agent and no pretend actual live Lead inbox.

| Decoded body component | Current | Role only | Joint |
|---|---:|---:|---:|
| Launch | 604 | 604 | 604 |
| Workspace + project AGENTS | 7,299 | 7,299 | 7,299 |
| Role + agent in inbox | 17,965 | 10,162 | 8,776 |
| Role + agent in explicit read | 17,965 | 10,162 | 8,776 |
| Bootstrap subtotal | 43,833 | 28,227 | 25,455 |
| Historical representative plan HTML once | 21,604 | 21,604 | 21,604 |
| Total including that plan | 65,437 | 49,831 | 47,059 |

Joint saves 18,378 decoded units (41.9% of bootstrap, 28.1% with illustrative unchanged plan); additional 2,772 versus role-only. Plan is the earlier measured immutable Lead plan v354 blob 717ae71e7ec34b89872220eef4cfe85baa71e8a3, **not** a fresh live plan claim. Plan is a separate read, not claimed part of inbox. Metadata, queues, envelope/wrapper escaping, exact assignment/spec/checkpoint reads, job responses, provider wrappers and actual token counts excluded. Re-measure real current hierarchy, prompt and actual full workflow at adoption/production measurement; do not infer whole live context reduction from these sums.

Current launch template unchanged byte-for-byte; role/config are later tool messages, not the existing launch prefix. Any cached prefix spanning changed role/config may invalidate; future stable cache-ordered prompt-first delivery and deduplication are separate scoped work, with actual cache/token metrics required. No claim about provider cache hits. No application runtime test necessary for this documentation-only proposal; no build/deployment/integration performed.
