// Standalone presentation contract, NOT a production projection or route test.
// Run: dotnet fsi tests/AutomationThreeStateUi20261127/ThreeStateUiContract.fsx
// Parent spec v2 137690e7ef482295c7107472a97eb1104f387e9b; checkpoint 18143.
// Source baseline 99fb354015d853f732bd27a6dd1e8fb298b20d2d.
// Serial implementation: adapt verified AgentDashboard snapshot to Input and
// AgentView/ConsoleTui outputs to Row/Counts; run check against those adapters.
// Never implement state/dispatch inference in AgentView or use this model as authority.
#load "../../Client/Modules/FilterSelectors.fs"
open System
open Client.FilterSelectors

type Execution = Idle | Working | Blocked
type Evidence<'a> = Verified of 'a | Unknown of string
type Dispatch = Allowed | Held of string
type Demand = Runnable | NoDemand
type Input =
    { Id: string; Project: string; Aliases: string list; Roles: string list
      State: Evidence<Execution>; Dispatch: Evidence<Dispatch>
      Demand: Evidence<Demand>; Focus: string option; Dependencies: string list
      HistoricalDependencies: string list; HistoricalState: string option
      Activity: string option; Review: string option }

type Row =
    { Id: string; Label: string; DispatchLabel: string; DemandLabel: string
      Roles: string list; Focus: string option; Dependencies: string list
      Activity: string option; Review: string option; EvidenceKnown: bool }

type Counts = { Idle: int; Working: int; Blocked: int; Unknown: int; Held: int }

type Adapter =
    { Render: Input -> Row
      Filter: string -> Input list -> string list
      Counts: Input list -> Counts
      Changed: Input -> Input -> bool }

// A minimal reference renderer to exercise the fixture and the oracle.
// Input is already verified by the canonical projection (parallel task A).
let render (input: Input) =
    { Id = input.Id
      Label = match input.State with Verified s -> string s | Unknown _ -> "Unknown"
      DispatchLabel =
        match input.Dispatch with
        | Verified Allowed -> "Allowed"
        | Verified (Held reason) -> "Held: " + reason
        | Unknown reason -> "Unknown: " + reason
      DemandLabel =
        match input.Demand with
        | Verified Runnable -> "Runnable"
        | Verified NoDemand -> "No demand"
        | Unknown reason -> "Unknown: " + reason
      Roles = input.Roles
      Focus = input.Focus
      Dependencies = input.Dependencies
      Activity = input.Activity
      Review = input.Review
      EvidenceKnown = match input.State with Verified _ -> true | Unknown _ -> false }

let contains (term: string) (value: string) =
    value.Contains(term, StringComparison.OrdinalIgnoreCase)

let filter query inputs =
    let terms = parse query
    inputs |> List.choose (fun input ->
        let row = render input
        // Legacy search words are compatibility facets, not execution labels.
        // Parked remains searchable for historical records or explicit holds;
        // Ready/Waiting/NeedsWork are not synonyms for every Idle agent.
        let legacy =
            [ yield! Option.toList input.HistoricalState
              if input.HistoricalState = Some "NeedsWork" then yield "Waiting"
              match input.State, input.Dispatch, input.Demand, input.Focus with
              | Verified Idle, Verified Allowed, Verified Runnable, Some _ -> yield "Ready"
              | Verified Idle, Verified Allowed, Verified NoDemand, None ->
                  yield "NeedsWork"; yield "Waiting"
              | _ -> ()
              match input.Dispatch with Verified (Held _) -> yield "Parked" | _ -> () ]
        let text =
            [ input.Id; input.Project; yield! input.Aliases
              row.Label; row.DispatchLabel; row.DemandLabel
              yield! row.Roles; yield! Option.toList row.Focus
              yield! row.Dependencies; yield! Option.toList row.Activity
              yield! Option.toList row.Review; yield! legacy ]
        if (terms.Projects |> List.forall (fun t -> input.Project :: input.Aliases |> List.exists (contains t)))
           && (terms.Agents |> List.forall (fun t -> matchesAgentTerm t input.Id))
           && (terms.Text |> List.forall (fun t -> text |> List.exists (contains t)))
        then Some input.Id else None)

