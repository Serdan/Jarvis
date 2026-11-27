module AssignmentGenerationPropertiesW14

open System
open System.IO
open System.Diagnostics
open System.Text
open System.Text.Json
open System.Security.Cryptography
open System.Collections.Generic
open NUnit.Framework
open Client.LedgerStore
open Client.ReviewWorkflow

let private good = function Ok x -> x | Error e -> failwithf "Unexpected ledger error: %A" e
let private equal context expected actual =
    if expected <> actual then failwithf "%s: expected %A, actual %A" context expected actual

// Local-only repositories, with no inherited Git configuration, credentials or hooks.
let private git directory args =
    use p = new Process()
    p.StartInfo.FileName <- "git"
    p.StartInfo.WorkingDirectory <- directory
    p.StartInfo.RedirectStandardOutput <- true
    p.StartInfo.RedirectStandardError <- true
    for a in args do p.StartInfo.ArgumentList.Add a
    if not (p.Start()) then failwith "Git failed to start"
    let output = p.StandardOutput.ReadToEndAsync()
    let error = p.StandardError.ReadToEndAsync()
    if not (p.WaitForExit(30000)) then
        p.Kill(true)
        failwith "Fixture Git timed out"
    if p.ExitCode <> 0 then failwithf "Git %A: %s" args error.Result
    output.Result.Trim()

let private fixture action =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-w14-generation-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(root) |> ignore
    let gitEnvironment () =
        Environment.GetEnvironmentVariables()
        |> Seq.cast<System.Collections.DictionaryEntry>
        |> Seq.choose (fun e ->
            let name = string e.Key
            if name.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase) then Some(name, string e.Value)
            else None)
        |> Seq.toList
    let inherited = gitEnvironment ()
    let xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
    try
        for name, _ in inherited do Environment.SetEnvironmentVariable(name, null)
        for name, value in
            [ "GIT_CONFIG_GLOBAL", Path.Combine(root, "gitconfig")
              "GIT_CONFIG_NOSYSTEM", "1"
              "GIT_AUTHOR_NAME", "Fixture"
              "GIT_AUTHOR_EMAIL", "fixture@example.test"
              "GIT_COMMITTER_NAME", "Fixture"
              "GIT_COMMITTER_EMAIL", "fixture@example.test"
              "XDG_CONFIG_HOME", Path.Combine(root, "config") ] do
            Environment.SetEnvironmentVariable(name, value)
        let remote = Path.Combine(root, "remote.git")
        let one = Path.Combine(root, "one")
        let two = Path.Combine(root, "two")
        git root ["init"; "--bare"; "--object-format=sha1"; "--initial-branch=main"; remote] |> ignore
        git root ["clone"; remote; one] |> ignore
        File.WriteAllText(Path.Combine(one, "source.txt"), "fixture")
        git one ["add"; "source.txt"] |> ignore
        git one ["commit"; "-m"; "fixture"] |> ignore
        git one ["push"; "origin"; "HEAD:main"] |> ignore
        git root ["clone"; remote; two] |> ignore
        let projectId, ledgerId = Guid.NewGuid(), Guid.NewGuid()
        let binding path =
            { RepositoryPath = path; RemoteName = "origin"; ApprovedFetchUrl = remote
              ApprovedPushUrl = remote; ProjectId = projectId; LedgerId = ledgerId; Epoch = 1 }
        let bindings = [|binding one; binding two|]
        LedgerStore.initialize bindings[0] true |> good |> ignore
        action bindings
    finally
        for name, _ in gitEnvironment () do Environment.SetEnvironmentVariable(name, null)
        for name, value in inherited do Environment.SetEnvironmentVariable(name, value)
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", xdg)
        Directory.Delete(root, true)

// Oracle: a generation is a value captured at issuance, not a pointer to the
// editable canonical specification. History grows; an acknowledgement attaches
// once to a generation. The execution slot is independent of task specification.
type private Slot = Unstarted | Running | Yielded

type private Issue =
    { Html: string; Sequence: int64; Reason: string
      Instructions: string option; Ack: int64 option }

