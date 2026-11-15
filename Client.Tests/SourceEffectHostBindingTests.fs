module Client.Tests.SourceEffectHostBindingTests

open System
open System.Collections.Concurrent
open System.IO
open System.Threading
open System.Threading.Tasks
open Common
open NUnit.Framework
open Jarvis.WorkQueues
open Client.LedgerStore
open Client.SourceEffectJournal
open Client.SourceEffectRouteBridge
open Client.SourceEffectHostBinding
open Client.WorkQueueGitLedger

// The pure seam receives a canonical project for deterministic negative tests.
// Production MUST call prepareHostFileEdit, which itself loads the verified
// approved host binding and canonical Git-CAS snapshot, never caller input.
let private withHostJournal action =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-wq03-bound-" + Guid.NewGuid().ToString("N"))
    let host = Path.Combine(root, "host-journal")
    Directory.CreateDirectory(host) |> ignore
    try
        match openAtHostRoot host with
        | Error error -> failwith error
        | Ok store -> action root host store
    finally
        if Directory.Exists(root) then Directory.Delete(root, true)

let private denied<'T> (result: Result<'T,string>) : unit =
    match result with
    | Error message ->
        Assert.That(message.Contains("RecoveryBlocked", StringComparison.Ordinal), Is.True, message)
    | Ok _ -> failwith "Unbound source route must deny before dispatch"

let private fixture root =
    let projectId = Guid.NewGuid()
    let ledgerId = Guid.NewGuid()
    let launchId = Guid.NewGuid().ToString("D")
    let workId = "work-01"
    let generation = 4
    let checkout = sprintf "wq03-%s-%s-g%d" workId launchId generation
    let binding: LedgerBinding =
        { RepositoryPath = Path.Combine(root, checkout)
          RemoteName = "origin"
          ApprovedFetchUrl = "file:///approved/" + ledgerId.ToString("N")
          ApprovedPushUrl = "file:///approved/" + ledgerId.ToString("N")
          ProjectId = projectId; LedgerId = ledgerId; Epoch = 1 }
    let lineage: SourceLineage =
        { RepositoryId = WorkQueueGitLedger.sourceRepositoryId binding
          TaskId = "task-wq03"; TargetRef = "refs/heads/main"
          LineageId = "lineage-01"; PinnedBaseCommit = String.replicate 40 "a"
          ReachableRef = None; ContinuationSha = None; Generation = 1 }
    let work: WorkStage =
        { Id = workId; ProjectId = projectId.ToString("D")
          SubjectId = "task-wq03"; Kind = WorkKind.Implementation
          QueueId = "source"; StageGeneration = 1
          State = WorkState.Claimed launchId; Domain = DomainState.Undecided
          Gate = GateState.Clear; ClaimGeneration = generation
          Dependencies = Set.empty; WaivedDependencies = Map.empty
          AvailableAt = 1L; AvailableSequence = 1L
          SourceLineage = Some lineage; Push = None; IntegrationAuthorized = false }
    let launch: Launch =
        { Id = launchId; ProjectId = projectId.ToString("D")
          RoleId = "Worker"; RoleVersion = 1
          AccountTokenId = "token-1"; State = LaunchState.Registered("agent-uuid", "session")
          Lease = None; Version = 1; Credit = None
          PrimaryWorkId = Some workId; EverClaimed = true
          AncillaryMessages = Set.empty; DispatchCount = 1 }
    let project =
        { Contracts.emptyProject Map.empty with
            Works = Map.ofList [workId, work]
            Launches = Map.ofList [launchId, launch]
            Migration = MigrationEpoch.Frozen }
    let command =
        WriteFileCommand
            { ProjectName = "Fixture::" + checkout
              FilePath = "effect.txt"; Content = "one"
              FileWriteMode = FileWriteMode.Write
              ExpectedHash = None; CreateParents = Some false }
    binding, project, checkout, work, launch, command

let private bound binding project checkout command uuid store =
    bindCanonicalClaim binding project "Fixture" checkout command uuid store

let private run invocation command write =
    executeDiagnostic invocation command write
    |> fun operation -> operation.GetAwaiter().GetResult()

[<Test>]
let original_uuid_two_clients_journal_before_os_edit_and_refuse_replay () =
    withHostJournal (fun root host first ->
        let second =
            match openAtHostRoot host with
            | Ok store -> store
            | Error error -> failwith error
        let binding, project, checkout, _, _, cmd = fixture root
        let originalUuid = Guid.NewGuid()
        let firstBound = bound binding project checkout cmd originalUuid first |> function
            | Ok result -> result
            | Error error -> failwith error
        let secondBound = bound binding project checkout cmd originalUuid second |> function
            | Ok result -> result
            | Error error -> failwith error
        Assert.That(firstBound.Intent.OriginalOperationId, Is.EqualTo originalUuid)
        Assert.That(firstBound.TrustedScope.ProjectId, Is.EqualTo binding.ProjectId)
        Assert.That(firstBound.TrustedScope.LedgerId, Is.EqualTo binding.LedgerId)
        Assert.That(firstBound.TrustedScope.CheckoutId, Is.EqualTo checkout)
        let target = Path.Combine(root, "effect.txt")
        let firstResult = run firstBound cmd (fun () -> task {
            let report = inspectForHandoff first firstBound.TrustedScope [originalUuid]
            Assert.That(report.Records.Head.State, Is.EqualTo Prepared)
            Assert.That(report.RecoveryBlocked, Is.True)
            File.WriteAllText(target, "once")
            return (Ok () : Result<unit,string>)
        })
        Assert.That((firstResult = Ok(Ok ())), Is.True)
        denied (run secondBound cmd (fun () -> task {
            File.WriteAllText(target, "replayed")
            return (Ok() : Result<unit,string>)
        }))
        Assert.That(File.ReadAllText(target), Is.EqualTo "once")
        let report = inspectForHandoff second secondBound.TrustedScope [originalUuid]
        Assert.That(report.AllKnownOperationsTerminal, Is.True)
        Assert.That(report.RecoveryBlocked, Is.True)
        Assert.That(report.UninstrumentedRoutes, Does.Contain "LegacyOrOutOfBandOSGit"))

