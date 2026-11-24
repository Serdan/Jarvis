# Current-source account CAS property rebind

Base: published main `3f84149a29950830d5bb217fa341611d95bfe958`.
The harness checks the source blob `818bb6f8d43ab55177afe17e108e0155e6f43160`
and fixture blob `6206e852903ca9b69ef314fbed5e2092a305e25e`,
including the working-tree bytes. The earlier pin and historical test evidence
remain documented in LINEAGE.md and evidence/.

The integrated fixture uses the configured client roster rather than the
historical disposable signing API; coverage now uses project heads. Transitions
carry the original request receipt hash, candidate, generation, and current
fence/session; terminal locators carry the original request hash and expected
generation. The oracle remains a separate abstract occupancy/receipt model.
The generator uses bounded seeded permutations of a structured trace, not an
exhaustive exploration. A poisoned recorded observation checks comparator and
shrinker sensitivity; it is not a mutation of production code. The test
interposition covers pre-push failure and lost push acknowledgement.

Run `sh scripts/tests/scheduler-property-account-cas-20261122/run.sh` from a
checkout with the pinned source blobs. Results go to TestResults, leaving
historical evidence untouched. Passing an incorrect blob to prepare.fsx must
fail with SOURCE_PIN_MISMATCH; the argument cannot override the pinned blob.
The harness compiles Client.fsproj and does not attest all of its dependencies.
These tests cover the reservation component, not complete sender, provider
account, project, or end-to-end scheduler behavior.

## Verification

On 2026-11-24 the combined Release filter ran six tests successfully on the
pinned source: 384 model seeds (9,216 abstract steps), two-clone races,
negative coverage/identity tests, fault injection, and native differential
seeds 17, 17, and 31 (27 operations each). A wrong source blob caused
`SOURCE_PIN_MISMATCH`. The shell entrypoint could not be run under the Jarvis
client's shell restriction. The direct commands were:

```
dotnet fsi --exec scripts/tests/scheduler-property-account-cas-20261122/prepare.fsx
dotnet test scripts/tests/scheduler-property-account-cas-20261122/PropertyHarness.fsproj --configuration Release --no-restore --filter 'FullyQualifiedName~SchedulerAccountCasProperty|FullyQualifiedName~SchedulerAccountCasNativeDifferential' --logger 'console;verbosity=normal' --results-directory /tmp/jarvis-w13-cas-final
```
 After deriving transition inputs from the independent model, the two native
differential tests passed again: seeds 17, 17 and 31 matched all 27 steps each;
the poisoned observation shrank from 27 to one. The final run used the same
dotnet test command with filter `FullyQualifiedName~SchedulerAccountCasNativeDifferential`
and results directory `/tmp/jarvis-w13-cas-model-final`.
