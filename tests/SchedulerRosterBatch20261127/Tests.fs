module SchedulerRosterBatchTests

open System
open System.IO
open System.Diagnostics
open System.Text
open NUnit.Framework
open Client.LedgerStore
open Client.ReviewWorkflow
open Client.RoleInstructions
open Client.ScheduledReadyResume
open Client.ChallengeWorkflow

let private good = function Ok value -> value | Error issue -> failwithf "%A" issue

let private git directory args =
    use proc = new Process()
    proc.StartInfo.FileName <- "git"
    proc.StartInfo.WorkingDirectory <- directory
    proc.StartInfo.RedirectStandardOutput <- true
    proc.StartInfo.RedirectStandardError <- true
    for arg in args do proc.StartInfo.ArgumentList.Add arg
    if not (proc.Start()) then failwith "Cannot start fixture git."
    let output = proc.StandardOutput.ReadToEnd()
    let error = proc.StandardError.ReadToEnd()
    proc.WaitForExit()
    if proc.ExitCode <> 0 then failwith error
    output.Trim()

let private fixtureIn root name =
    let bare = Path.Combine(root, name + ".git")
    let local = Path.Combine(root, name)
    git root ["init"; "--bare"; "--initial-branch=main"; bare] |> ignore
    git root ["clone"; bare; local] |> ignore
    File.WriteAllText(Path.Combine(local, "source.txt"), "fixture")
    git local ["add"; "source.txt"] |> ignore
    git local ["-c"; "user.name=Fixture"; "-c"; "user.email=fixture@example.test"; "commit"; "-m"; "fixture"] |> ignore
    git local ["push"; "-u"; "origin"; "HEAD"] |> ignore
    let binding =
        { RepositoryPath = local; RemoteName = "origin"; ApprovedFetchUrl = bare; ApprovedPushUrl = bare
          ProjectId = Guid.NewGuid(); LedgerId = Guid.NewGuid(); Epoch = 1 }
    LedgerStore.initialize binding true |> good |> ignore
    let assign agent task =
        ReviewWorkflow.registerAgent binding (Guid.NewGuid()) agent |> good |> ignore
        ReviewWorkflow.createTask binding (Guid.NewGuid()) task task |> good |> ignore
        ReviewWorkflow.assignTask binding (Guid.NewGuid()) task (Guid.NewGuid()) agent |> good |> ignore
    RoleInstructions.setLaunchTemplate binding "fixture" (Guid.NewGuid()) 0 "Agent {{agentId}}, project {{projectName}}. Call inbox."
    |> good |> ignore
    binding, assign

let private fixture action =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-ready-resume-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    try
        let binding, assign = fixtureIn root "local"
        action binding assign
    finally
        Directory.Delete(root, true)


let private snapshot binding = LedgerStore.fetch binding |> good
let private compare binding head limits =
    let expected = SerialRosterOracle.readRosterAt binding head limits
    let actual = readRosterAt binding head limits
    Assert.That(actual, Is.EqualTo(box expected))
    let entries = good actual
    Assert.That(candidates limits entries, Is.EqualTo(box (candidates limits (good expected))))
    entries

let private path (id: string) =
    "agents/" + Convert.ToBase64String(Encoding.UTF8.GetBytes id).TrimEnd('=').Replace('+','-').Replace('/','_') + ".json"

let private register binding id =
    ReviewWorkflow.registerAgent binding (Guid.NewGuid()) id |> good |> ignore

let private corruptCommit binding (head: LedgerSnapshot) mutate =
    let local = binding.RepositoryPath
    git local ["checkout"; "--detach"; head.Commit] |> ignore
    mutate local
    git local ["add"; "-A"] |> ignore
    git local ["-c"; "user.name=Fixture"; "-c"; "user.email=fixture@example.test"; "commit"; "-m"; "negative fixture"] |> ignore
    { head with Commit = git local ["rev-parse"; "HEAD"] }

let private put local relative (text: string) =
    let file = Path.Combine(local, relative)
    Directory.CreateDirectory(Path.GetDirectoryName file) |> ignore
    File.WriteAllText(file, text)

