module Client.ProjectCapacityAdmission

open System
open System.Text.Json
open Client.LedgerStore

// Default-off planner. No production bootstrap, trusted role binder, effect issuer,
// or send path is supplied here. Callers must not treat a replay as send permission.
[<CLIMutable>]
type Entry =
    { Effect: Guid
      ReservationOperation: Guid
      Role: string
      Owner: string
      RoleVersion: int
      Generation: int
      Status: string }

[<CLIMutable>]
type State =
    { Project: Guid
      Ledger: Guid
      Epoch: int
      Total: int
      Roles: Map<string,int>
      Entries: Entry list }

let private path = "records/project-capacity-disposable/state.json"
let private json x = JsonSerializer.Serialize x
let private reject message = Error(InvalidBinding ("Disposable capacity: " + message))
let private intent actor operation kind request =
    { OperationId = operation; Actor = actor; Kind = kind; RequestJson = request }

let private read (binding: LedgerBinding) snapshot =
    LedgerStore.readAt binding snapshot path
    |> Result.bind (function
        | None, None -> reject "unknown initial occupancy"
        | Some raw, Some oid ->
            try
                let state = JsonSerializer.Deserialize<State>(raw)
                if isNull (box state) || state.Project <> binding.ProjectId
                   || state.Ledger <> binding.LedgerId || state.Epoch <> binding.Epoch
                   || state.Total < 1 || isNull (box state.Roles) || isNull (box state.Entries)
                   || (state.Roles |> Map.exists (fun k v -> String.IsNullOrWhiteSpace k || v < 1))
                   || (state.Entries |> List.exists (fun e ->
                       isNull (box e) || e.Effect = Guid.Empty || e.ReservationOperation = Guid.Empty || String.IsNullOrWhiteSpace e.Role
                       || String.IsNullOrWhiteSpace e.Owner || e.RoleVersion < 1
                       || e.Generation < 1 || not (List.contains e.Status ["Held"; "Dispatching"; "Excluded"])))
                   || (state.Entries |> List.map (fun e -> e.Effect) |> List.distinct |> List.length) <> state.Entries.Length
                then reject "invalid state"
                else Ok(state, oid)
            with _ -> reject "malformed state"
        | _ -> reject "inconsistent blob")

let private change oid state =
    [{ Path = path; ExpectedBlobOid = oid; Content = json state }]

#if PROJECT_CAPACITY_DISPOSABLE_TEST_ONLY
// Only used with a newly initialized, isolated test ledger with no possible writers.
let initializeFixture (binding: LedgerBinding) actor operation total roles =
    let state = { Project = binding.ProjectId; Ledger = binding.LedgerId; Epoch = binding.Epoch
                  Total = total; Roles = roles; Entries = [] }
    if total < 1 || (roles |> Map.exists (fun k v -> String.IsNullOrWhiteSpace k || v < 1))
    then reject "invalid fixture limits"
    else
        LedgerStore.transact binding (intent actor operation "FixtureCapacity.Initialize" (json state)) (fun snapshot ->
            LedgerStore.readAt binding snapshot path
            |> Result.bind (function
                | None, None -> Ok(change None state)
                | _ -> reject "already initialized"))

#endif

// Composable planner: invoke only inside the inventory owner's LedgerStore.transact
// callback, using the same snapshot and a transaction intent binding all inputs.
// Re-run inventory validation and this function on every callback invocation.
// A successful replay bypasses the callback and is not permission to send.
let internal planReservation binding snapshot actor operation effect role roleVersion generation =
    if effect = Guid.Empty || String.IsNullOrWhiteSpace role || roleVersion < 1 || generation < 1 then reject "invalid identity"
    else
        read binding snapshot
        |> Result.bind (fun (state, oid) ->
            let occupied = state.Entries |> List.filter (fun e -> e.Status <> "Excluded")
            if state.Entries |> List.exists (fun e -> e.Effect = effect) then reject "effect already exists"
            elif occupied.Length >= state.Total then reject "total capacity exhausted"
            elif state.Roles |> Map.tryFind role |> Option.exists (fun limit ->
                (occupied |> List.filter (fun e -> e.Role = role) |> List.length) >= limit)
            then reject "role capacity exhausted"
            else
                let entry = { Effect = effect; ReservationOperation = operation; Role = role; Owner = actor; RoleVersion = roleVersion; Generation = generation; Status = "Held" }
                Ok(change (Some oid) { state with Entries = entry :: state.Entries }))

// The actor/role must be verified against canonical authority by the future host.
// This lower-level planner alone is not a complete admission boundary.
let internal reserve binding actor operation effect role roleVersion generation =
    let request = json {| effect = effect; role = role; roleVersion = roleVersion; generation = generation |}
    if effect = Guid.Empty || String.IsNullOrWhiteSpace role || roleVersion < 1 || generation < 1 then reject "invalid identity"
    else
        LedgerStore.transact binding (intent actor operation "FixtureCapacity.Reserve" request) (fun snapshot ->
            planReservation binding snapshot actor operation effect role roleVersion generation)

// Pure planning against the caller's transaction snapshot. This does not establish
// production authority or permission to send; replay bypasses this callback.
// A composing CAS adapter must treat LedgerWrite.AlreadyApplied=true as replay,
// never as permission to send. A new commit alone is not production authority.
let private planTransition binding snapshot actor original effect role roleVersion generation target =
    read binding snapshot
    |> Result.bind (fun (state, oid) ->
        match state.Entries |> List.tryFind (fun e -> e.Effect = effect) with
        | Some e when e.ReservationOperation = original && e.Role = role && e.Owner = actor && e.RoleVersion = roleVersion && e.Generation = generation && e.Status = "Held" ->
            let entries = state.Entries |> List.map (fun x -> if x.Effect = effect then { x with Status = target } else x)
            Ok(change (Some oid) { state with Entries = entries })
        | _ -> reject "original held identity missing or changed")

let internal planBeginSend binding snapshot actor original effect role roleVersion generation =
    planTransition binding snapshot actor original effect role roleVersion generation "Dispatching"

#if PROJECT_CAPACITY_DISPOSABLE_TEST_ONLY
// Fixture send and fixture no-send compete for the same held predecessor.
let transition binding actor operation original effect role roleVersion generation target =
    let request = json {| original = original; effect = effect; role = role; roleVersion = roleVersion; generation = generation; target = target |}
    if not (List.contains target ["Dispatching"; "Excluded"]) then reject "unsupported transition"
    else
        LedgerStore.transact binding (intent actor operation ("FixtureCapacity." + target) request) (fun snapshot ->
            if target = "Dispatching" then planBeginSend binding snapshot actor original effect role roleVersion generation
            else planTransition binding snapshot actor original effect role roleVersion generation target)

let beginSend binding actor operation original effect role roleVersion generation =
    transition binding actor operation original effect role roleVersion generation "Dispatching"

let excludeBeforeSend binding actor operation original effect role roleVersion generation =
    transition binding actor operation original effect role roleVersion generation "Excluded"
#endif
