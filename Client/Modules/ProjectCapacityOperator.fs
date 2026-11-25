module Client.ProjectCapacityOperator

open System
open Client.ConsoleTui
open Client.LedgerStore

// Production composition uses the existing local OAuth subject and approved
// project-binding resolver. There is no independent authority or fallback.
let create
    (authenticate: unit -> string option)
    (resolve: string -> Result<LedgerBinding * LedgerSnapshot, string>)
    : CapacityPolicyUiAccess =
    let actor () =
        authenticate () |> Option.filter (String.IsNullOrWhiteSpace >> not)
    let render failure = sprintf "%A" failure
    let classify = function
        | InvalidBinding "Project capacity policy: stale policy revision" ->
            CapacityStale "The canonical policy revision changed. Reload before editing."
        | StaleEntityVersion detail -> CapacityStale detail
        | InvalidBinding detail when detail.StartsWith("Project capacity policy: ", StringComparison.Ordinal) ->
            CapacityRejected detail
        | failure -> CapacityUncertain(render failure)
    { Load = fun project ->
        match actor () with
        | None -> Error "Authenticated operator access unavailable."
        | Some owner ->
            resolve project
            |> Result.bind (fun (binding, snapshot) ->
                ProjectCapacityPolicy.readCurrentOptional binding snapshot
                |> Result.mapError render
                |> Result.map (fun (policy, _) ->
                    let save operation (draft: CapacityPolicyDraft) =
                        // Reacquire the existing authenticated identity and compare the
                        // entire approved binding, not just project display name or GUID.
                        if actor () <> Some owner then
                            Error(CapacityUncertain "Restore the original authenticated operator before retrying.")
                        else
                            resolve project
                            |> Result.mapError CapacityUncertain
                            |> Result.bind (fun (current, _) ->
                                if current <> binding then
                                    Error(CapacityUncertain "Project binding changed; restore the original trusted binding before retrying.")
                                else
                                    // Do not pre-check current revision: the original UUID's
                                    // receipt must be reconciled before stale-revision handling.
                                    ProjectCapacityPolicy.updateAutomation binding owner operation policy.Revision draft.AutomationEnabled draft.Total draft.Roles
                                    |> Result.map ignore
                                    |> Result.mapError classify)
                    { CapacityProjectName = project; ProjectId = binding.ProjectId
                      LedgerId = binding.LedgerId; Epoch = binding.Epoch
                      Revision = policy.Revision; Draft = { AutomationEnabled = policy.AutomationEnabled; Total = policy.Total; Roles = policy.Roles }
                      Save = save })) }