type private Checkpoint =
    { Sequence: int64; Generation: int; Kind: string; Summary: string; Next: string }

type private Lane =
    { Task: string; Owner: string; Attempt: string
      Canonical: string; TaskVersion: int; SpecVersion: int
      AttemptVersion: int; AgentVersion: int; Slot: Slot
      History: Issue list; Checkpoints: Checkpoint list }

type private Step = { Id: int; Lane: int; Verb: string; Arg: int }

let private generation m = List.length m.History
let private current m = List.last m.History
let private newHtml (s: Step) = $"<h1>spec lane {s.Lane} step {s.Id}</h1>"
let private instructions (s: Step) = $"instructions step {s.Id}"
let private reason (s: Step) = $"reason step {s.Id}"
let private next (s: Step) = $"continue step {s.Id}"
let private bump m =
    { m with AttemptVersion = m.AttemptVersion + 1; AgentVersion = m.AgentVersion + 1 }
let private issue html why instruction seq m =
    { (bump m) with Slot = Unstarted
                    History = m.History @ [{ Html = html; Sequence = seq; Reason = why
                                             Instructions = Some instruction; Ack = None }] }
let private checkpoint s seq kind continuation m =
    { Sequence = seq; Generation = generation m; Kind = kind; Summary = reason s; Next = continuation }

type private Prediction = Change of Lane * string | Deny of string

let private predict (s: Step) seq m =
    match s.Verb with
    | "reissue-new" | "reissue-same" | "reissue-none" ->
        if m.Slot <> Unstarted then Deny "phase"
        elif m.Canonical <> (current m).Html then Deny "unadopted"
        else
            let html = if s.Verb = "reissue-new" then newHtml s else m.Canonical
            let issued = issue html "ExplicitReissue" (instructions s) seq m
            if html = m.Canonical then Change(issued, "issue-unchanged")
            else
                Change({ issued with Canonical = html; TaskVersion = m.TaskVersion + 1
                                     SpecVersion = m.SpecVersion + 1 }, "issue-new-spec")
    | "revise" ->
        Change({ m with Canonical = newHtml s; TaskVersion = m.TaskVersion + 1
                        SpecVersion = m.SpecVersion + 1 }, "revision-without-adoption")
    | "adopt" ->
        if m.Slot = Unstarted && m.Canonical <> (current m).Html then
            Change(issue m.Canonical "SpecAdoption" (instructions s) seq m, "adopt")
        else Deny "phase-or-no-new-spec"
    | "resume" ->
        match m.Slot with
        | Unstarted ->
            let latest = { (current m) with Ack = Some seq }
            let history = m.History |> List.rev |> List.tail |> List.rev
            Change({ (bump m) with Slot = Running; History = history @ [latest] }, "first-resume")
        | Yielded -> Change({ (bump m) with Slot = Running }, "resume-after-yield")
        | Running -> Change({ m with AgentVersion = m.AgentVersion + 1 }, "working-resume")
    | "yield" ->
        if m.Slot <> Running then Deny "phase"
        else
            Change({ (bump m) with Slot = Yielded
                                   Checkpoints = m.Checkpoints @ [checkpoint s seq "Yield" (next s) m] }, "yield")
    | "recover" ->
        let recovered = issue (current m).Html "Recovery" (reason s) seq m
        let tag =
            match m.Slot with
            | Running -> "recover-working"
            | Yielded -> "recover-yielded"
            | Unstarted -> "recover-unstarted"
        Change({ recovered with Checkpoints = m.Checkpoints @ [checkpoint s seq "Recovery" "Restart the same assignment" m] }, tag)
    | "stale-generation" -> Deny "generation"
    | "stale-version" -> Deny "attempt-version"
    | "stale-agent" -> Deny "agent-version"
    | "wrong-owner" -> Deny "owner"
    | _ -> failwithf "Unknown model command %s" s.Verb

let private blobOid (html: string) =
    let bytes = Encoding.UTF8.GetBytes(html)
    let header = Encoding.UTF8.GetBytes($"blob {bytes.Length}\u0000")
    Convert.ToHexString(SHA1.HashData(Array.append header bytes)).ToLowerInvariant()

