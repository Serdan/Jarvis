module Client.ProjectCapacityExclusionTransition

open Client.LedgerStore

// Deliberately not included in Client.fsproj: this is a default-off composition
// seam, not a production endpoint or role binder. The existing authenticated
// client must bind actor to its authenticated identity before calling it.
// A fresh commit excludes only the exact Held reservation and competes on the
// same project-ledger state blob as beginTransition. Replay observes the
// original receipt; it is not a second release or permission to dispatch.
//
// This fence covers only send paths that commit beginTransition before their
// first external effect. It cannot fence nonparticipating senders or establish
// provider terminal outcomes. Do not call it as a timeout/transport-error/
// closed-tab/dead-agent cleanup heuristic.
let internal excludeTransition (binding: LedgerBinding) actor operation original effect role roleVersion generation =
    Client.ProjectCapacityAdmission.excludeHeldBeforeSend
        binding actor operation original effect role roleVersion generation
    |> Result.map (fun result ->
        if result.AlreadyApplied then
            Client.ProjectCapacityDispatchTransition.Replay
        else
            Client.ProjectCapacityDispatchTransition.Committed)