let counts inputs =
    let state s = inputs |> List.filter (fun i -> i.State = Verified s) |> List.length
    { Idle = state Idle; Working = state Working; Blocked = state Blocked
      Unknown = inputs |> List.filter (fun i -> match i.State with Unknown _ -> true | _ -> false) |> List.length
      Held = inputs |> List.filter (fun i -> match i.Dispatch with Verified (Held _) -> true | _ -> false) |> List.length }

let model =
    { Render = render; Filter = filter; Counts = counts
      Changed = fun a b -> render a <> render b || a.HistoricalState <> b.HistoricalState }

let idle =
    { Id = "Interpreter"; Project = "Jarvis"; Aliases = ["J"]; Roles = ["Interpreter"]
      State = Verified Idle; Dispatch = Verified Allowed; Demand = Verified NoDemand
      Focus = None; Dependencies = []; HistoricalDependencies = []; HistoricalState = None
      Activity = None; Review = None }
let ready =
    { idle with Id = "Worker 1"; Roles = ["Worker"]; Demand = Verified Runnable
                Focus = Some "task-a"; HistoricalState = Some "Ready" }
let working =
    { ready with Id = "Worker 10"; State = Verified Working; HistoricalState = Some "Working"
                 HistoricalDependencies = ["old-wait"] }
let blocked =
    { ready with Id = "Worker 2"; State = Verified Blocked; Focus = None
                 Demand = Verified NoDemand; Dependencies = ["challenge-a"; "challenge-b"]
                 HistoricalState = Some "NeedsWork" }
let stopped =
    { idle with Id = "Worker 3"; Roles = ["Worker"]; Dispatch = Verified (Held "Operator STOP")
                Demand = Verified Runnable; HistoricalState = Some "Parked" }
let historicalPark =
    { idle with Id = "Worker 4"; Roles = ["Worker"]; HistoricalState = Some "Parked"
                Dispatch = Unknown "legacy park origin" }
let unknown =
    { idle with Id = "Worker 5"; Roles = ["Worker"]; State = Unknown "wait index unavailable"
                Dispatch = Unknown "wait index unavailable"; Demand = Unknown "unverified"
                Dependencies = ["known-wait"] }
let reviewer =
    { idle with Id = "Reviewer 1"; Roles = ["Reviewer"]; State = Verified Working
                Activity = Some "Reviewing"; Review = Some "review-42" }
let other =
    { ready with Id = "Worker 6"; Project = "Other"; Aliases = []; Focus = Some "task-b" }
let roster = [idle; ready; working; blocked; stopped; historicalPark; unknown; reviewer; other]

let equal name expected actual =
    if expected <> actual then failwithf "%s: expected %A, observed %A" name expected actual