let private uuid seed step =
    let hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}/{step}"))
    Guid(hash[0..15])

// Adapter uses only model identities and expected versions, never a successful
// implementation read to decide what operation can be issued next.
let private request seed (s: Step) m other : LedgerBinding -> Result<LedgerWrite, LedgerFailure> =
    let id = uuid seed s.Id
    let g, v, a = generation m, m.AttemptVersion, m.AgentVersion
    fun b ->
        match s.Verb with
        | "reissue-new" | "reissue-same" | "reissue-none" ->
            let html =
                match s.Verb with
                | "reissue-new" -> newHtml s
                | "reissue-same" -> m.Canonical
                | _ -> ""
            ReviewWorkflow.reissueAssignment b id m.Task m.Owner v a g (instructions s) html
        | "revise" ->
            ReviewWorkflow.reviseTaskSpecPinned b id m.Task m.TaskVersion (blobOid m.Canonical) m.Task (newHtml s)
        | "adopt" ->
            ReviewWorkflow.adoptTaskSpec b id m.Task m.Owner m.TaskVersion v a g (blobOid m.Canonical) (instructions s)
        | "resume" ->
            ReviewWorkflow.resumeAgent b id m.Task m.Owner v a g
        | "yield" ->
            ReviewWorkflow.yieldAgent b id m.Task m.Owner v a g (reason s) (next s)
        | "recover" ->
            ReviewWorkflow.recoverAgentAssignment b id m.Task m.Owner v a g (reason s)
        | "stale-generation" ->
            // Current versions deliberately accompany an old (or not-yet-issued)
            // generation, so a version guard alone cannot reject the request.
            let wrong = if g > 1 then g - 1 else g + 1
            ReviewWorkflow.resumeAgent b id m.Task m.Owner v a wrong
        | "stale-version" ->
            ReviewWorkflow.resumeAgent b id m.Task m.Owner (v + 1) a g
        | "stale-agent" ->
            ReviewWorkflow.resumeAgent b id m.Task m.Owner v (a + 1) g
        | "wrong-owner" ->
            ReviewWorkflow.resumeAgent b id m.Task other.Owner v other.AgentVersion g
        | _ -> failwithf "Unknown request command %s" s.Verb

let private options = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)
let private decode<'a> (s: string) : 'a =
    try JsonSerializer.Deserialize<'a>(s, options)
    with ex -> failwithf "Cannot decode %s from %s: %s" typeof<'a>.Name s ex.Message
let private taskPath m = $"tasks/{m.Task}/task.json"
let private specPath m = $"tasks/{m.Task}/spec.html"
let private attemptPath m = $"tasks/{m.Task}/attempts/{m.Attempt}.json"
let private assignmentPrefix m = $"tasks/{m.Task}/attempts/{m.Attempt}/assignments/"
let private issuePath m g = $"{assignmentPrefix m}{g}.json"
let private ackPath m g = $"{assignmentPrefix m}{g}.ack.json"
let private checkpointPrefix m = $"tasks/{m.Task}/attempts/{m.Attempt}/checkpoints/"
let private agentPath m =
    let key = Convert.ToBase64String(Encoding.UTF8.GetBytes(m.Owner)).TrimEnd('=').Replace('+','-').Replace('/','_')
    $"agents/{key}.json"

