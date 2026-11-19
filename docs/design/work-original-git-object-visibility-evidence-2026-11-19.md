# Original-Git object visibility: pinned negative evidence (Work/C)

- **Task:** `p1-original-git-object-visibility-20261119` / Worker 23
- **Pinned, clean source:** `a83549a6897a4e26aa4834ff81dc38b6e16ee85e` (`origin/main`, 2026-11-19)
- **Disposition:** Object handoff is not demonstrably available to an independent publisher. **No positive admission, source preflight, remote barrier or cutover witness.** No production or protected remote mutation was attempted.

## Exact producer / consumer and missing input

1. The existing client creates original Git trees/commits locally and publishes by `git push ... <commit>:refs/heads/jarvis-orchestration` (`Client/Modules/LedgerStore.fs:2350-2351`; initial and direct-record pushes at :1131 and :1217-1218). The push is both *object delivery* and *protected ref mutation*. `AccountCapacityLedger.fs:692-695` likewise pushes a locally constructed commit. The authenticated fetch path (`LedgerStore.fs:649-710`; account :476-513) only reads objects already exposed by the remote. `sourceRepositoryIdentity` (:1604-1631) identifies a locally configured remote and checks binding consistency; it does not prove an independent server publisher can inspect unpublished local objects.
2. The typed server CAS envelope supplies `ProposedRemoteHead: string option`, not an object manifest or transfer handle (`Server/OriginalGitPublisherAdmission.fs:16-39`). `Server/OriginalGitPublisherExecutor.fs:11-28,127-180` describes a **fixture** remote with `ReadHead` and `ExactRemoteCas`; it has no verified object-reader/transfer method and no registered hosted publisher authority. Its in-memory test fake accepts arbitrary 40-hex OID strings by updating a dictionary (`Server.Tests/OriginalGitPublisherGatewayTests.fs:45-79,94-140`), so an accepted CAS fixture is not commit-object proof.
3. The bare-Git gateway fixture first makes its proposed commit reachable by **pushing** `HEAD:refs/heads/publisher-stage` (`Server.Tests/OriginalGitPublisherGatewayTests.fs:481-495`), then invokes `git update-ref` against the protected ref (:505-537). This is a disposable test setup, **not** approved publisher delivery. Treating a writable staging ref as a real handoff would create an unauthorized alternate Git mutation channel.
4. `Server/WorkLegacySourceWitness.fs:10-13,61-87` explicitly has no production authenticated original-source reader or independently anchored effect source. The P1c design at `docs/design/work-original-git-client-publisher-p1c-plan-2026-11-17.md:54-56,66-74,88` marks object delivery, authenticated route/inventory, replay and remote rule verification unimplemented. Its earlier all-client/credential-exclusion assumption should not be mistaken for a verified current single-operator object path.

**Missing trusted input:** an independently bound original repository/epoch/ref/remote identity **plus** publisher-side proof that the exact proposed Git **commit object and its tree/parents** are actually readable in the same authority that would perform the CAS. A client-provided SHA, local `cat-file`, synthetic `AuthenticatedIdentity`, or source-repository URL is not this proof.

## Hermetic negative reproduction (2026-11-19)

Executed in a `TemporaryDirectory` using local-only `git init --bare`, `git clone`, two `commit --allow-empty` commands, and **one fixture-only initial push** to `refs/heads/jarvis-orchestration`. The second commit was deliberately not delivered. Then checked `git cat-file -e <second>^{commit}` in both the producer checkout and independent bare original repository, and verified the protected ref still equals the first commit:

```text
DISPOSABLE_FIXTURE_PASS
producer_commit_present=True
independent_original_git_object_present=False
protected_ref_unchanged=True
base=1221352e041e28a57a4f2bb9e9321630a03dae18
unpublished_candidate=34cc71ec035366371f46adab83850cd999860fa6
```

This proves **absence at the independent original** in the ordinary separate-repository case, even though the producer knows an exact valid commit OID. No live remote was contacted or changed. It does *not* prove all deployment topologies lack a preexisting authenticated shared-object read path.

## Smallest safe next boundary / owner decision

**Lead/source-owner verification first:** decide and independently evidence whether the actual single-operator deployment already exposes the exact producer object database to the authenticated, approved original publisher under a stable canonical repository mapping. If it does, lease a **read-only, publisher-owned exact-object verifier** composed with that mapping; do not invent a transport. Otherwise separately review and lease a non-ref-mutating quarantined object delivery path (bounded, authenticated, content-addressed, commit/tree/parent verified) **before** any publisher CAS. The reader must check exact target/epoch/ref and expected parent, proposed object kind, object presence, remote head at effect time, and unknown/racing reads; denial must leave protected refs untouched. No caller-supplied positive attestation, alternate staging push, production publisher writer, new credential authority, deployment or cutover is permitted by this evidence.

**Stop condition:** Verified production-side trust/object inputs are unavailable from pinned source. Do not fabricate successful fixture authority or add an unused positive preflight API; yield to Lead for the cross-owner source/host decision. Existing Git-backed Work remains authoritative and the new path remains default-off.

## Adjacent scoped regression (pinned checkout)

- SDK: `dotnet --version` = `11.0.100`.
- `dotnet restore Server.Tests/Server.Tests.fsproj --ignore-failed-sources -v minimal` succeeded in the isolated worktree.
- `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~ServerOriginalGitPublisherGatewayTests --logger 'console;verbosity=normal' -v minimal`: **18/18 passed**, including the bare-Git fixture that explicitly pre-stages objects through its test-only branch. Build emitted existing F# warnings; no test failures.
- No new publisher implementation or executable source was introduced; results above test existing behavior only, **not** authenticated delivery or production safety.
