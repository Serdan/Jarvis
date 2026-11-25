# Fixed joint Automation fixture (disposable, default-off)

Base: integrated main `b04150dfc0fa7f903939a61c6387e874570eb716`, verified descendant of Health roster correction `e967acba68e0cef3f502ea94c6f84c07a0445c2b`. This test-only project complements, rather than reproduces, generated parity. Run only this fixture:

```sh
dotnet test tests/AutomationFixedJoint20261125/AutomationFixedJoint20261125.fsproj -c Release --logger 'trx;LogFileName=fixed-joint.trx'
```

The joint test starts the registered production Server host on loopback, registers two JWT-authenticated clients, gets qualifying activity through the genuine MCP ingress, uses the native Health roster/current/recovery implementation and one disposable bare Git ledger with independent clones, then follows the recovered Ready episode and a subsequent Challenge block/resolve/requeue through the real recurring browser schedule caller. Browser frames are handled by a loopback fake extension with the normal bridge handshake, and the source sent over the bridge is the checked-in launch script. No browser, external provider, live project, production scheduler activation, deployment or production restart occurs. Temporary Git, SQLite, configuration and schedule state are isolated. The manual case invokes the real manual command and deliberately refuses the fake browser's openTab request; it tests admission, not an external provider submission.

The server activity store has no deterministic clock injection on this base. The hosted scenario uses genuine server timestamps, a 600-second initial hold and an explicitly accelerated one-second timeout; separate exact 599/600/601 cases use a controlled observation at the NativeHealth dependency boundary and real canonical Git recovery. Those boundary cases do not establish hosted elapsed-time behavior. The recurring scheduler has an injected deterministic clock. The scheduler caller consumes the same authenticated-actor callback as production, with fixture identity; this is not a full remote client authentication/session-reconnect test for the scheduler. The fake browser's outcomes are not real provider acknowledgments.

This fixture is not evidence of an actual OS-process restart. Separate same-process cold reconstruction tests are not substituted for that evidence. Final combined acceptance, full Release run, review/integration and operational verification/cutover remain separate gates.

## Scoped Release evidence (2026-11-25)

Job `a73ee92badc042efa4d7c03e87d97ce1`, terminal exit 0: 4 passed, 0 failed, 0 skipped. All four individual TRX records Passed, counters total=4, executed=4, passed=4, failed=0, notExecuted=0. The production dependencies were built in the same isolated checkout at the pinned main above; the final fixture-only incremental invocation added `--no-restore -p:BuildProjectReferences=false --logger 'trx;LogFileName=fixed-joint-final.trx'` to the scoped command (replace its logger argument). No unfiltered test run.

- TRX SHA256 `bbbe6bd16d13e8a74556a2f5e6ce451a44b16a39f5c21073b1ed6b4b18a7dd3e`
- Test DLL SHA256 `1483205becdaf8c54b8f3441d071c77c7c8f8b6dc99aae9d7ff3fb05a9eeda84`
- Client DLL SHA256 `064eca17bc5fea31fea5726db18646fa14e4594b0e94eeacfb4244f1090cc2a5`
- Server DLL SHA256 `5910507f2d4ea3cdbac7f325a58870c29f35ca6e02549057520f702175c7ff1b`

The three boundary cases were green in the earlier red runs too. Initial joint failures were fixture errors (using raw Execution instead of derived State, asserting an empty recurring completion message rather than `completed: `, and attempting an unsupported ledger list prefix). Final test uses exact pinned disposable Git tree enumeration and verifies two successful resumed episodes plus independent ordinary Ready unknown and no replay. This does not exhaust every negative or threshold permutation in the broader final joint gate.
