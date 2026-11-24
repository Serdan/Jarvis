#load "InventoryModel.fsx"
open System
open InventoryModel

let ok = function Ok x -> x | Error e -> failwith e
let rejected = function Error _ -> () | Ok _ -> failwith "expected rejection"
let identity = { Project = Guid.NewGuid(); Ledger = Guid.NewGuid(); Epoch = 1; Generation = 1 }
let effect = { Id = Guid.NewGuid(); Original = Guid.NewGuid(); Owner = "worker"; Role = "Worker"; RoleVersion = 1; Generation = 1 }
let initial = start identity |> ok
reserve identity effect initial |> rejected
fixtureBaseline { identity with Generation = 2 } initial |> rejected
let baseline = fixtureBaseline identity initial |> ok
let held = reserve identity effect baseline |> ok
reserve identity effect held |> rejected
reserve identity { effect with Id = Guid.NewGuid() } held |> rejected
reserve identity { effect with Id = Guid.NewGuid(); Original = Guid.NewGuid(); Generation = 2 } held |> rejected
transition identity { effect with Owner = "other" } Excluded held |> rejected
transition identity { effect with RoleVersion = 2 } Excluded held |> rejected
transition { identity with Epoch = 2 } effect Excluded held |> rejected
let sent = transition identity effect Dispatching held |> ok
transition identity effect Excluded sent |> rejected
if occupied sent <> 1 then failwith "uncertain send must remain occupied"
// No no-send condition exists in the model, so even a matching held effect cannot release.
transition identity effect Excluded held |> rejected
if occupied held <> 1 then failwith "unproven exclusion must retain occupancy"
if occupied sent <> 1 then failwith "dispatch must retain occupancy"
printfn "Inventory fixture model checks passed"
