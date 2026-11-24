Disposable exact-issuer composition only. From the repository root, materialize the independently accepted W23 source into ignored obj (do not edit or commit the producer):
```sh
mkdir -p tests/AutomationNativeAccountNoSendEnvelope20261124/obj
git show 6bb5998af609014fc0c4c69c501f028d827dead2:Client/Modules/NativeProjectEffectTransition.fs > tests/AutomationNativeAccountNoSendEnvelope20261124/obj/NativeProjectEffectTransition.fs
test "$(git hash-object tests/AutomationNativeAccountNoSendEnvelope20261124/obj/NativeProjectEffectTransition.fs)" = 14656e5aae8536739c0da3cc6350778812d59c35
dotnet test tests/AutomationNativeAccountNoSendEnvelope20261124 -c Release
```
No production hosts, provider calls, or live ledger writes. The test project compiles the actual frozen W23 planner, not a replica.

The focused project links the account roster suite as well as the new exact-issuer tests. The latter cover Worker and Reviewer A/B/C predecessors, verified release and reuse, idempotent replay, stale or altered evidence, malformed canonical operations, and distinct operation identity. Use the filter `FullyQualifiedName~ExactNoSendEnvelope` to run only the composition cases.
