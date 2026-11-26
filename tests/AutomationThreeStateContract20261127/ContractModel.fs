module ContractModel

// Specification model only. Not a second implementation of Challenge authority.
// The real canonical wait/requeue reader must supply the verdict atomically.
type WaitVerdict = NoOperativeWait | ExactPending | ExactSatisfied | Missing | Corrupt | Partial | Cancelled
type Execution = Idle | Working | Blocked
type Snapshot =
    { Version: int
      Generation: int
      Active: bool
      Wait: WaitVerdict
      HistoricalBlocked: bool
      Held: bool
      RoleEligible: bool
      Runnable: bool
      InFlight: bool
      Applied: Set<int>
      Requeues: int
      Launches: int }

let initial =
    { Version = 0; Generation = 1; Active = false; Wait = NoOperativeWait
      HistoricalBlocked = false; Held = false; RoleEligible = true; Runnable = true
      InFlight = false; Applied = Set.empty; Requeues = 0; Launches = 0 }

let projection s =
    if s.Active then Working
    else
        match s.Wait with
        | ExactPending | ExactSatisfied -> Blocked // satisfaction alone is not requeue
        | _ -> Idle // invalid evidence is not a proven Blocked label; dispatch fails closed

let canDispatch s =
    projection s = Idle && s.Wait = NoOperativeWait &&
    not s.Held && s.RoleEligible && s.Runnable && not s.InFlight

type Command = OwnCall | Finish | RecoverVerified | RecoverUnverified | YieldChallenge
             | Wake | Launch | Observe | AttributeLaunch
type Request = { Operation: int; Version: int; Generation: int; Command: Command }

let apply request s =
    // Model atomic CAS and replay; observation has no mutation or receipt.
    if request.Command = Observe || Set.contains request.Operation s.Applied ||
       request.Version <> s.Version || request.Generation <> s.Generation then s
    else
        let next =
            match request.Command with
            | OwnCall when not s.Active && s.Wait = NoOperativeWait ->
                Some { s with Active = true }
            | Finish | RecoverVerified when s.Active ->
                Some { s with Active = false }
            | YieldChallenge when s.Active && s.Wait = ExactPending ->
                Some { s with Active = false }
            | Wake when not s.Active && s.Wait = ExactSatisfied && not s.Held ->
                Some { s with Wait = NoOperativeWait; Requeues = s.Requeues + 1 }
            | Launch when canDispatch s ->
                Some { s with InFlight = true; Launches = s.Launches + 1 }
            | AttributeLaunch when s.Active && s.InFlight ->
                Some { s with InFlight = false }
            | _ -> None
        match next with
        | None -> s
        | Some t -> { t with Version = s.Version + 1; Applied = Set.add request.Operation s.Applied }

let request operation command s =
    { Operation = operation; Version = s.Version; Generation = s.Generation; Command = command }
