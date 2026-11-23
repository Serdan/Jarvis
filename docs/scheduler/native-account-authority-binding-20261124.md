# Native account authority binding (default-off)

## Scope and provenance

Task `automation-native-account-authority-binding-20261124`, spec blob
`619c201b4afd523b37c5bc64e265124e69b8f833`, Worker 12.
Base `d9bea11986bd8b9ac8318c599c934c78de77c235`, source
`refs/heads/feat/automation-native-account-authority-worker12-20261124`.
The candidate `e41f16c44b4549203ad946d1e7df46537c079063` is **not**
an ancestor of this base; the three account/roster/reservation file blobs
are byte-identical (git diff --stat of those three paths is empty).
That is source identity, not acceptance of the candidate or project-fence
composition. Base blobs:

- AccountCapacityLedger.fs: 8f4f3978ce599d0a3c5a411952aa621ee8f24430
- NativeSchedulerRosterPolicy.fs: 99483f6764dd99b98db328d9c367ee573f24e28a
- NativeSchedulerAccountReservation.fs: fd9c9591c74ce906e15dfc191c2dbac0995b8dc3
- Client.Tests/NativeSchedulerRosterPolicyTests.fs: e6988ac42f4191f807b57f04dcb6ddec160b503f

Lease: new `Client/AutomationNativeAccountAuthority.fs`, standalone
`tests/AutomationNativeAccountAuthority20261123`, this document, and
only the internal read-only `readNativeReserveOrigin` adjacent to
`readNativeReservationDocument` in AccountCapacityLedger.fs. No changes
to existing native reserve, transition, release, signed policy verification,
project fence, Work migration/WQ07 writer, host, transport, or runtime registration.

## Authority boundary

At the pinned base, the server authenticates a JWT subject
(Server/Auth.fs:63–73). The operator conversation route derives
an audit actor as `operator:` plus a SHA-256 profile ID from the authenticated
subject (Server/McpTools.fs:74–88), rather than accepting a free-text agent
name. That authenticates the conversation actor, **not** a signed native
account policy. WQ06 explicitly is not work/effect authority
(WorkQueueOperatorConversations.fs:9–12). The manual grant route creates
ephemeral, project-bound capabilities, not durable account approval
(Server/McpTools.fs:1733; Server/ManualGrantStore.fs:11).
The CLI scheduler preview's AccountPreviewBindingFile
(WorkQueueSchedulerHostPreview.fs:426–481) is a local read-only account
mapping; it contains no authenticated subject, operator key, or signed
approval. The host OAuthSession supplies access tokens, not an independently
trusted account-to-subject registry.

NativeSchedulerRosterPolicy.fs:150–199 already defines the signed,
domain-separated, version/predecessor-head-bound approval and ECDSA SPKI
verification. AccountCapacityLedger.fs:1020–1122 independently verifies the
subject/account/ledger/epoch/remote binding and receipt origin. Its production
read/set paths at 1276–1287 deliberately deny when the trust registry is
absent. A search for ECDsa, SignData, ImportSubjectPublicKeyInfo, and native
approval types in Client/Server/Common finds verifiers but no production
native signing/provisioning route. WQ10 migration fixture keys are not native
production authority. No new signing service or trust infrastructure is
proposed or added by this change.

Minimal Lead/Operator decision: identify the already approved host-owned
account/subject mapping and existing trusted operator key/approval provisioning
interface (including the exact complete roster and account identity), or
explicitly authorize an alternative trust contract in a separate decision.
Neither OAuth transport HMAC, a user-selected JSON path, a typed value, a
local approval prompt, nor an agent called Operator is such a binding.

## Typed seam for W16

The account-side observation shape carries exact account head/identity/epoch,
subject, policy version, complete approved membership and per-pool limits, plus
individually verified project heads. It is **not** globally atomic provider
coverage or permission to launch. The disposable-only read delegates to
the existing authenticated, marked fixture reader. It is not a production
registration. A future host-owned port must supply independently approved
authority and coverage; do not turn this fixture API into production authority.

Original reservation context binds the exact original reservation operation
UUID and request hash, full dispatch candidate (including project/source/claim
identity), account receipt origin/parent, current account head, expected
generation, project fence/session identity, and verified project heads.
The current account document is checked through existing native state
verification; the historical first-parent native reserve receipt and state
origin are separately verified. The original coverage parent, immutable
request, original result, and original reservation record must agree; the
same account and project observations are reread before returning context.
This is a read-only evidence result, not a release permit.

Production roster/original read and reconciliation are deliberately HOLD.
No genuine attributable original-bound human reconciliation ingress was
proven. An eventual project-side reconciliation producer belongs to W16, after
agreement with Lead on exact no-send/terminal outcome evidence and existing
operator authorization provenance. A separate account CAS release must bind
that immutable approval/outcome to the exact original receipt, project lineage,
dispatch/effect, session, current generation and release operation UUID/hash.
It must not write a NativeProviderSessionTerminal for human testimony. Do not
infer release from tab closure, Stop button absence, completion/yield, role text
or arbitrary booleans. Unknown stays occupied. The existing disposable
provider-terminal path remains unchanged, and no synthetic terminal or human
approval has been promoted to a production signal.

## Reproduction

The standalone test project uses the pinned existing fixture in a generated
compilation unit under obj/ (same two eta expansions as the prior property
harness), plus focused authority tests. Its assembly name matches the existing
Client.Tests friend assembly; the new adapter is compiled only into this test
project. Run from the project root:

```sh
dotnet fsi --exec tests/AutomationNativeAccountAuthority20261123/prepare.fsx
dotnet test tests/AutomationNativeAccountAuthority20261123/AutomationNativeAccountAuthority20261123.fsproj -c Release --filter 'FullyQualifiedName~AutomationNativeAccountAuthorityTests|FullyQualifiedName~NativeSchedulerRosterPolicyTests|FullyQualifiedName~NativeSchedulerAccountReservationTests|FullyQualifiedName~AccountCapacityLedgerTests'
```

The original fixture is source-pinned by Git blob hash and is not edited.
No unfiltered Client test or actual account policy seed, reservation, provider
send, migration, restart, deploy or activation.


## Durable authority decision

Lead-owned challenge `446160db299740c086558bf4bc40928e`, condition
`1692c91420b9ab7d4685adbab68dcf7a`, creation sequence 12881, records the
missing authority prerequisites and the exact decision needed. The isolated
seam remains nonproduction regardless of the outcome of the independent
source review; acceptance cannot substitute for the authority decision.

## Execution evidence (owner-reported, independent review pending)

Release job `8268f2aae2ba45f38035ff4576bd0f3b`: exit 0, 36/36 tests
passed, 3 new authority tests plus 33 existing native roster, native
reservation, and WQ07 ledger tests. First compilation job
`e8b4a8324a244d51afd7695ba003f8ad` failed on an overload annotation and
record-update indentation; corrected in the final tested source. Generated
fixture and TRX (`obj/results/authority.trx`) are untracked build outputs.
No production authority, effects, or consumer release is implied.