// Observe the underlying immutable Git blobs, not just the same accessor used
// by the mutation. Field expectations come from the independent model above.
let private observe b snapshot m =
    let mutable records : Map<string, string option> = Map.empty
    let read path =
        let content, oid = LedgerStore.readAt b snapshot path |> good
        records <- Map.add path oid records
        content
    let required path = read path |> Option.defaultWith (fun () -> failwithf "Missing %s" path)
    let check name = equal $"{m.Task}/{name}"
    let taskRaw = required (taskPath m)
    let task = decode<ReviewTask> taskRaw
    if isNull task.TaskId then failwithf "Task JSON decoded without identity: %s" taskRaw
    check "task identity" m.Task task.TaskId
    check "task title" m.Task task.Title
    check "task attempt" m.Attempt task.AttemptId
    check "lifecycle" "InProgress" task.Lifecycle
    check "task version" m.TaskVersion task.Version
    check "spec version" m.SpecVersion task.SpecVersion
    check "canonical html" m.Canonical (required (specPath m))
    check "canonical blob" (Some(blobOid m.Canonical)) records[specPath m]
    let attempt = required (attemptPath m) |> decode<ReviewAttempt>
    check "attempt task" m.Task attempt.TaskId
    check "attempt identity" m.Attempt attempt.AttemptId
    check "attempt owner" m.Owner attempt.OwnerAgentId
    check "attempt version" m.AttemptVersion attempt.Version
    check "attempt phase" (if m.Slot = Unstarted then "Queued" else "Started") attempt.Phase
    check "generation" (generation m) attempt.AssignmentGeneration
    check "pinned spec" (blobOid (current m).Html) attempt.SpecBlobOid
    check "issued sequence" (current m).Sequence attempt.IssuedSequence
    let agent = required (agentPath m) |> decode<ReviewAgent>
    check "agent identity" m.Owner agent.AgentId
    check "agent version" m.AgentVersion agent.Version
    check "slot execution" (if m.Slot = Running then "Working" else "Stopped") agent.Execution
    check "slot task" m.Task agent.CurrentAssignment.TaskId
    check "slot attempt" m.Attempt agent.CurrentAssignment.AttemptId
    check "unassigned disposition" null agent.UnassignedDisposition
    m.History |> List.iteri (fun i expected ->
        let g = i + 1
        let issue = required (issuePath m g) |> decode<AssignmentIssue>
        check $"issue {g} identity" (m.Task,m.Attempt,m.Owner,g) (issue.TaskId,issue.AttemptId,issue.OwnerAgentId,issue.Generation)
        check $"issue {g} previous" (g - 1) issue.PreviousGeneration
        check $"issue {g} reason" expected.Reason issue.Reason
        check $"issue {g} sequence" expected.Sequence issue.IssuedSequence
        check $"issue {g} spec" (blobOid expected.Html) issue.SpecBlobOid
        expected.Instructions |> Option.iter (fun value -> check $"issue {g} instructions" value issue.Instructions)
        match expected.Ack, read (ackPath m g) with
        | None, None -> ()
        | Some seq, Some raw ->
            let ack = decode<AssignmentReceipt> raw
            check $"ack {g} identity" (m.Task,m.Attempt,m.Owner,g) (ack.TaskId,ack.AttemptId,ack.OwnerAgentId,ack.Generation)
            check $"ack {g} spec" (blobOid expected.Html) ack.SpecBlobOid
            check $"ack {g} issue sequence" expected.Sequence ack.IssueSequence
            check $"ack {g} sequence" seq ack.AcknowledgedSequence
        | other -> failwithf "Generation %d acknowledgment presence differs: %A" g other)
    check "future issue absent" None (read (issuePath m (generation m + 1)))
    check "future receipt absent" None (read (ackPath m (generation m + 1)))
    for expected in m.Checkpoints do
        let cp = required ($"{checkpointPrefix m}{expected.Sequence}.json") |> decode<AgentCheckpoint>
        check "checkpoint identity" (m.Task,m.Attempt,m.Owner,expected.Generation) (cp.TaskId,cp.AttemptId,cp.OwnerAgentId,cp.AssignmentGeneration)
        check "checkpoint sequence" expected.Sequence cp.Sequence
        check "checkpoint kind" expected.Kind cp.Kind
        check "checkpoint summary" expected.Summary cp.Summary
        check "checkpoint continuation" expected.Next cp.NextAction
    records

let private verifyPreserved targeted (before: Lane array) (after: Lane array)
                            (oldRecords: Map<string, string option> array) (newRecords: Map<string, string option> array) =
    for i in 0 .. before.Length - 1 do
        let old, updated = before[i], after[i]
        for KeyValue(path, oid) in oldRecords[i] do
            let immutable =
                oid.IsSome && (path.StartsWith(assignmentPrefix old, StringComparison.Ordinal)
                               || path.StartsWith(checkpointPrefix old, StringComparison.Ordinal))
            let unchanged =
                (path = taskPath old && old.TaskVersion = updated.TaskVersion)
                || (path = specPath old && old.Canonical = updated.Canonical)
                || (path = attemptPath old && old.AttemptVersion = updated.AttemptVersion)
                || (path = agentPath old && old.AgentVersion = updated.AgentVersion)
            if i <> targeted || immutable || unchanged then
                equal $"preserved blob {path}" (Some oid) (Map.tryFind path newRecords[i])

