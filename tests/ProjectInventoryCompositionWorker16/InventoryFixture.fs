module ProjectInventoryComposedFixture

// Disposable test-only protocol. Fixture baseline is an assumption, not authority.
open System
open System.Text.Json
open Client.LedgerStore
open Client.ProjectCapacityAdmission

[<CLIMutable>]
type Record =
    { Project: Guid; Ledger: Guid; Epoch: int; Generation: int
      Fence: Guid; Effects: Guid array }

let path = "records/project-inventory-worker16-fixture/state.json"
let encode x = JsonSerializer.Serialize x
let invalid s = Error(InvalidBinding ("inventory fixture: " + s))
let intent actor operation kind value =
    { OperationId = operation; Actor = actor; Kind = kind; RequestJson = encode value }

let read (binding: LedgerBinding) snapshot generation fence =
    LedgerStore.readAt binding snapshot path |> Result.bind (function
      | Some raw, Some oid ->
          try
              let s = JsonSerializer.Deserialize<Record>(raw)
              if isNull (box s) || s.Project <> binding.ProjectId || s.Ledger <> binding.LedgerId
                 || s.Epoch <> binding.Epoch || s.Generation <> generation || s.Generation < 1
                 || fence = Guid.Empty || s.Fence <> fence || isNull s.Effects
                 || Array.exists ((=) Guid.Empty) s.Effects
                 || (Array.distinct s.Effects).Length <> s.Effects.Length
              then invalid "unknown, stale or malformed"
              else Ok(s, oid)
          with _ -> invalid "malformed"
      | _ -> invalid "missing")

// Only for a new isolated fixture with no existing writers or sessions.
let initialize (binding: LedgerBinding) actor operation generation fence =
    if generation < 1 || fence = Guid.Empty then invalid "bad fixture identity"
    else
        let state = { Project = binding.ProjectId; Ledger = binding.LedgerId; Epoch = binding.Epoch
                      Generation = generation; Fence = fence; Effects = [||] }
        LedgerStore.transact binding (intent actor operation "FixtureInventory.Initialize" state) (fun snapshot ->
            LedgerStore.readAt binding snapshot path |> Result.bind (function
              | None, None -> Ok [{ Path = path; ExpectedBlobOid = None; Content = encode state }]
              | _ -> invalid "already initialized"))

let plan (binding: LedgerBinding) snapshot actor operation effect role roleVersion generation fence =
    read binding snapshot generation fence |> Result.bind (fun (state, oid) ->
        if Array.contains effect state.Effects then invalid "duplicate effect"
        else
            planReservation binding snapshot actor operation effect role roleVersion generation
            |> Result.map (fun capacity ->
                { Path = path; ExpectedBlobOid = Some oid
                  Content = encode { state with Effects = Array.append state.Effects [|effect|] } } :: capacity))

let reserve (binding: LedgerBinding) actor operation effect role roleVersion generation fence =
    let request = {| project = binding.ProjectId; ledger = binding.LedgerId; epoch = binding.Epoch
                     operation = operation; actor = actor; effect = effect; role = role
                     roleVersion = roleVersion; generation = generation; fence = fence |}
    LedgerStore.transact binding (intent actor operation "FixtureInventory.Reserve" request) (fun snapshot ->
        plan binding snapshot actor operation effect role roleVersion generation fence)