let private raw binding head relative = LedgerStore.readAt binding head relative |> good |> fst |> Option.get

[<Test>]
let ``fresh roster uses two runner starts for idle agents and preserves ordering`` () =
    fixture (fun binding _ ->
        for id in ["z"; "Z"; "a"; "Æ"; "a_"; "000"; "Agent +"; "Worker 12"; "α"; "🙂"] do
            register binding id
        let head = snapshot binding
        let expected, serialMetrics = LedgerStore.measureGitReads(fun () -> SerialRosterOracle.readRosterAt binding head disabled)
        let actual, batchMetrics = LedgerStore.measureGitReads(fun () -> readRosterAt binding head disabled)
        Assert.That(actual, Is.EqualTo(box expected))
        Assert.That(serialMetrics.ProcessStarts, Is.EqualTo(41))
        Assert.That(batchMetrics.ProcessStarts, Is.EqualTo(2))
        Assert.That(batchMetrics.RemoteFetches, Is.Zero)
        Assert.That(batchMetrics.BatchBlobReads, Is.EqualTo(1))
        printfn "idle agents=10 serial=%A batch=%A" serialMetrics batchMetrics
        let warm, warmMetrics = LedgerStore.measureGitReads(fun () -> readRosterAt binding head disabled)
        Assert.That(warm, Is.EqualTo(box expected))
        Assert.That(warmMetrics.ProcessStarts, Is.EqualTo(2)))

[<Test>]
let ``seeded mixed roster and role caps are snapshot equivalent across writes`` () =
    fixture (fun binding assign ->
        let rng = Random(7401)
        let mutable waiting = []
        let mutable ready = []
        for role in ["worker"; "reviewer"] do
            RoleInstructions.createRole binding "fixture" (Guid.NewGuid()) role role "test"
            |> good |> ignore
        register binding "Lead"
        for n in 0..7 do
            let id, task = $"Agent {n}", $"task-{n}"
            // Include known Ready, Working, stopped and blocked states; then
            // seeded choices vary the remainder and the role-cap projection.
            let state = if n < 4 then n else rng.Next(4)
            if state = 0 then register binding id
            else
                assign id task
                let role = if rng.Next(2) = 0 then "worker" else "reviewer"
                RoleInstructions.attachRole binding "fixture" (Guid.NewGuid()) id role 0 |> good |> ignore
                if state = 1 then ready <- id :: ready
                else
                    ReviewWorkflow.activateReadyAgent binding (Guid.NewGuid()) task id |> good |> ignore
                    if state = 3 then
                        let _, taskRecord, attempt = ReviewWorkflow.getTask binding task |> good
                        let agent = (ReviewWorkflow.getAgentInbox binding id |> good).Agent
                        let challenge =
                            ChallengeWorkflow.create binding (Guid.NewGuid()) id "Wait for gate" task
                                [ { OwnerKind = ""; OwnerAgentId = "Lead"; SuggestedAgentId = ""; Description = "Gate" } ]
                            |> good
                        ChallengeWorkflow.blockAttempt binding (Guid.NewGuid()) id task attempt.Value.AttemptId
                            taskRecord.Version attempt.Value.Version agent.Version attempt.Value.AssignmentGeneration
                            "Waiting" [challenge.ChallengeId] |> good |> ignore
                        waiting <- (id, challenge) :: waiting
        let first = snapshot binding
        let cases = [ disabled; { Total = Some 1; Roles = Map.empty }
                      { Total = Some 5; Roles = Map.ofList ["worker",1; "reviewer",2] }
                      { Total = None; Roles = Map.ofList ["worker",2] } ]
        let firstResults = cases |> List.map (compare binding first)
        let stopped = List.head ready
        ReviewWorkflow.queueResponseAlert binding (Guid.NewGuid()) "fixture" stopped "  sToP now" |> good |> ignore
        let released, challenge = List.head waiting
        ChallengeWorkflow.resolve binding (Guid.NewGuid()) "Lead" challenge.ChallengeId
            challenge.ConditionIds.Head 1 "Passed" "" |> good |> ignore
        let second = snapshot binding
        Assert.That(second.Commit, Is.Not.EqualTo(first.Commit))
        let secondResults = cases |> List.map (compare binding second)
        Assert.That(firstResults.Head |> List.exists (fun e -> e.AgentId = stopped && e.Ready.IsSome), Is.True)
        Assert.That(secondResults.Head |> List.exists (fun e -> e.AgentId = stopped && e.Ready.IsNone), Is.True)
        Assert.That(secondResults.Head |> List.exists (fun e -> e.AgentId = released && e.Ready.IsSome), Is.True)
        // Re-read the original immutable head after canonical mutations.
        Assert.That(cases |> List.map (compare binding first), Is.EqualTo(box firstResults))
        // Deleting an issued historical owner wait index must not make Ready
        // eligibility succeed even though the current roster blob is unchanged.
        let ownerKey = (path released).Substring(7).Replace(".json","")
        let prefix = "challenge-waits/by-owner/" + ownerKey
        let paths = git binding.RepositoryPath ["ls-tree"; "-r"; "--name-only"; second.Commit; "--"; prefix]
        let index = paths.Split('\n', StringSplitOptions.RemoveEmptyEntries) |> Array.head
        let missingWait = corruptCommit binding second (fun local -> File.Delete(Path.Combine(local,index)))
        let expected = SerialRosterOracle.readRosterAt binding missingWait disabled
        Assert.That(expected.IsError, Is.True)
        Assert.That(readRosterAt binding missingWait disabled, Is.EqualTo(box expected)))

