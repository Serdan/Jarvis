# Native provider session host — historical Stage A/B and Stage C checkpoint

**Current scope (2026-11-24):** task specification v7,
`e5d5b5713698dec2e4bc74fad39988dfd512c7c8`, supersedes the signing and
separate issuer requirements below. The Scheduler runs inside the existing
authenticated client/configuration/ledger boundary. No new approval,
signer, subject registry, or second authentication scheme is required.
The original Stage A/B evidence remains historical, not current acceptance.
Unknown external outcomes remain occupied; there is no automatic replay.
No runtime activation is authorized.

Task: `automation-native-provider-session-host-20261123`, specification v3
(`14e476a9bbb62b7b211be2620b58e02bc1ddf82f`). This is a **partial
implementation**, not a production host, terminal issuer, integration decision,
or automatic release authorization.

## Exact local prerequisite composition

The worktree `automation-native-provider-session-host-worker16-20261123`
was created at published property aggregate
`d63c49dacda93e246c3b38fe3854ea7a0b2f2135` (publication receipt 12704).
Exact native account `5607baf9913f4976add13c984e0182906d4fe8c7`,
Phase2 snapshot `3ea482c5fedd95b3a88817f0b8a432e93955fd34`, and Stage A
property fence `a2e244bf747c4dec89907dd6207bf3b4c4098888` were verified
as ancestors. Remote main subsequently moved to the separate Health fixture
merge `d9bea11986bd8b9ac8318c599c934c78de77c235`; no unrelated work
was imported.

The following exact accepted source heads were verified against their frozen
remote refs and composed locally using their individual patches with
`git cherry-pick -x`, not a merge of unrelated private preflight ancestry:

- W12 trust contract: `ed1b6d671067de2c27fee82852c9e63a8991105a`,
  decision 11864.
- W16 Started ingress: `12f21309c084b8a325892aab3a3c03b6aef6ce9c`,
  decision 12443.
- W17 default-off challenge wake: `314c328a3f92afc66771276e0424538d9afd564e`,
  decision 12196.
- W23 corrected canonical template: `0ff5ad90fcb219773cce0537f60333d50c3a00ab`,
  decision 12732.
- W19 browser attribution: `415ef34cf6717be2d0aad00c3dc5b3a224ed6828`.

Local checkpoint before adding B1:
`953a07d0995c7d122029622184787fe15d1e894f`, tree
`5231698b500762e49128cfe54532a5bea053922d`.
Both W12 and W16 fixture builds initially reported FS0225 missing
`Client/Modules/NativeProjectEffectFence.fs`: the published a2e244bf
Stage A JS property model does not contain that F# source. The separately
accepted W22 B1 head `74fcda395857669c5db0f80ef997b4f2d6e37fcd`
(review `c82ba0618d8b41f395b12ef1adab22b1`, decision 10938) was
verified against its immutable review ref and canonical Accepted record;
its exact three-patch series was cherry-picked, including
`5cf8f99542a9653a013683e32e56a2c32a706164` and
`67e2ed9d80607daa3dc87157d535717454e817d2`.
The F# file matches blob `255e9c6956e2e110c738e4c27e01959ea0ca6b8a`
of the W12/W16 prerequisite preflight.

Final local pre-implementation composition:
`81365fa427fb3f9d66921c84d516f04ea197207e`, tree
`877bd0be3b66ef9ce2241dca9bdbc49fef0de153`.
This is not the Integrator's independently accepted shared composition.
None of the source review refs were modified or published by this worker.

## Implemented boundary

The new module `Client/AutomationNativeProviderSessionHost.fs` is compiled
only in the standalone focused test assembly. It reads the existing real
HMAC-paired BrowserBridge window registry and rejects missing, unavailable,
ambiguous and changed generations. It does not accept an injected
browser-shaped observation as a capability. The window registry is transport
preflight evidence, not a provider first-connect.

The host rereads the signed disposable account reservation and canonical
project head; invokes the common `renderLaunchPromptAt` on exactly that
snapshot; checks the full serialized browser argument envelope with
`BrowserArguments.fits`; then adds original launch metadata and exact raw
wire JSON to the same W16 Started pre-send project CAS. The metadata includes
the original reservation/claim/dispatch/effect and source identities, account
generation/key, project name, template version/blob/prompt digest, argument
digest and observed window generation. Raw wire bytes are a separate
immutable file so a 64 KiB valid envelope is not rejected by escaping it
into a second JSON string larger than the ledger entity limit.
Both paths are derived from the original effect UUID and require absence.
The new operation kind binds hashes of both files. The existing Started
project fence still verifies signed roster/account head and original receipt,
current Started identity, source generation and lease, and project head
inside the transaction. No second launch path exists.