let private createLane b lane =
    let task, owner = $"task-{lane}", $"Worker {lane}"
    let attempt = Guid.NewGuid()
    let html = $"<h1>initial {lane}</h1>"
    ReviewWorkflow.registerAgent b (Guid.NewGuid()) owner |> good |> ignore
    ReviewWorkflow.createTaskWithSpec b (Guid.NewGuid()) task task html |> good |> ignore
    let assigned = ReviewWorkflow.assignTask b (Guid.NewGuid()) task attempt owner |> good
    { Task = task; Owner = owner; Attempt = attempt.ToString("N"); Canonical = html
      TaskVersion = 2; SpecVersion = 1; AttemptVersion = 1; AgentVersion = 2; Slot = Unstarted
      History = [{ Html = html; Sequence = assigned.Operation.Sequence; Reason = "Assignment"
                   Instructions = None; Ack = None }]
      Checkpoints = [] }

type private Saved =
    { Invoke: LedgerBinding -> Result<LedgerWrite, LedgerFailure>; Original: LedgerOperation }

let private format s = $"{s.Id},{s.Lane},{s.Verb},{s.Arg}"
let private parse (text: string) =
    text.Split(';', StringSplitOptions.RemoveEmptyEntries)
    |> Array.map (fun line ->
        match line.Split(',') with
        | [|id; lane; verb; arg|] -> { Id = int id; Lane = int lane; Verb = verb; Arg = int arg }
        | _ -> failwithf "Malformed trace step %s" line)

let private generate seed =
    // Stable logical step IDs permit prefix replay and deletion-based reduction
    // without renumbering operation references. Prefix establishes useful states.
    let prefix =
        [| 0,"reissue-new",0; 1,"reissue-same",0; 0,"stale-generation",0
           0,"revise",0; 0,"reissue-none",0; 1,"resume",0; 0,"adopt",0
           0,"replay",1; 1,"yield",0; 0,"resume",0; 0,"recover",0
           0,"stale-generation",0; 1,"resume",0; 0,"resume",0; 0,"collision",1
           1,"reissue-new",0; 0,"yield",0; 0,"recover",0; 0,"reissue-none",0
           0,"stale-version",0; 0,"resume",0; 1,"replay",6; 0,"wrong-owner",0
           0,"stale-agent",0; 1,"revise",0; 1,"recover",0; 1,"adopt",0; 1,"resume",0 |]
    let random = Random(seed)
    let verbs =
        if seed % 2 = 0 then
            [|"revise"; "adopt"; "reissue-new"; "reissue-same"; "reissue-none"; "recover"; "resume"; "yield"; "replay"|]
        else
            [|"resume"; "yield"; "recover"; "stale-generation"; "stale-version"; "stale-agent"; "wrong-owner"; "collision"; "replay"|]
    let suffix =
        Array.init 12 (fun _ ->
            let lane = random.Next(2)
            let verb = verbs[random.Next(verbs.Length)]
            let arg = if verb = "replay" || verb = "collision" then [|1;2;4;6;7;9;10;11;13;14|][random.Next(10)] else 0
            lane,verb,arg)
    Array.append prefix suffix
    |> Array.mapi (fun i (lane,verb,arg) -> { Id = i + 1; Lane = lane; Verb = verb; Arg = arg })

