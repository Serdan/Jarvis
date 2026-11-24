module Client.ProjectCapacityDispatchTransition

open System
open System.Text.Json
open Client.LedgerStore

// Preparation only: neither outcome establishes authority to invoke a provider.
type TransitionOutcome = Committed | Replay

let internal beginTransition binding actor operation original effect role roleVersion generation =
    let request = JsonSerializer.Serialize {| original = original; effect = effect; role = role; roleVersion = roleVersion; generation = generation |}
    let intent = { OperationId = operation; Actor = actor; Kind = "ProjectCapacity.BeginDispatch"; RequestJson = request }
    LedgerStore.transact binding intent (fun snapshot ->
        Client.ProjectCapacityAdmission.planBeginSend binding snapshot actor original effect role roleVersion generation)
    |> Result.map (fun result -> if result.AlreadyApplied then Replay else Committed)
