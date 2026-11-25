# Capacity policy operator preparation

Scope: task automation-capacity-policy-operator-surface-20261125, spec blob
335e69a1be92a38e7e928c1130509988dfa1042f. Base
40d27e7bf33cd38479e50e72b385dbb67e865d65. No live policy or activation.

The base contains readCurrentOptional/updateOptional in
Client/Modules/ProjectCapacityPolicy.fs but no production references to either
function; the policy module is not included in Client.fsproj. The existing
Agents view uses selectedAgentUnsafe().Project and the authenticated
SetLaunchTemplateAccess registration in Program.fs. The capacity editor
reuses that selection and modal, with an independent typed draft.

## Shared-path custody handoff (NOT applied here)

After Lead explicitly transfers Client/Program.fs and Client/Client.fsproj
following W19's integration, rebase/compose against verified current main.
Check whether W19 already registered ProjectCapacityPolicy; if not, insert its
Compile entry after LedgerStore. Include Modules/ProjectCapacityOperator.fs
after both ProjectCapacityPolicy and ConsoleTui, before Program. Do not duplicate
the policy module, replace its implementation or touch scheduled runtime.

Inside the existing Program.fs authenticatedOperator / Some actor branch,
register the following access (using the same local OAuth client and
operatorActorFromAccessToken helper, with a current token on each operation):

```fsharp
let authenticateCapacityOperator () =
    try
        oauth.GetAccessTokenAsync().GetAwaiter().GetResult()
        |> operatorActorFromAccessToken
    with _ -> None
tui.SetCapacityPolicyAccess(
    Client.ProjectCapacityOperator.create authenticateCapacityOperator
        (fun project ->
            OrchestrationApi.LedgerBindingConfig.loadWithSnapshot project rt
            |> Result.mapError (sprintf "%A")))
```

No registration in the unauthenticated branch. loadWithSnapshot resolves the
approved project root, verifies its pinned trust binding and remote ancestry,
and returns the authoritative snapshot. The adapter captures the complete
binding and current actor/revision, rechecks them at save, and invokes the
existing exact revision CAS. Do not preflight away a possible receipt by
checking current policy revision before calling updateOptional with the
original operation UUID. The shared editor holds the original UUID and exact
save closure while an outcome is uncertain; it cannot edit/reload/switch
project until that in-process intent is resolved. This is not a claim of
cross-process recovery or production activation.

Once both modules are registered, remove their linked source entries from the
isolated test project (the Client project reference supplies them). Re-run the
dedicated Release suite and applicable existing Client tests. Verify actual
Program registration plus Agents/K entry and approved binding, without writing
a live ledger. A disposable runtime integration proof is still required;
these tests exercise the actual modal key dispatcher with injected access and
actual policy operations on disposable Git clones, not the production
authenticated Program route. No changes to manual launch admission or
scheduler counting behavior.

Run:

```sh
dotnet test tests/AutomationCapacityOperator20261125/AutomationCapacityOperator20261125.fsproj -c Release
```