let private run seed (trace: Step array) requireCoverage =
    fixture (fun bindings ->
        let mutable models = Array.init 2 (createLane bindings[0])
        let mutable snapshot = LedgerStore.fetch bindings[1] |> good
        let mutable observed = models |> Array.map (observe bindings[1] snapshot)
        let saved = Dictionary<int, Saved>()
        let outcomes = Dictionary<string, int>()
        let seen = HashSet<int>()
        let record tag =
            let n = match outcomes.TryGetValue tag with true,n -> n | _ -> 0
            outcomes[tag] <- n + 1
        let report () =
            outcomes |> Seq.map (fun p -> $"{p.Key}={p.Value}") |> Seq.sort |> String.concat "; "
        for index in 0 .. trace.Length - 1 do
            let s = trace[index]
            if not (seen.Add s.Id) || s.Lane < 0 || s.Lane >= models.Length then
                failwithf "Invalid or duplicated logical step %A" s
            let writer, reader = bindings[s.Id % 2], bindings[1 - s.Id % 2]
            let before = Array.copy models
            let m, other = before[s.Lane], before[1 - s.Lane]
            let head = snapshot.Commit
            let sequence = snapshot.Header.NextSequence
            let mutable changed = false
            try
                match s.Verb with
                | "replay" ->
                    let original = saved[s.Arg]
                    let result = original.Invoke writer |> good
                    equal "exact replay marker" true result.AlreadyApplied
                    equal "exact historical operation receipt" original.Original result.Operation
                    record "exact-replay"
                | "collision" ->
                    let original = saved[s.Arg]
                    let id = Guid.Parse(original.Original.OperationId)
                    match ReviewWorkflow.recoverAgentAssignment writer id m.Task m.Owner m.AttemptVersion m.AgentVersion (generation m) ("collision " + reason s) with
                    | Error OperationIdReused -> record "uuid-collision"
                    | actual -> failwithf "Expected UUID collision, actual %A" actual
                | _ ->
                    let invoke = request seed s m other
                    match predict s sequence m, invoke writer with
                    | Change(updated,tag), Ok result ->
                        equal "new operation marker" false result.AlreadyApplied
                        equal "new operation sequence" sequence result.Operation.Sequence
                        models[s.Lane] <- updated
                        saved.Add(s.Id, { Invoke = invoke; Original = result.Operation })
                        changed <- true
                        record tag
                    | Deny "unadopted", Error(LedgerIntegrityError _) -> record "denied-unadopted"
                    | Deny tag, Error(StaleEntityVersion _) -> record ("denied-" + tag)
                    | expected, actual -> failwithf "Prediction %A, actual result %A" expected actual
                snapshot <- LedgerStore.fetch reader |> good
                if changed then
                    equal "one durable sequence consumed" (sequence + 1L) snapshot.Header.NextSequence
                    if snapshot.Commit = head then failwith "Accepted operation did not advance head"
                else
                    equal "rejected or replayed operation head" head snapshot.Commit
                    equal "rejected or replayed operation sequence" sequence snapshot.Header.NextSequence
                let actual = models |> Array.map (observe reader snapshot)
                verifyPreserved (if changed then s.Lane else -1) before models observed actual
                observed <- actual
            with ex ->
                let prefix = trace[0..index] |> Array.map format |> String.concat ";"
                failwithf "seed=%d first divergence step=%s\nreplay-prefix=%s\nrealized-before-failure=%s\n%s" seed (format s) prefix (report()) (ex.ToString())
        let traceText = trace |> Array.map format |> String.concat ";"
        TestContext.Progress.WriteLine($"seed={seed}; trace={traceText}; realized={report()}")
        if requireCoverage then
            for tag in
                ["issue-new-spec";"issue-unchanged";"revision-without-adoption";"adopt";"first-resume"
                 "resume-after-yield";"yield";"recover-working";"recover-yielded";"exact-replay"
                 "uuid-collision";"denied-generation";"denied-unadopted";"denied-phase"
                 "denied-attempt-version";"denied-agent-version";"denied-owner"] do
                if not (outcomes.ContainsKey tag) then failwithf "Generated trace failed to realize required behavior %s" tag)

[<TestCase(1401); TestCase(1402); NonParallelizable>]
let GeneratedAssignmentHistories seed =
    let replay = Environment.GetEnvironmentVariable("JARVIS_W14_TRACE")
    if String.IsNullOrWhiteSpace replay then run seed (generate seed) true
    else run seed (parse replay) false