let check adapter =
    let get = adapter.Render
    let ids query = adapter.Filter query roster |> Set.ofList
    let expectIds query expected = equal ("filter " + query) (Set.ofList expected) (ids query)
    // Explicit expectations independent of the reference renderer.
    for input, label, hold, demand, known in
        [ idle, "Idle", "Allowed", "No demand", true
          ready, "Idle", "Allowed", "Runnable", true
          working, "Working", "Allowed", "Runnable", true
          blocked, "Blocked", "Allowed", "No demand", true
          stopped, "Idle", "Held: Operator STOP", "Runnable", true
          historicalPark, "Idle", "Unknown: legacy park origin", "No demand", true
          unknown, "Unknown", "Unknown: wait index unavailable", "Unknown: unverified", false
          reviewer, "Working", "Allowed", "No demand", true ] do
        let row = get input
        equal (input.Id + " execution") label row.Label
        equal (input.Id + " dispatch") hold row.DispatchLabel
        equal (input.Id + " demand") demand row.DemandLabel
        equal (input.Id + " evidence") known row.EvidenceKnown
    equal "Idle Interpreter no fabricated task" None (get idle).Focus
    equal "Interpreter role" ["Interpreter"] (get idle).Roles
    equal "Ready focus retained" (Some "task-a") (get ready).Focus
    equal "historical blocked work not operative" [] (get working).Dependencies
    equal "all operative dependencies retained" ["challenge-a"; "challenge-b"] (get blocked).Dependencies
    equal "unknown dependency not erased" ["known-wait"] (get unknown).Dependencies
    equal "taskless reviewer has no fabricated focus" None (get reviewer).Focus
    equal "taskless review context" (Some "review-42") (get reviewer).Review
    equal "taskless activity" (Some "Reviewing") (get reviewer).Activity
    expectIds "" (roster |> List.map _.Id)
    expectIds "@Interpreter" ["Interpreter"]
    expectIds "#J @Worker 1 -- Idle" ["Worker 1"]
    expectIds "#Jarvis -- Ready" ["Worker 1"]
    expectIds "#Jarvis -- Waiting" ["Interpreter"; "Worker 2"]
    expectIds "#Jarvis -- NeedsWork" ["Interpreter"; "Worker 2"]
    expectIds "Parked" ["Worker 3"; "Worker 4"]
    expectIds "#Jarvis -- Blocked" ["Worker 2"]
    expectIds "#Jarvis -- challenge-b" ["Worker 2"]
    expectIds "old-wait" []
    expectIds "#Other -- Ready" ["Worker 6"]
    expectIds "#Jarvis -- Operator STOP" ["Worker 3"]
    expectIds "review-42" ["Reviewer 1"]
    expectIds "Unknown" ["Worker 4"; "Worker 5"]
    equal "aggregate categories exclusive, hold orthogonal"
        { Idle = 5; Working = 2; Blocked = 1; Unknown = 1; Held = 1 } (adapter.Counts roster)
    equal "missing evidence not zero/Idle" { Idle = 0; Working = 0; Blocked = 0; Unknown = 1; Held = 0 }
        (adapter.Counts [unknown])
    // Same-state changes must invalidate presentation/cache comparison.
    equal "dispatch hold change redraws" true (adapter.Changed idle { idle with Dispatch = Verified (Held "STOP") })
    equal "demand change redraws" true (adapter.Changed idle { idle with Demand = Verified Runnable })
    equal "dependency change redraws" true (adapter.Changed blocked { blocked with Dependencies = ["challenge-b"] })
    equal "unknown evidence change redraws" true (adapter.Changed idle { idle with State = Unknown "unavailable" })
    equal "focus change redraws" true (adapter.Changed ready { ready with Focus = Some "task-new" })
    equal "identical row stable" false (adapter.Changed idle idle)

check model
// Negative controls demonstrate that the contract fails for representative UI defects.
let rejects name adapter =
    let rejected = try check adapter; false with _ -> true
    equal ("negative control " + name) true rejected
rejects "hide idle Interpreter" { model with Filter = fun q xs -> model.Filter q (xs |> List.filter (fun i -> i.Id <> "Interpreter")) }
rejects "legacy execution labels" { model with Render = fun i -> { render i with Label = defaultArg i.HistoricalState "NeedsWork" } }
rejects "collapse STOP into state" { model with Render = fun i -> match i.Dispatch with Verified (Held _) -> { render i with Label = "Parked" } | _ -> render i }
rejects "unknown defaults Idle" { model with Render = fun i -> if i.State = Verified Idle then render i else match i.State with Unknown _ -> { render i with Label = "Idle" } | _ -> render i }
rejects "no cache redraw on hold" { model with Changed = fun a b -> a.State <> b.State }
printfn "Three-state UI presentation reference contract and five negative controls passed."
