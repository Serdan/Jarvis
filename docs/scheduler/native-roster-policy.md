# Native Scheduler authenticated roster and typed policy (default-off)

Source task: scheduler-native-authenticated-roster-policy-20261121. Based on Accepted architecture audit SHA 95410cc5eec2383f80160b5aaeb9b15660b45b59, review a0468ce277e744d8a91cd2d263311b16, decision 9647.

## Single account Git authority

The module extends AccountCapacityLedger's existing CAS head, refs/heads/jarvis-account-admission, with a new namespaced blob at native/scheduler-roster-policy-v1.json. The operation SetNativeSchedulerRosterPolicy writes the signed policy, immutable request-hashed operation receipt and the unchanged WQ07 schema-v1 state with its ordinary StateVersion/NextSequence increment, atomically in one Git commit. It does not change WQ07 MigrationPhase, legacy Tokens, or MaxConcurrentAgents semantics.

Account identity, ledger ID, epoch, approved fetch/push remotes, trusted ancestry, remote-head lease/CAS, exact UUID request replay, distinct-UUID version conflict, immutable receipt origin and original writer publication journal reuse existing mechanics. Signed approval pins the previous head and version. There is no second account ref, database, scheduler process or live writer.

The verified receipt-origin commit must have the **signed exact predecessor head** as its actual Git parent, and may change only state.json, that operation's immutable receipt and the native policy blob. The account state transition is verified to preserve legacy configuration, phase and tokens exactly, except for the ordinary sequence/version increment. Git replace refs are disabled for account Git commands, and disposable authority roots reject local graft metadata. A valid signed document transplanted to a different Git parent is not valid approval.

## Signed policy and missing production host proof

The versioned ECDSA P-256 approval binds a domain-separated, length-framed statement of operation UUID, actor label, predecessor policy version and account Git head, typed full account/project membership and signer public-key fingerprint. It signs the authenticated subject, canonical account and account-ledger UUID/epoch, fingerprint of exact approved account fetch+push remotes, complete nonempty list of project UUIDs, unique aliases and ledger UUIDs, project epoch, canonical source ref, fingerprint of project fetch+push remotes, account Worker/Reviewer quotas and per-project Worker/Reviewer quotas (zero is valid).

A valid signed fixture must match an exact-sized complete pinned host project set. Every project is freshly fetched from its independent real Git remote, and canonical project UUID/ledger UUID/epoch verified. Unknown, duplicate, stale, missing, unapproved, altered-signature or foreign account/project identity fails closed. The authenticated subject is never established by a caller's Boolean flag, project alias, local checkout, browser tab, dashboard cache or WQ07 token.

IMPORTANT: A production host OAuth subject/operator-owned approval registry and independent complete account-wide project registry are not provisioned. Therefore public setNativeRosterPolicy is disabled with AccountPolicyNotAuthoritative, and public readNativeRosterPolicy always returns NotAuthoritative with the explicit missing proof. The only executable signed writer/read path is assembly-internal and gated by the existing WQ10 strict private, marked /tmp disposable account Git sink, with genuine ECDSA signed test fixtures and actual bare remotes. It must not be wired to a production host without separate authorization and review.

## Snapshot provenance and occupancy boundary

The disposable read path verifies the single current account Git head, signed blob and exactly matching immutable receipt-origin commit, approved account/subject identity and complete project remotes. It records the verified account head, individually observed project heads, and local wall-clock read time (not authoritative expiry). A re-fetched changed account head results in NotAuthoritative. Project-head reads are not globally atomic across independent remotes; the snapshot is policy provenance, not a live dispatch authority.

A verified signed policy has MembershipComplete=true and PolicyComplete=true but ALWAYS OccupancyComplete=false, AccountCapacityComplete=false and LaunchAuthorized=false. Missing/unverified/foreign project identity or absent account signer proof makes all of those false. No WQ07 Held token, empty browser cache, missing provider session, agent last-seen, elapsed time or signed quota can establish free native capacity. A zero quota is a configured policy limit, not a claim that no session is active.

## Testing and safety fences

NativeSchedulerRosterPolicyTests uses two independent account clients (and a third fresh trust-root clone for transplant tests) and two project bare Git remotes, real P-256 signature verification, request-hashed receipts, competing version-CAS updates, exact replay and lost ACK, changed payload, zero quotas, unapproved/duplicate/foreign identities, incomplete roster, remote and epoch mismatch, WQ07 coexistence, Frozen migration guard, raw Git policy rewrite and old signed policy/receipt transplant to a different Git parent. WQ07 capacity, admission and migration tests must remain green.

No provider session registry, Worker/Reviewer reservation, original-send fence, terminal proof, browser launch, scheduler activation, live migration, schedule action or deployment is added. Native admission and external-effect reconciliation require separately reviewed work; no scheduler may interpret policy completeness as capacity completeness.