[<Test>]
let ``agent path and canonical decoder reject malformed or duplicate evidence`` () =
    fixture (fun binding _ ->
        register binding "Agent"
        let head = snapshot binding
        let original = raw binding head (path "Agent")
        let check edit =
            let corrupt = corruptCommit binding head edit
            let old = SerialRosterOracle.readRosterAt binding corrupt disabled
            let next = readRosterAt binding corrupt disabled
            Assert.That(old.IsError, Is.True)
            Assert.That(next, Is.EqualTo(box old))
        check (fun local -> put local (path "Agent") "{")
        check (fun local -> put local (path "wrong") original)
        check (fun local -> put local (path "Agent") (original.Replace("\"agentId\":", "\"agentId\":\"Agent\",\"agentId\":")))
        check (fun local ->
            let longId = String('x',121)
            put local (path longId) (original.Replace("\"Agent\"", "\"" + longId + "\"")))
        // Unrelated non-roster paths and challenge subscriptions do not become rows.
        let ignored = corruptCommit binding head (fun local ->
            put local "agents/not.canonical.json" "{"
            put local ("agents/" + (path "Agent").Substring(7).Replace(".json",".challenge-subscription.json")) "{")
        Assert.That(compare binding ignored disabled, Is.EqualTo(box (compare binding head disabled))))

[<Test>]
let ``wrong object type fails closed and warm dashboard cache cannot mask missing roster blob`` () =
    fixture (fun binding _ ->
        register binding "Agent"
        let head = snapshot binding
        let relative = path "Agent"
        let original = raw binding head relative
        let wrongType = corruptCommit binding head (fun local ->
            let file = Path.Combine(local, relative)
            File.Delete file
            File.CreateSymbolicLink(file, "nonexistent-target") |> ignore)
        Assert.That((SerialRosterOracle.readRosterAt binding wrongType disabled).IsError, Is.True)
        Assert.That((readRosterAt binding wrongType disabled).IsError, Is.True)
        let missing = corruptCommit binding head (fun local ->
            put local relative (original.Replace("Registered without an assignment","unique missing-object fixture")))
        compare binding missing disabled |> ignore
        LedgerStore.readDashboardRecordsAt binding missing |> good |> ignore
        let oid = git binding.RepositoryPath ["rev-parse"; missing.Commit + ":" + relative]
        let objects = Path.Combine(binding.RepositoryPath, ".git", "objects")
        let loose = Path.Combine(objects, oid.Substring(0,2), oid.Substring(2))
        Assert.That(File.Exists loose, Is.True)
        File.Delete loose
        Assert.That((SerialRosterOracle.readRosterAt binding missing disabled).IsError, Is.True)
        Assert.That((readRosterAt binding missing disabled).IsError, Is.True))
