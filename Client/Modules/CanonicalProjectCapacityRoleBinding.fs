module Client.CanonicalProjectCapacityRoleBinding

open System
open System.Text
open System.Text.Json
open Client.LedgerStore
open Client.ReviewWorkflow
open Client.RoleInstructions

// A host-authenticated project command supplies the attributed target and
// exact expected identity. These are assertions, not an authentication token.
// No sender is wired here; this module cannot authorize a launch.
type ExpectedAssignment =
    { Project: Guid
      Ledger: Guid
      Epoch: int
      TargetAgent: string
      Task: string
      Attempt: string
      Generation: int
      Role: string
      RoleVersion: int
      ConfigVersion: int }

// Private representation prevents construction by downstream consumers.
// Evidence is scoped to one CAS callback's snapshot, not a durable grant.
type Witness = private Witness of ExpectedAssignment * string * string

let identity (Witness (assignment, _, _)) = assignment
let snapshotCommit (Witness (_, commit, _)) = commit
let roleBlobOid (Witness (_, _, oid)) = oid

let private reject message = Error(InvalidBinding ("Canonical capacity role: " + message))

let private present (s: string) = not (String.IsNullOrWhiteSpace s)

// ReviewWorkflow validates the outer entity; its nested assignment is
// deserialized case-insensitively. Reject duplicate or noncanonical nested
// keys here rather than accepting the serializer's last-value-wins behavior.
let private requireUnambiguousFocus binding snapshot agentId =
    let key = Convert.ToBase64String(Encoding.UTF8.GetBytes(agentId: string)).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    LedgerStore.readAt binding snapshot ("agents/" + key + ".json")
    |> Result.bind (function
        | Some raw, Some _ ->
            try
                use doc = JsonDocument.Parse raw
                let focus = doc.RootElement.GetProperty("currentAssignment")
                let fields = focus.EnumerateObject() |> Seq.map (fun p -> p.Name) |> Seq.sort |> Seq.toList
                if fields = ["attemptId"; "taskId"] then Ok ()
                else reject "ambiguous nested assignment identity"
            with _ -> reject "missing or malformed nested assignment"
        | _ -> reject "missing agent record")

// Call on EVERY invocation of the admission CAS planner. Never fetch here.
// An unattributed or taskless launch cannot provide this assignment witness.
// The currently supported admission entry has one role; multiple attachments
// are ambiguous, so deny instead of choosing one or undercounting other roles.
let internal projectAt (binding: LedgerBinding) (snapshot: LedgerSnapshot)
                       (attributedTarget: string option) (expected: ExpectedAssignment) =
    if isNull (box expected) || binding.ProjectId = Guid.Empty || binding.LedgerId = Guid.Empty
       || binding.Epoch < 1 || expected.Project <> binding.ProjectId
       || expected.Ledger <> binding.LedgerId || expected.Epoch <> binding.Epoch
       || snapshot.Header.ProjectId <> string binding.ProjectId
       || snapshot.Header.LedgerId <> string binding.LedgerId
       || snapshot.Header.Epoch <> binding.Epoch
       || not (present expected.TargetAgent && present expected.Task && present expected.Attempt && present expected.Role)
       || attributedTarget <> Some expected.TargetAgent
       || expected.Generation < 1 || expected.RoleVersion < 1 || expected.ConfigVersion < 1 then
        reject "missing or mismatched attributed project/assignment identity"
    else
        requireUnambiguousFocus binding snapshot expected.TargetAgent
        |> Result.bind (fun () -> ReviewWorkflow.getAgentWorkingTurnAt binding snapshot expected.TargetAgent)
        |> Result.bind (fun agent ->
            if agent.AgentId <> expected.TargetAgent || agent.Version < 1
               || isNull (box agent.CurrentAssignment)
               || agent.CurrentAssignment.TaskId <> expected.Task
               || agent.CurrentAssignment.AttemptId <> expected.Attempt
               || not (List.contains agent.State ["Ready"; "Working"]) then
                reject "registered target has no matching current assignment"
            else
                ReviewWorkflow.getTaskAt binding snapshot agent.CurrentAssignment.TaskId
                |> Result.bind (fun (task, attempt) ->
                    match attempt with
                    | Some a when task.TaskId = expected.Task && task.Version > 0
                                  && task.Lifecycle = "InProgress" && task.AttemptId = expected.Attempt
                                  && a.TaskId = task.TaskId && a.AttemptId = task.AttemptId
                                  && a.Version > 0 && a.OwnerAgentId = agent.AgentId
                                  && a.AssignmentGeneration = expected.Generation
                                  && ((agent.State = "Ready" && agent.Execution = "Stopped"
                                       && List.contains a.Phase ["Queued"; "Started"])
                                      || (agent.State = "Working" && a.Phase = "Started")) ->
                        RoleInstructions.resolveAtKnownAgent binding snapshot agent.AgentId
                        |> Result.bind (fun roles ->
                            match roles.RoleIds, roles.Documents |> List.filter (fun d -> d.Scope = "role") with
                            | [role], [document] when role = document.Identifier
                                                      && role = expected.Role
                                                      && document.Version = expected.RoleVersion
                                                      && roles.AgentConfigVersion = expected.ConfigVersion
                                                      && roles.SnapshotCommit = snapshot.Commit ->
                                // Build from decoded records, not assertions.
                                let actual =
                                    { Project = binding.ProjectId; Ledger = binding.LedgerId; Epoch = binding.Epoch
                                      TargetAgent = agent.AgentId; Task = task.TaskId; Attempt = a.AttemptId
                                      Generation = a.AssignmentGeneration; Role = role
                                      RoleVersion = document.Version; ConfigVersion = roles.AgentConfigVersion }
                                Ok(Witness(actual, snapshot.Commit, document.BlobOid))
                            | _ -> reject "missing, ambiguous, mismatched or stale role attachment")
                    | _ -> reject "missing, mismatched or stale task attempt"))