Reconciliation reads the original canonical operation and its immutable
origin, requires both bound paths, validates the original claim, project
binding and exact digests of both blobs and verifies the original rendered
prompt digest. It does not rerender an uncertain launch, and returns no
send/release capability. The signed-disposable function is internal and its
project name input is fixture-supplied; a future runtime adapter must derive
the canonical project display name from the authenticated runtime binding.
Reviewer candidates are denied at this Worker ingress rather than
reinterpreted as Started workers.

The only modification to the separately accepted W16 module is the
`claimVerifiedDisposableCore` private refactor and the internal
`claimVerifiedDisposableWithLaunchIntent` adapter. Existing
`claimVerifiedDisposable` uses the same operation kind, request, and
writes as before. The two extra paths are only allowed at the exact
original-effect-derived paths. The frozen W16 and W12 refs are unchanged.
No Stage B shared source files were edited; the old trusted contract is
not promoted from shapes into authority.

## Unavailable authority / explicit HOLD

Existing `BrowserBridge.Service` authenticates extension transport using
a HMAC challenge. `ListWindows()` flattens connections' registries
without exposing the owning connection; `ListTabs(ownerConnectionId)`,
`OpenTab`, and `Execute` can select or pin transport connection and
tab registration/generation. None proves an authenticated provider
session/turn identity or a provider terminal. `launchAgent.js` observes
submission, route and generating UI controls; a DOM observation, browser
ACK, disappearing tab, missing activity, or Jarvis lifecycle transition
is not independent terminal evidence. The native account read/reserve
production boundary is still explicitly disabled. The existing W12
interface is not an issuer.

Accordingly the module explicitly denies provider dispatch and terminal
release. It does not create a provider signer, fake authenticated session,
actual account reservation, live browser effect, or automatic release.
A lost or uncertain response remains occupied. The existing canonical
terminal release path must not be supplied with invented origin/provider
OIDs. Reviewer claim/source lease authority must be independently verified.

Lead challenge `6f79f22487ca45b69425b608b90fdcb3` condition
`200ca25b373aee1d6700a865a7112e8b` requests exact accepted composition,
bounded Stage B function lease, and source-real authority decision.
If the signal is unavailable, the original project fence's permitted
attributable Operator reconciliation is a possible fallback, not a
terminal substitute or automatic release. Such a path would require
separate authorized implementation of an authenticated, explicitly
attributed Operator decision bound to the same original account/project
ledger identities, reservation, dispatch, effect, source/assignment
generation, current heads, immutable prior effect record, and a unique
reconciliation operation UUID. Preserve compare-and-swap and one-time
release constraints and keep unproven evidence occupied. No such fallback
is implemented or authorized by this Stage A code.

## Tests

All test executions are focused disposable fixtures. The offline WebSocket
peer uses the actual existing BrowserBridge challenge/response and window
registry; it is not a real provider and never executes a launch. Git
fixtures use local bare repositories, two separately cloned clients, and
the signed-disposable authority already accepted in the W16 suite.
No live provider requests or production ledger mutations occur.

```sh
dotnet test tests/AutomationProviderHostTrust20261122/AutomationProviderHostTrust20261122.fsproj -c Release --no-restore --nologo --filter FullyQualifiedName!~Production
dotnet test tests/AutomationNativeAccountProjectIngress20261123/AutomationNativeAccountProjectIngress20261123.fsproj -c Release --no-restore --nologo
dotnet test tests/AutomationNativeProviderSessionHost20261123/AutomationNativeProviderSessionHost20261123.fsproj -c Release --no-restore --nologo
```

W12 focused baseline: 29 passed, deliberate production RED excluded
(job `b741e271b1b24f46b3d110d60af75af2`).
W16 baseline: 10 passed (job `254bfc12d2c34d8799e1a3885102a102`,
prior to separating wire bytes from metadata).
Final tests can reuse the exact Release Client build with
`-p:BuildProjectReferences=false`. Initial host test exposed the common
renderer prompt hash's `sha256:` prefix; reconciliation now uses that
same digest function, rather than an unprefixed account hash. The first
seven host tests passed after the correction and durable raw wire split
(job `622e011e77064e1c914967053cc93578`).

No production RED result is claimed as PASS. Stage B, an actual
source-real provider identity/terminal producer, independently accepted
Integrator composition, and fresh independent review remain outstanding.

