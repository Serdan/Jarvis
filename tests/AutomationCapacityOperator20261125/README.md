# Capacity policy operator surface

Task automation-capacity-policy-operator-surface-20261125, spec blob
335e69a1be92a38e7e928c1130509988dfa1042f. Composition base
ece3c2a23dc2ec0ecd7c24855410d25675fe0ba1; accepted preparatory source
180f9d429763742058d531b7a837e1411fb87595 (review
8ec72d7f9c764bd9a4dd70f51ed6b721). Lead custody transfer 15583.
No live policy changes, deployment or activation.

## Operator usage

After the existing local client login, open the Agents view with A, select
an agent in the intended project, and press K. The editor displays the
canonical project/ledger identity, epoch, and policy revision. Use one
total line and optional canonical role lines, for example:

```
total=4
role Worker=2
role Reviewer=1
```

Use total=off to disable the independent total limit; omit a role line to
remove its limit. F2 saves; F5 reloads. A stale revision requires a reload
before another save. An unknown receipt retains the exact UUID and draft
for F2 reconciliation; the pending operation cannot be replaced or closed
in-process. This does not provide cross-process recovery. These limits
apply to Scheduler resume decisions; manual launches remain available and
Working agents count toward scheduler occupancy.

## Composition and proof

The already-registered ProjectCapacityPolicy module is reused, with no
new policy schema, approval, credential, or runtime scheduling path.
Program.registerCapacityPolicyAccess registers only following existing
authenticated local login. Every load/save reacquires the current local
OAuth subject and resolves the selected project's approved binding and
verified snapshot via LedgerBindingConfig.loadWithSnapshot. The editor
captures the complete binding and actor/revision and rechecks them before
calling the existing exact revision CAS. It does not pre-check the policy
revision in place of original-UUID receipt reconciliation.

The dedicated tests reference the actual compiled Client assembly rather
than linked copies. They exercise independent dimensions, removal,
canonical roles, competing and stale revisions, identity/binding mismatch,
denied/unauthenticated access, parser validation, shared modal behavior,
and an intentionally withheld receipt on a disposable Git remote. The
production registration fixture calls the same Program registration and
selected Agents/K dispatch with a disposable approved project binding and
fixture identity (the real OAuth login is not executed). No live account
or production ledger is required.

Run:
```sh
dotnet test tests/AutomationCapacityOperator20261125/AutomationCapacityOperator20261125.fsproj -c Release
```
