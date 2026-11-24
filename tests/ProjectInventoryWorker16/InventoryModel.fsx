// Disposable model only. No authenticated baseline, sender fence, persistence or live admission.
module InventoryModel

open System

type Identity = { Project: Guid; Ledger: Guid; Epoch: int; Generation: int }
type Effect = { Id: Guid; Original: Guid; Owner: string; Role: string; RoleVersion: int; Generation: int }
type Status = Held | Dispatching | Excluded
type Coverage = Closed | FixtureBaseline
type State = { Identity: Identity; Coverage: Coverage; Entries: Map<Guid, Effect * Status> }

let start identity =
    if identity.Project = Guid.Empty || identity.Ledger = Guid.Empty || identity.Epoch < 1 || identity.Generation < 1 then
        Error "invalid identity"
    else Ok { Identity = identity; Coverage = Closed; Entries = Map.empty }

// This transition is ONLY a test assumption for a newly created isolated ledger with no writers.
// It is not a production baseline issuer.
let fixtureBaseline expected state =
    if state.Identity <> expected || state.Coverage <> Closed || not state.Entries.IsEmpty then Error "baseline mismatch"
    else Ok { state with Coverage = FixtureBaseline }

let reserve expected effect state =
    if state.Identity <> expected || state.Coverage <> FixtureBaseline then Error "coverage unknown"
    elif effect.Id = Guid.Empty || effect.Original = Guid.Empty || String.IsNullOrWhiteSpace effect.Owner
         || String.IsNullOrWhiteSpace effect.Role || effect.RoleVersion < 1
         || effect.Generation <> state.Identity.Generation then Error "invalid effect"
    elif state.Entries.ContainsKey effect.Id
         || (state.Entries |> Map.exists (fun _ (e, _) -> e.Original = effect.Original)) then Error "duplicate effect or operation"
    else Ok { state with Entries = state.Entries.Add(effect.Id, (effect, Held)) }

let transition expected effect target state =
    if state.Identity <> expected || state.Coverage <> FixtureBaseline then Error "coverage unknown"
    // No original-bound no-send condition is modeled yet. Never release occupancy.
    // A fixture baseline is not evidence that this effect was never dispatched.
    elif target <> Dispatching then Error "exclusion requires modeled no-send evidence"
    else
        match state.Entries.TryFind effect.Id with
        | Some (original, Held) when original = effect ->
            Ok { state with Entries = state.Entries.Add(effect.Id, (effect, target)) }
        | _ -> Error "original held effect missing or changed"

let occupied state =
    state.Entries |> Map.toSeq |> Seq.filter (fun (_, (_, status)) -> status <> Excluded) |> Seq.length
