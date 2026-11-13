module ServerAgentExecutionGateTests

open System
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open Server.Services

let private admitted = fun () -> Ok ()

[<Test>]
let active_commands_and_aliases_block_correction_without_work_path_git () =
    let gate = AgentExecutionGate()
    let one, two = Guid.NewGuid(), Guid.NewGuid()
    gate.TryAdmit("alice", "Worker 3", one, admitted) |> function Ok () -> () | x -> Assert.Fail(sprintf "%A" x)
    gate.TryAdmit("alice", "WORKER 3", two, admitted) |> function Ok () -> () | x -> Assert.Fail(sprintf "%A" x)
    let mutable verified = false
    let check () =
        verified <- true
        Ok ()
    Assert.That(gate.TryBeginCorrection("alice", "Worker 3", check).IsError, Is.True)
    Assert.That(verified, Is.False, "Do not run expensive correction checks when active.")
    gate.ClientTerminal("alice", "worker 3", one, admitted) |> function Ok () -> () | x -> Assert.Fail(sprintf "%A" x)
    Assert.That(gate.TryBeginCorrection("alice", "Worker 3", check).IsError, Is.True)
    gate.ClientTerminal("alice", "Worker 3", two, admitted) |> function Ok () -> () | x -> Assert.Fail(sprintf "%A" x)
    let correction =
        match gate.TryBeginCorrection("alice", "Worker 3", check) with
        | Ok id -> id
        | Error error -> failwith error
    Assert.That(verified, Is.True)
    Assert.That(gate.TryAdmit("alice", "Worker 3", Guid.NewGuid(), admitted).IsError, Is.True)
    gate.EndCorrection("alice", "Worker 3", correction, true)
    |> function Ok () -> () | x -> Assert.Fail(sprintf "%A" x)
    Assert.That(gate.TryAdmit("alice", "Worker 3", Guid.NewGuid(), admitted).IsOk, Is.True)

[<Test>]
let timeout_or_disconnect_does_not_clear_in_flight_operation () =
    let gate = AgentExecutionGate()
    let req = Guid.NewGuid()
    Assert.That(gate.TryAdmit("alice", "Worker 3", req, admitted).IsOk, Is.True)
    Assert.That(gate.MarkIndeterminate("alice", "Worker 3", req), Is.True)
    let active, unknown, _, _ = gate.Snapshot("alice", "Worker 3")
    Assert.That(active, Is.EqualTo(1))
    Assert.That(unknown, Is.EqualTo(1))
    Assert.That(gate.TryBeginCorrection("alice", "Worker 3", admitted).IsError, Is.True)
    // Only a subsequent authenticated client-terminal receipt can clear.
    Assert.That(gate.ClientTerminal("alice", "Worker 3", req, admitted).IsOk, Is.True)
    Assert.That(gate.TryBeginCorrection("alice", "Worker 3", admitted).IsOk, Is.True)

[<Test>]
let missing_journal_evidence_poisoned_gate_refuses_false_inactivity () =
    let gate = AgentExecutionGate()
    let req = Guid.NewGuid()
    Assert.That(gate.TryAdmit("alice", "Worker 3", req, fun () -> Error "disk full").IsError, Is.True)
    let active, _, _, unknown = gate.Snapshot("alice", "Worker 3")
    Assert.That(active, Is.Zero)
    Assert.That(unknown, Is.True)
    Assert.That(gate.TryBeginCorrection("alice", "Worker 3", admitted).IsError, Is.True)

[<Test>]
let correction_exclusive_lock_closes_check_then_dispatch_race () =
    let gate = AgentExecutionGate()
    use checking = new ManualResetEventSlim(false)
    use finished = new ManualResetEventSlim(false)
    let checkingWork =
        Task.Run(fun () ->
            gate.TryBeginCorrection("alice", "Worker 3", fun () ->
                checking.Set()
                if not (finished.Wait(TimeSpan.FromSeconds(3.))) then
                    Error "test liveness verifier was not released"
                else Ok ()))
    Assert.That(checking.Wait(TimeSpan.FromSeconds(3.)), Is.True)
    Assert.That(gate.TryAdmit("alice", "Worker 3", Guid.NewGuid(), admitted).IsError, Is.True)
    finished.Set()
    let permit =
        match checkingWork.GetAwaiter().GetResult() with
        | Ok id -> id
        | Error message -> failwith message
    Assert.That(gate.TryAdmit("alice", "Worker 3", Guid.NewGuid(), admitted).IsError, Is.True)
    Assert.That(gate.EndCorrection("alice", "Worker 3", permit, false).IsError, Is.True)
    Assert.That(gate.TryAdmit("alice", "Worker 3", Guid.NewGuid(), admitted).IsError, Is.True)
    Assert.That(gate.EndCorrection("alice", "Worker 3", permit, true).IsOk, Is.True)
    Assert.That(gate.TryAdmit("alice", "Worker 3", Guid.NewGuid(), admitted).IsOk, Is.True)

[<Test>]
let lost_terminal_evidence_still_vetoes_correction () =
    let gate = AgentExecutionGate()
    let req = Guid.NewGuid()
    Assert.That(gate.TryAdmit("alice", "Worker 3", req, admitted).IsOk, Is.True)
    let failure =
        gate.ClientTerminal("alice", "Worker 3", req, fun () -> Error "receipt storage unavailable")
    Assert.That(failure.IsError, Is.True)
    let active, uncertain, _, unknown = gate.Snapshot("alice", "Worker 3")
    Assert.That(active, Is.EqualTo(1))
    Assert.That(uncertain, Is.EqualTo(1))
    Assert.That(unknown, Is.True)
    Assert.That(gate.TryBeginCorrection("alice", "Worker 3", admitted).IsError, Is.True)

[<Test>]
let correction_requires_verified_durable_quiescence () =
    let gate = AgentExecutionGate()
    Assert.That(gate.TryBeginCorrection("alice", "Worker 3", fun () -> Error "old client has unknown command").IsError, Is.True)
    let _, _, correcting, _ = gate.Snapshot("alice", "Worker 3")
    Assert.That(correcting, Is.False, "Aborted verification releases only the correction gate.")
    Assert.That(gate.TryAdmit("alice", "Worker 3", Guid.NewGuid(), admitted).IsOk, Is.True)

[<Test>]
let authenticated_users_are_isolated () =
    let gate = AgentExecutionGate()
    let id = Guid.NewGuid()
    Assert.That(gate.TryAdmit("alice", "Worker 3", id, admitted).IsOk, Is.True)
    Assert.That(gate.TryBeginCorrection("alice", "Worker 3", admitted).IsError, Is.True)
    Assert.That(gate.TryBeginCorrection("bob", "Worker 3", admitted).IsOk, Is.True)