Final host suite: **10/10 passed** (job `c89d4080cf3347218b44a7f079eda5d9`), including exact 65,536-byte accepted full envelope, minimal-only-fit rejected envelope, duplicate authenticated window registration, rewritten argument blob denial, and actual race of two separately cloned Git clients. Final W16 re-regression after atomic split: **10/10 passed** (job `3f0daa3d83ec47418ee2a1a1d5dac891`). Both final commands used `-p:BuildProjectReferences=false` with the exact previously built Release Client dependencies. All three focused suites: 49 passed; no unfiltered client tests, live provider sends, or runtime activation.


## Stage B — accepted composition, exact transport owner, compiled default-off ports (2026-11-24)

The preceding Stage A notes and results are historical; Stage B is based on
the exact immutable remote ref
`refs/heads/jarvis-review/integrator/9cb405cd2dfd4c349e8149a810b7d6af/r1`,
advertising `e41f16c44b4549203ad946d1e7df46537c079063`.
Lead's canonical resolution of challenge
`6f79f22487ca45b69425b608b90fdcb3`, event 12843, records independent
Reviewer 1 Accepted v3 decision 12838 and exact Stage B lease, task spec v4.
The accepted composition contains the actual NativeProjectEffectFence.fs
blob `255e9c6956e2e110c738e4c27e01959ea0ca6b8a`.
Remote main was independently observed at
`d9bea11986bd8b9ac8318c599c934c78de77c235`. A new registered isolated
worktree was created at e41f16c; only the frozen Stage A commit
`44d3a53dda1fb92aa8016ca07512dd51c673c758` was cherry-picked, yielding
`85ec8be22`. The old Stage A branch remains intact. This is not a main
publication or a claim of integration of the current delta.

The additive internal `BrowserBridge.Service.TryGetExactWindowOwner`
looks up an exact window registration (not aliases) under the same lock
that protects the authenticated connections and registrations. It rejects
duplicates, non-ready/closed connections and missing or out-of-range
generations. The host compares the connection ID *and* generation at both
preflights, and binds them into the v2 disposable immutable launch-intent
record; the readback verifies the original request digest and schema. The
connection ID is ephemeral transport provenance, not a provider identity.
A same-generation reconnect cannot be treated as the same owner. No
change to ListWindows, bridge send, pairing protocol, tab execution,
launcher, legacy scheduler or Health.

Program keeps the single B1 registration, postcommit callback and
shutdown path. The host's compiled `defaultOffHostPorts` factory supplies
a denied verified-head reader and reports the exact HOLD reasons. Program
retains its hard-coded false flag and disabled coordinator. The new
test project references the compiled Client, no longer compiling linked
copies of the host or ingress. Signed fixture writes remain internal and
restricted by the original disposable account verification; the production
claim entrypoint still denies. No independent trust root, external effects
or actual account reservations are introduced.

### Remaining authority boundary (not PASS)

The existing local OAuth login derives an operator actor from the returned
token subject (Program.fs 117–134). Its existing human reply ingress
(OrchestrationApi.fs 3052–3070) binds a Challenge reply UUID, challenge,
condition and version; this is not an actual independently verified
native original-reservation outcome. The project effect fence
(NativeProjectEffectFence.fs 280–327) supplies *test-only* fake terminal
and no-send operations; neither can furnish the production
NativeProviderSessionTerminal operation required by the account verifier.
No conversion from human Challenge evidence, browser connection/window
identity, UI state, activity, or agent lifecycle to a provider terminal
is authorized.

Lead explicitly holds automatic reserve/dispatch/release; W12 task
`automation-native-account-authority-binding-20261124` owns the
account-side approved roster and reconciliation verifier. Its exact
independently accepted contract and genuine original-bound outcome
evidence are required before any project-side human reconciliation
producer. Current source does not establish that evidence. The
default-off host does not call a release, fabricate a terminal or accept
generic role/approval text. Unknown remains occupied. Reviewer claim/lease
ingress is independently held, never manufactured from Worker claims.
Do not activate the scheduler or infer milestone completion from the
bounded source acceptance.

### Stage B regression history

Initial full Release Client compile succeeded. Host job
`bb4e3a5c64dc4dd6bc2ef42f6c7a9bd2`: 10 pass, 2 fail, both NUnit boxed
F# Result comparison type mismatches; after typed assertions the compiled
host suite passed 12/12 (job `0c819a00a1184d5a8dc0c9191a6e748b`).
Predecessor W12 29/29 (job `e66a05a1742c4e65a633c6c9cdff0090`,
intentional production RED excluded), W16 10/10 (job
`948577f678ee4d8291066bd95a28fa0b`). B1 job
`3f49f1bc700640869d9328134ab4e672` identified removal of the direct
Program registration anchor (12 pass, 1 fail). The correction retains
the existing direct registration and uses only the native HOLD ports
factory, preserving the W17 regression source unchanged. Final tests must
use the rebuilt exact Client after this correction.

