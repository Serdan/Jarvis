module Client.Tests.WQ04ProducerFixture

open System
open System.Text.Json
open System.Text.RegularExpressions
open FsToolkit.ErrorHandling
open Client.LedgerStore
open Client.WorkQueueGitLedger
open Client.WorkQueueRoleAuthority
open Client.WorkQueueReceiptProducer
open Client.WorkCompositionGitLedger
open Jarvis.WorkQueues

// Legacy disposable end-to-end fixtures predate original WQ04 role and graph
// records. Seed genuine project Git records BEFORE the ordinary producer CAS.
// No fake graph or caller assertion bypasses the production authority checks.
let private ok = function
    | Ok value -> value
    | Error error -> failwithf "Original WQ04 fixture setup: %A" error

let private spec title linked =
    { Title = title
      Goal = "Prove original WQ04 source, role and graph"
      Scope = "Disposable project Git ledger"
      AcceptanceCriteria = Set.singleton "Original receipt"
      Constraints = Set.empty
      LinkedTaskId = linked }

let private mutate binding mutation =
    let graph = WorkCompositionGitLedger.load binding |> ok |> snd
    WorkCompositionGitLedger.execute binding "WQ04 Fixture"
        (Guid.NewGuid()) graph.Version mutation |> ok |> ignore

let private seedRoleHistory binding =
    let snapshot = LedgerStore.fetch binding |> ok
    let existing, _ = LedgerStore.readAt binding snapshot WorkQueueRoleAuthority.CatalogPath |> ok
    if existing.IsNone then
        WorkQueueRoleAuthority.initializeCatalog binding "WQ04 Fixture" (Guid.NewGuid())
        |> ok |> ignore
    for roleId, queue in
        [ "WQ04FixturePlanner", "planning"
          "Reviewer", "review"
          "Integrator", "integration" ] do
        let view = WorkQueueRoleAuthority.load binding |> ok
        let covered =
            view.Catalog.Current
            |> Map.exists (fun id version ->
                view.Catalog.Versions.TryFind id
                |> Option.bind (fun versions -> versions.TryFind version)
                |> Option.exists (fun role ->
                    role.Enabled && role.QueueSubscriptions.Contains queue))
        if not covered then
            let definition: RoleConfiguration =
                { RoleId = roleId; Version = 1; Enabled = true
                  Instructions = "Authenticated disposable original WQ04 role"
                  QueueSubscriptions = Set.singleton queue }
            WorkQueueRoleAuthority.configure binding "WQ04 Fixture" (Guid.NewGuid())
                view.Catalog.Revision 0 definition |> ok |> ignore

let private linkTask binding task =
    let graph = WorkCompositionGitLedger.load binding |> ok |> snd
    if not (graph.Units |> Map.exists (fun _ unit -> unit.Spec.LinkedTaskId = Some task)) then
        let find kind =
            let current = WorkCompositionGitLedger.load binding |> ok |> snd
            current.Units |> Map.toSeq
            |> Seq.tryFind (fun (_, unit) -> unit.Kind = kind)
            |> Option.map fst
        let root =
            match find WorkUnitKind.Project with
            | Some id -> id
            | None ->
                let id = "wq04-fixture-project"
                mutate binding (WorkCompositionMutation.Create(id, WorkUnitKind.Project,
                    None, spec "Original project" None))
                id
        let system =
            match find WorkUnitKind.System with
            | Some id -> id
            | None ->
                let id = "wq04-fixture-system"
                mutate binding (WorkCompositionMutation.Create(id, WorkUnitKind.System,
                    Some root, spec "Original system" None))
                id
        mutate binding (WorkCompositionMutation.Create(
            "wq04-feature-" + Guid.NewGuid().ToString("N"),
            WorkUnitKind.Feature, Some system, spec task (Some task)))

let prepare binding originalOperationId =
    let snapshot = LedgerStore.fetch binding |> ok
    let operation =
        LedgerStore.getOperationAt binding snapshot originalOperationId |> ok
        |> Option.defaultWith (fun () -> failwith "Missing original WQ04 fixture operation")
    if not (List.contains operation.Type ["CreateTask"; "SubmitBranchReview"; "SubmitIntegration"]) then
        failwithf "Unsupported original fixture operation: %s" operation.Type
    let task =
        operation.Paths |> Array.choose (fun path ->
            let found = Regex.Match(path, @"^tasks/(?<task>[A-Za-z0-9_-]{1,80})/")
            if found.Success then Some found.Groups.["task"].Value else None)
        |> Array.distinct
    if task.Length <> 1 then failwith "Original WQ04 fixture operation has no unique task path"
    seedRoleHistory binding
    linkTask binding task.[0]

let produce binding actor operationId originalOperationId =
    prepare binding originalOperationId
    WorkQueueReceiptProducer.produce binding actor operationId originalOperationId

// An isolated integration-source fixture needs a real immutable SAME-CAS
// role-admission witness even though it does not own a live WQ07 account
// reservation. This does not grant live launch or producer authority.
let admitPinnedReviewFixture binding actor (operationId: Guid) (launch: Launch) reviewWorkId =
    let _, project = WorkQueueGitLedger.load binding |> ok
    let validate snapshot state = result {
        let! view = WorkQueueRoleAuthority.loadAt binding snapshot
        let! role =
            match view.Catalog.Versions.TryFind launch.RoleId with
            | Some revisions ->
                match revisions.TryFind launch.RoleVersion with
                | Some role when role.Enabled && role.QueueSubscriptions.Contains "review" -> Ok role
                | _ -> Error(InvalidBinding "Fixture role revision was not issued for review.")
            | None -> Error(InvalidBinding "Fixture review role missing.")
        let stored: StoredRole =
            { RoleId = role.RoleId; Version = role.Version
              Instructions = role.Instructions; Enabled = role.Enabled
              QueueSubscriptions = role.QueueSubscriptions |> Seq.sort |> Seq.toArray }
        let! roleOid = LedgerStore.computeBlobOid binding (JsonSerializer.Serialize stored)
        let witness: WorkQueueRoleAuthority.StoredRoleAdmission =
            { SchemaVersion = 1; ProjectId = launch.ProjectId; LaunchId = launch.Id
              AccountTokenId = launch.AccountTokenId
              RoleId = launch.RoleId; RoleVersion = launch.RoleVersion
              CatalogBlobOid = view.Proof.CatalogBlobOid
              RoleDefinitionOid = roleOid
              AdmissionOperationId = operationId.ToString("D") }
        return JsonSerializer.Serialize witness
    }
    WorkQueueGitLedger.executePinnedRoleAdmission binding actor operationId project.Version
        launch reviewWorkId validate |> ok |> ignore