[<Test>]
let forged_scope_project_repository_launch_generation_checkout_and_route_are_rejected () =
    withHostJournal (fun root _ store ->
        let binding, project, checkout, work, launch, cmd = fixture root
        let uuid = Guid.NewGuid()
        let fail b p selected command = denied (bound b p selected command uuid store)
        fail {binding with ProjectId = Guid.NewGuid()} project checkout cmd
        fail {binding with ApprovedPushUrl = binding.ApprovedPushUrl + "-fork"} project checkout cmd
        fail binding {project with Works = project.Works.Add(work.Id, {work with ClaimGeneration = work.ClaimGeneration + 1})} checkout cmd
        fail binding {project with Works = project.Works.Add(work.Id, {work with Gate = GateState.SourceRecoveryBlocked "unknown effects"})} checkout cmd
        fail binding {project with Launches = project.Launches.Add(launch.Id, {launch with State = LaunchState.ReservedUnregistered})} checkout cmd
        fail binding {project with Launches = project.Launches.Add(launch.Id, {launch with PrimaryWorkId = None})} checkout cmd
        fail binding {project with Migration = MigrationEpoch.Activated} checkout cmd
        fail binding project (checkout + "-forged") cmd
        let wrongProject =
            match cmd with
            | WriteFileCommand c -> WriteFileCommand {c with ProjectName = "Other::" + checkout}
            | _ -> failwith "fixture"
        fail binding project checkout wrongProject
        let unsupported =
            RunCommandCommand
                { ProjectName = "Fixture::" + checkout; Executable = "git"
                  Args = ["push"]; Reason = None; WorkingDirectory = None
                  TimeoutSeconds = Some 1; MaxOutputBytes = Some 2048 }
        fail binding project checkout unsupported
        denied (bound binding project checkout cmd Guid.Empty store))

[<Test>]
let two_clients_same_original_uuid_race_allows_single_file_edit () =
    withHostJournal (fun root host first ->
        let second =
            match openAtHostRoot host with
            | Ok store -> store
            | Error error -> failwith error
        let binding, project, checkout, _, _, cmd = fixture root
        let uuid = Guid.NewGuid()
        let invocations =
            [| bound binding project checkout cmd uuid first
               bound binding project checkout cmd uuid second |]
            |> Array.map (function Ok invocation -> invocation | Error reason -> failwith reason)
        let edited = ConcurrentQueue<int>()
        let results = Array.zeroCreate<Result<Result<int,string>,string>> 2
        Parallel.For(0,2,fun index ->
            results.[index] <- run invocations.[index] cmd (fun () -> task {
                edited.Enqueue(index)
                return (Ok 1 : Result<int,string>)
            })) |> ignore
        Assert.That(edited.Count, Is.EqualTo 1)
        Assert.That(results |> Array.filter Result.isOk |> Array.length, Is.EqualTo 1)
        Assert.That(results |> Array.filter Result.isError |> Array.length, Is.EqualTo 1)
        Assert.That((inspectForHandoff second invocations.[0].TrustedScope [uuid]).RecoveryBlocked, Is.True))

[<Test>]
let same_original_uuid_cannot_be_rebound_to_other_ledger_or_request () =
    withHostJournal (fun root _ journal ->
        let binding, project, checkout, _, _, command = fixture root
        let original = Guid.NewGuid()
        let initial = bound binding project checkout command original journal |> function
            | Ok value -> value | Error reason -> failwith reason
        let started = run initial command (fun () -> task { return (Ok 1 : Result<int,string>) })
        Assert.That((started = (Ok(Ok 1) : Result<Result<int,string>,string>)), Is.True)
        let forgedLedger =
            bound {binding with LedgerId = Guid.NewGuid()} project checkout command original journal
            |> function Ok value -> value | Error reason -> failwith reason
        denied (run forgedLedger command (fun () -> task { return (Ok 2 : Result<int,string>) }))
        let changedRequest =
            match command with
            | WriteFileCommand c -> WriteFileCommand {c with Content = "different"}
            | _ -> failwith "fixture"
        let changedEnvelope =
            bound binding project checkout changedRequest original journal
            |> function Ok value -> value | Error reason -> failwith reason
        denied (run changedEnvelope changedRequest (fun () -> task { return (Ok 3 : Result<int,string>) }))
        let preflight = inspectForHandoff journal initial.TrustedScope [original]
        Assert.That(preflight.RecoveryBlocked, Is.True))