Final corrected Stage B Release results (all exit 0; exact rebuilt Client):

| Suite | Passed | Job |
| --- | ---: | --- |
| Native session host | 12/12 | `0e39b8eba7554841937395f890d34bd4` |
| W17 Challenge wake/B1 | 13/13 | `59e23066a48f45bc8d5161c50da83bd8` |
| W12 provider trust | 29/29 | `771a23c70c2f4c5ea37d6ed0dbe0e34d` |
| W16 Started ingress | 10/10 | `ba3e76d0d183477180fead652a70d222` |

The first command rebuilt the real Client in Release; the three predecessor
commands used `--no-restore -p:BuildProjectReferences=false` with that
build. The W12 command excluded the deliberate production RED using
`--filter FullyQualifiedName!~Production`. W17 regression source and all
shared wire files remain unchanged. In total 64 focused tests passed.
No unfiltered Client tests, production send, reservation, terminal
release, deployment, restart or runtime activation were performed.
`git diff --check` passed before freezing.

## Stage C — reviewer preparation and existing-auth interface dependency

The isolated worktree `automation-native-provider-session-host-stagec-worker16-20261124`
starts from exact independent Stage B Accepted commit
`90dff82aeb3cf5f4885468fccaf9c3ee2bb381a0`, tree
`da4a00d5ded11adb3db280b7d95b2c439c9c067f`, immutable review ref
`refs/heads/jarvis-review/automation-native-provider-session-host-20261123/8c3e3dc550ce4ec38ed86ec052acc816/r1`,
decision sequence 12897. No changes to its frozen source.

The separate ReviewerProjectClaim and reviewer fence path bind project,
ledger, epoch, review ID/version and canonical claim-operation UUID;
Worker attempt fields cannot supply this identity. The fence key is
independent of a retry operation UUID. Reviewer rendering reads the common
launch template and canonical role/agent layers at the same project
snapshot, retaining a role resolution digest and the full exact argument
bytes. A distinct OriginalReviewerLaunchIntent record and read-only
journal reader bind the original operation, review identity, review-fence
path, transport observation, and immutable argument/prompt hashes.
The journal reader does not validate a current claim or grant send
permission. Actual same-head reviewer verification and the project CAS
are not implemented yet. The project-only journal fixture deliberately
seeds a test transaction to exercise historical readback; it is not a
positive test of the real reservation-to-project writer.

The prior Worker adapter remains unchanged pending replacement by Worker
12's existing-auth interface, rather than broadening the superseded
signed-fixture path to reviewers. The proposed unused trust-host interface
and issuer-fingerprint receipt have been removed; structural observation
checks, real paired transport, and exact original terminal matching remain.
This does not implement a provider outcome or an operator reconciliation.

Lead challenge `e8a0900cd83744308c510119ab155601` was resolved at
sequence 12966, authorizing the distinct W12/W16 leases (not proof of an
implemented interface). W12 task `automation-native-account-authority-binding-20261124`,
spec v3 `82a2103299dd2d3bf002a5fc4e9e86c92a177157`, owns the account
side and shared same-snapshot canonical reviewer verifier. Dependency
`72be7bf11e864787bb40a76c1366dffc`, condition
`4ae7a9b1690833c89806e2b6ee007944`, requests actual accepted commit,
review evidence and callable interfaces, including normal configuration
fixtures. The consumer must use that verifier rather than a duplicate
implementation or caller-asserted trust. Replace the legacy signed
adapter and signing-dependent test setup with the accepted existing-auth
path; then connect the distinct reviewer fence + launch metadata/argument
CAS with no replay and exercise both Worker and Reviewer under real
two-clone races. Preserve all existing runtime and provider-boundary
leases. Do not present the intermediate tests as corrected architecture
acceptance; fresh independent review and composed tests are required.

### Stage C — existing-auth Worker and Reviewer consumer connected

The preceding Stage C preparation subsection is historical. Per pinned
specification v8 (00639e2f72766240c6b4838e0373416ab925233c), W16 was
authorized to compose and test against frozen W12 candidate
4a400be780d8e0b91cbd6ff6913fffff455b675b (tree
1b93f8f5df2b6a2ef4072f6b5df2e98532ffeef8) without awaiting its acceptance.
It is **not** accepted: review b38c6c3596d448e299773c8912a6d113
ChangesRequested, decision 13310. No ownership of W12 code was imported
into the W16 feature branch.

