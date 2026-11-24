# Project and role capacity integration contract

Status: design only. No cap is enforced by this document, and no admission or release authority is established here.

## Scope and existing entry points

The operator's scope is Jarvis-managed launches within one project: the project total and each applicable role ceiling constrain both manual and scheduler launches. External provider sessions and cross-project allocation are outside this stage. A project-only result must not be described as an account-wide limit.

The inspected Stage C source at 19378518c5469b1dda8eeaa9b2cd9e3f7d2f8c82 and the prior audit at checkpoint 14021 identify attributed BrowserRunInNewTab, generic BrowserRunInNewTab without a target, BrowserExecuteFile, BrowserExecuteJs, persisted scheduled scripts, and the manager journal as separate potential send paths. Native scheduler dispatch is another path. This inventory is evidence for those paths, not proof that no other sender exists. A positive enforcement claim requires a fresh exhaustive inventory of supported paths and a fence at every applicable send boundary, including old and concurrent clients.

## Shared state and admission

Use the existing authenticated project ledger identity and its Git CAS transaction, not per-client counters or a new signing or approval authority. A single project-scoped atomic transition must read the current configuration and all occupied and unknown entries, enforce the total and role limits together, and publish the original operation receipt and reservation in the same ledger transaction. Stable original effect ID, project identity and ledger epoch, role identity/version, and generation must be bound to that reservation. Duplicate operation IDs must match the complete semantic request; a replay of an already released reservation is audit evidence, not permission to launch again. An uncertain ledger publication must be reconciled using its original operation ID before retry.

Missing, stale, disconnected, conflicting, or incomplete occupancy evidence cannot establish an empty project. Initial reconciliation must be fenced against concurrent launch entry and old writers before a positive admission decision. Until a real issuer and complete fence are established, bootstrap and admission stay closed. No caller-supplied completeness flag is evidence.

## Effect and release

Admission must precede the first irreversible send. The boundary must preserve a durable original-effect journal across restart, distinguish a proven no-send from a possibly sent operation, and retain uncertain sends as occupied. Release requires independently verified, immutable terminal or no-send evidence for the exact original reservation, project, role and generation. Unknown, stale, mismatched, duplicate or caller-asserted evidence must not release capacity. A no-send determination must fence any competing original send; a missing receipt alone is insufficient.

## Integration and verification

W19 owns runtime adapter and Client project-file wiring; W12 owns account-side producer; W14 owns launcher; W20 owns the property model. Integrate their leased callsites only through explicit coordination. This contract does not authorize editing those regions, performing a reservation, sending a provider request, deploying, restarting or activating the scheduler.

A future implementation should be reviewed at a frozen source SHA and tested against a real disposable shared Git ledger with concurrent independent clients: total and role races, duplicate and mismatched operation identities, missing initial coverage, restart, stale generation, unknown and unproven release, original-bound verified terminal/no-send, and every bypass route when the cap is enabled. Tests must distinguish fixture assertions from a real trusted issuer and complete sender fence. Separate source review and composed operational proof from any operator-authorized activation.

## Disposable CAS seam

The new ProjectCapacityAdmission module is deliberately absent from the production project file and all launch paths. Its reservation transition stores project and ledger identity, total and role limits, and original operation, effect, owner, role version and generation together in a project-ledger CAS. A missing or malformed state denies admission; a replayed successful ledger receipt is never authority to send. The future host must derive and verify owner and role from canonical authority within the admission boundary. This lower-level seam does not provide that proof or a production initialization path.

Only the isolated test compilation defines PROJECT_CAPACITY_DISPOSABLE_TEST_ONLY. Its bootstrap assumes a newly created, exclusively controlled disposable ledger. Its begin-send and exclusion transitions compete for the same held record; their positive no-send result is fixture evidence only. No production bootstrap, terminal issuer, verified no-send issuer, restart integration, universal launch fence, scheduler integration, or live limit is supplied. Existing launchers remain uncontrolled, so no enabled-domain enforcement is claimed. In particular, the fixture's exclusion must never be exposed as a production caller-asserted no-send API.

Focused test command: dotnet test tests/ProjectCapacityDisposable20261124/ProjectCapacityDisposable20261124.fsproj -c Release --no-restore --nologo. Tests use disposable bare Git authorities and independent clones. Concurrent total and role reservations, duplicate/mismatched requests, unknown initial state, original identity mismatches, held no-send exclusion, and competing send/exclusion are exercised. These tests do not establish real client restarts, trusted producer provenance, terminal release, all sender coverage, or production bootstrap.

No-fixture compilation: dotnet build tests/ProjectCapacityProductionCompile20261124/ProjectCapacityProductionCompile20261124.fsproj -c Release --nologo. This confirms the seam compiles without the fixture symbol; it does not wire the seam into the production Client.