W16's consumer uses readNativeRosterPolicy, readOperation and readOriginal
with actual configured account/project bindings, verified coverage, exact
original receipt hash and candidate, generation one and absent prior
project fence/provider session. It checks current per-kind positive limits,
then repeats original account verification inside the same project CAS as
the canonical Worker Started/source lease or W12 verifyReviewerClaim
validation. No second signing identity, authority callback, or production
deny solely for absent Scheduler-specific approval remains. Both kinds use
the W12 noSendFencePath and require the path to be unambiguously absent.
The distinct Reviewer claim and launch journal remain bound to the exact
immutable claimed review ref/head/version/operation and canonical role
resolution. Both kinds validate the paired window connection/generation
and exact wire payload; neither replays an existing original effect.
The Worker journal reader accepts the historical disposable operation kind
as read-only provenance. Terminal observations remain non-authoritative.

The source/test delta is frozen at W16 commit
3464199d85e8b756918fb0f34e2ae5819878ecd7 (tree
ea5fc594d618a276d9172a9a91401162a2506ad4) on the registered Stage C
worktree. Disposable QA worktree native-stagec-compose-w16 combines it
with the two W12 producer commits fad737653905ad2b255adaef116c65bf2ef512db,
4a400be780d8e0b91cbd6ff6913fffff455b675b and the exact accepted shared
integration merge 9e66a2cbeefb0493d6662c7a075713e0f3b58378 (first-parent
patch required by LedgerStore.verifiedIntegrationOperationSnapshotAt).
The final source/test composition is e90158701dbfd1dcf809e755d10508efb7ff81e6,
tree f3635565d67a2e9e539bd98ecbc65f51d32ef66b.
It is a local QA composition only; no main publication.

The positive host fixture uses actual normal configured account
reservations, independent Git clones and existing authenticated paired
bridge, including concurrent Worker and distinct Reviewer project CAS.
Negative cases exercise stale/released/reclaimed/foreign claims,
role removal, source/ref mutation, reviewer quota revocation separately
from Worker quota, duplicate original claim, missing window, changed head,
wire limit and immutable readback. Occupied-path cases are negative-only:
arbitrary existing path data cannot be mistaken for absence or grant a
release. They are not a valid no-send issuer or proof.

Release Client rebuilt in job 26e4121a454b4cd9867f4ea2c8ef219f, exit 0,
23/23 host tests passed. Subsequent ingress/trust/B1 jobs use the same
rebuilt Client, with unchanged production source in the final QA tree:

| Suite | Passed | Job | Exit |
| --- | ---: | --- | ---: |
| Host before final occupied-path test additions | 23/23 | 26e4121a454b4cd9867f4ea2c8ef219f | 0 |
| Native Started ingress | 9/9 | 68536e8b9aae432aaf3efc06f83d6461 | 0 |
| Provider trust | 29/29 | 96762eb88aeb4cb9ab3f4d52d83c8db0 | 0 |
| Challenge wake/B1 (unchanged) | 13/13 | f4c4dc0ff3d94615ac9eb1467359c024 | 0 |

The genuine no-send writer and begin-send exclusion are Worker 23's
exclusive lease automation-native-project-no-send-issuer-20261124.
Candidate 51c0708cad4fa9364c1980f636a0f35ea6dbd360, review
a858ed49b090430e986472c57e6e70b6, ChangesRequested 13352 is not an
accepted dependency. W16 challenge 8d344cda39104e879c35d897486d93f2
(condition ef0c4ec5fdcc7bae59d41cb5bf4c8038) requests exact accepted
typed contract from W23; W12 dependency 72be7bf11e864787bb40a76c1366dffc
remains pending. W12's earlier issuer request 683f139d4af343da91dfe286d26508b5
was blocked by its owner W16 because the actual issuer was leased to W23;
W12 should reconcile ownership rather than require a competing writer.

Final acceptance needs independently Accepted exact W12/W23 heads,
the genuine original-bound no-send producer/consumer composition and
tests, then fresh independent W16 review. Actual callsite wiring belongs
to automation-native-runtime-authority-dispatch-20261124, not this lease.
Default-off registration, shared Program/Client.fsproj, BrowserBridge,
Health, legacy B1 and launch/wire files are unchanged in W16 source.
No provider send, actual reservation or release, runtime activation,
deployment, restart, main publication, or operational E2E is claimed.

Final host suite with both occupied-path tests: job 0cb49220c2a84af6aa6f4ab66c98a6cd, exit 0, 25/25 passed, zero skipped. Total 76/76 focused tests on the final production source and final QA test tree. This is bounded candidate verification, not independent acceptance of either pending producer.
