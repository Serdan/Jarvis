module Client.Tests.ChallengeStateMachinePropertyTests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open Client.LedgerStore
open Client.ReviewWorkflow
open Client.ChallengeWorkflow
open NUnit.Framework

// Deliberately independent domain model: no ledger records, workflow helpers,
// transition functions, or persisted index implementation are used as the oracle.
type private ConditionState = Waiting | Obstructed | Done
type private ReferenceChallenge =
    { TitleVariant: bool
      Conditions: ConditionState list
      Version: int
      Cancelled: bool }

type private WireCall =
    | NewChallenge of slot: int * variant: bool
    | Answer of slot: int * condition: int * actor: int * version: int * obstruct: bool * variant: bool
    | Reopen of slot: int * condition: int * actor: int * version: int * variant: bool
    | Cancel of slot: int * actor: int * version: int * variant: bool

type private Action =
    | Create of slot: int * variant: bool
    | Respond of slot: int * condition: int * actor: int * obstruct: bool * stale: bool
    | Unblock of slot: int * condition: int * actor: int * stale: bool
    | Withdraw of slot: int * actor: int * stale: bool
    | Repeat of originalCommand: int * mutatePayload: bool

type private Command = { Number: int; Action: Action }
type private Reference =
    { Challenges: Map<int, ReferenceChallenge>
      Receipts: Map<string, WireCall> }

type private Expected = Applied | Replayed | Rejected

module private ChallengeModel =
    let empty = { Challenges = Map.empty; Receipts = Map.empty }

    let actor = function
        | 0 -> "Agent 3"
        | 1 -> "Lead"
        | 2 -> "Agent 8"
        | _ -> "Intruder"

    let status (challenge: ReferenceChallenge) =
        if challenge.Cancelled then "Cancelled"
        elif challenge.Conditions |> List.forall ((=) Done) then "Satisfied"
        elif challenge.Conditions |> List.exists ((=) Obstructed) then "Blocked"
        else "Pending"

    let phase = function
        | Waiting -> "Pending"
        | Obstructed -> "Blocked"
        | Done -> "Satisfied"

    let terminal challenge =
        challenge.Cancelled || (challenge.Conditions |> List.forall ((=) Done))

    let operationKey commandId = function
        | NewChallenge(slot, _) -> sprintf "create:%d" slot
        | _ -> sprintf "command:%d" commandId

    let uuid seed (key: string) =
        let bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sprintf "%d:%s" seed key))
        Guid(bytes.AsSpan(0, 16))

    let challengeId seed slot =
        uuid seed (sprintf "create:%d" slot) |> fun id -> id.ToString("N")

    let currentVersion state slot stale =
        let observed =
            state.Challenges
            |> Map.tryFind slot
            |> Option.map (fun c -> c.Version)
            |> Option.defaultValue 1
        if stale then observed + 1 else observed

    let chooseCall state (command: Command) (previous: Map<int, WireCall>) =
        match command.Action with
        | Create(slot, variant) -> Some(NewChallenge(slot, variant), command.Number)
        | Respond(slot, condition, who, obstruct, stale) ->
            Some(Answer(slot, condition, who, currentVersion state slot stale, obstruct, false), command.Number)
        | Unblock(slot, condition, who, stale) ->
            Some(Reopen(slot, condition, who, currentVersion state slot stale, false), command.Number)
        | Withdraw(slot, who, stale) ->
            Some(Cancel(slot, who, currentVersion state slot stale, false), command.Number)
        | Repeat(original, changed) ->
            previous
            |> Map.tryFind original
            |> Option.map (fun originalCall ->
                let modified =
                    if not changed then originalCall
                    else
                        match originalCall with
                        | NewChallenge(slot, variant) -> NewChallenge(slot, not variant)
                        | Answer(slot, i, who, ver, blocked, variant) ->
                            Answer(slot, i, who, ver, blocked, not variant)
                        | Reopen(slot, i, who, ver, variant) ->
                            Reopen(slot, i, who, ver, not variant)
                        | Cancel(slot, who, ver, variant) ->
                            Cancel(slot, who, ver, not variant)
                modified, original)

    let apply state operationId call =
        match Map.tryFind operationId state.Receipts with
        | Some same when same = call -> Replayed, state
        | Some _ -> Rejected, state
        | None ->
            let update slot transform =
                match Map.tryFind slot state.Challenges with
                | None -> Rejected, state
                | Some challenge ->
                    match transform challenge with
                    | None -> Rejected, state
                    | Some next ->
                        Applied,
                        { state with
                            Challenges = Map.add slot next state.Challenges
                            Receipts = Map.add operationId call state.Receipts }
            match call with
            | NewChallenge(slot, variant) ->
                if state.Challenges.ContainsKey slot then Rejected, state
                else
                    Applied,
                    { state with
                        Challenges =
                            Map.add slot
                                { TitleVariant = variant
                                  Conditions = [ Waiting; Waiting ]
                                  Version = 1; Cancelled = false }
                                state.Challenges
                        Receipts = Map.add operationId call state.Receipts }
            | Answer(slot, i, who, version, blocked, _) ->
                update slot (fun challenge ->
                    if terminal challenge || challenge.Version <> version || i < 0 || i > 1
                       || actor who <> (if i = 0 then "Lead" else "Agent 8")
                       || challenge.Conditions.[i] <> Waiting then None
                    else
                        Some { challenge with
                                Conditions =
                                    challenge.Conditions
                                    |> List.mapi (fun j old ->
                                        if j = i then (if blocked then Obstructed else Done) else old)
                                Version = version + 1 })
            | Reopen(slot, i, who, version, _) ->
                update slot (fun challenge ->
                    if terminal challenge || challenge.Version <> version || who <> 0
                       || i < 0 || i > 1 || challenge.Conditions.[i] <> Obstructed then None
                    else
                        Some { challenge with
                                Conditions =
                                    challenge.Conditions
                                    |> List.mapi (fun j old -> if j = i then Waiting else old)
                                Version = version + 1 })
            | Cancel(slot, who, version, _) ->
                update slot (fun challenge ->
                    if terminal challenge || who <> 0 || challenge.Version <> version then None
                    else Some { challenge with Cancelled = true; Version = version + 1 })

    let generate seed count =
        let random = Random(seed)
        [ for number in 0 .. count - 1 do
            let slot = random.Next(3)
            let who = random.Next(4)
            let condition = random.Next(2)
            let stale = random.Next(5) = 0
            let action =
                if number = 0 then Create(0, false)
                elif number = 1 then Create(1, false)
                else
                    match random.Next(10) with
                    | 0 -> Create(slot, random.Next(4) = 0)
                    | 1 | 2 | 3 -> Respond(slot, condition, who, random.Next(3) = 0, stale)
                    | 4 | 5 -> Unblock(slot, condition, who, stale)
                    | 6 -> Withdraw(slot, who, stale)
                    | _ -> Repeat(random.Next(number), random.Next(3) = 0)
            yield { Number = number; Action = action } ]

    let exerciseReference seed commands =
        let mutable state = empty
        let mutable previous = Map.empty
        let mutable applied = 0
        let mutable rejected = 0
        let mutable replayed = 0
        for command in commands do
            match chooseCall state command previous with
            | None -> ()
            | Some(call, original) ->
                if not (match command.Action with Repeat _ -> true | _ -> false) then
                    previous <- Map.add command.Number call previous
                let expected, next = apply state (operationKey original call) call
                state <- next
                match expected with
                | Applied -> applied <- applied + 1
                | Rejected -> rejected <- rejected + 1
                | Replayed -> replayed <- replayed + 1
                for KeyValue(_, challenge) in state.Challenges do
                    if challenge.Version < 1 || challenge.Conditions.Length <> 2 then
                        failwithf "Invalid reference structure seed=%d step=%A" seed command
                    if status challenge = "Satisfied"
                       && not (challenge.Conditions |> List.forall ((=) Done)) then
                        failwithf "Nonconjunctive satisfaction seed=%d step=%A" seed command
                    if status challenge = "Cancelled" && not challenge.Cancelled then
                        failwithf "Cancelled without cancellation seed=%d step=%A" seed command
        applied, rejected, replayed

module private ChallengeFixture =
    let ok = function
        | Ok item -> item
        | Error error -> failwithf "Unexpected fixture failure: %A" error

    let git dir args =
        use proc = new Process()
        proc.StartInfo.FileName <- "git"
        proc.StartInfo.WorkingDirectory <- dir
        proc.StartInfo.RedirectStandardOutput <- true
        proc.StartInfo.RedirectStandardError <- true
        args |> List.iter proc.StartInfo.ArgumentList.Add
        if not (proc.Start()) then failwith "Cannot start fixture git."
        let stdout = proc.StandardOutput.ReadToEnd()
        let stderr = proc.StandardError.ReadToEnd()
        proc.WaitForExit()
        if proc.ExitCode <> 0 then failwithf "Git fixture error %s: %s" (String.concat " " args) stderr
        stdout.Trim()

    let sourceSha () =
        try
            let root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."))
            git root [ "rev-parse"; "HEAD" ]
        with _ -> "source-unavailable"

    let withTwoClones action =
        let root = Path.Combine(Path.GetTempPath(), "jarvis-challenge-model-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        try
            let bare = Path.Combine(root, "remote.git")
            let first = Path.Combine(root, "a")
            let second = Path.Combine(root, "b")
            git root [ "init"; "--bare"; bare ] |> ignore
            git root [ "clone"; bare; first ] |> ignore
            git first [ "config"; "user.name"; "Model fixture" ] |> ignore
            git first [ "config"; "user.email"; "model@example.test" ] |> ignore
            File.WriteAllText(Path.Combine(first, "baseline.txt"), "source")
            git first [ "add"; "baseline.txt" ] |> ignore
            git first [ "commit"; "-m"; "baseline" ] |> ignore
            git first [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
            git root [ "clone"; bare; second ] |> ignore
            git second [ "config"; "user.name"; "Model fixture" ] |> ignore
            git second [ "config"; "user.email"; "model@example.test" ] |> ignore
            let projectId = Guid.NewGuid()
            let ledgerId = Guid.NewGuid()
            let binding path : LedgerBinding =
                { RepositoryPath = path; RemoteName = "origin"
                  ApprovedFetchUrl = bare; ApprovedPushUrl = bare
                  ProjectId = projectId; LedgerId = ledgerId; Epoch = 1 }
            let a, b = binding first, binding second
            LedgerStore.initialize a true |> ok |> ignore
            for actor in [ "Agent 3"; "Lead"; "Agent 8"; "Intruder" ] do
                ReviewWorkflow.registerAgent a (Guid.NewGuid()) actor |> ok |> ignore
            action a b
        finally
            if Directory.Exists(root) then Directory.Delete(root, true)

    let definitions : ChallengeConditionSpec list =
        [ { OwnerKind = ""; OwnerAgentId = "Lead"; SuggestedAgentId = ""; Description = "Review" }
          { OwnerKind = ""; OwnerAgentId = "Agent 8"; SuggestedAgentId = ""; Description = "Bench" } ]

    let actual seed binding opId (conditionIds: Map<int, string list>) (call: WireCall) =
        let title slot changed =
            sprintf "gate-%d%s" slot (if changed then "-changed" else "")
        let challenge slot = ChallengeModel.challengeId seed slot
        let condition slot i =
            match Map.tryFind slot conditionIds with
            | Some ids when i >= 0 && i < ids.Length -> ids.[i]
            | _ -> ChallengeModel.uuid seed (sprintf "missing:%d:%d" slot i) |> fun g -> g.ToString("N")
        let result =
            match call with
            | NewChallenge(slot, variant) ->
                ChallengeWorkflow.create binding opId "Agent 3" (title slot variant) "" definitions
                |> Result.map (fun created -> created.Receipt.AlreadyApplied, Some(slot, created.ConditionIds))
            | Answer(slot, index, who, version, blocked, variant) ->
                let evidence = if variant then "evidence-modified" else "evidence"
                let reason = if blocked then "blocked" else ""
                ChallengeWorkflow.resolve binding opId (ChallengeModel.actor who)
                    (challenge slot) (condition slot index) version evidence reason
                |> Result.map (fun write -> write.AlreadyApplied, None)
            | Reopen(slot, index, who, version, variant) ->
                ChallengeWorkflow.reopen binding opId (ChallengeModel.actor who)
                    (challenge slot) (condition slot index) version
                    (if variant then "retry-modified" else "retry")
                |> Result.map (fun write -> write.AlreadyApplied, None)
            | Cancel(slot, who, version, variant) ->
                ChallengeWorkflow.cancel binding opId (ChallengeModel.actor who)
                    (challenge slot) version
                    (if variant then "withdrawn-modified" else "withdrawn")
                |> Result.map (fun write -> write.AlreadyApplied, None)
        result

    let checkActual seed binding (state: Reference) =
        for KeyValue(slot, expected) in state.Challenges do
            let id = ChallengeModel.challengeId seed slot
            let first = ChallengeWorkflow.getPage binding "Agent 3" id 16 0L "" |> ok
            let found = first.Challenge
            if found.Version <> expected.Version || found.Status <> ChallengeModel.status expected
               || found.Conditions |> List.map _.Status <> (expected.Conditions |> List.map ChallengeModel.phase) then
                failwithf "slot=%d expected=%A actual={version=%d;status=%s;conditions=%A}"
                    slot expected found.Version found.Status (found.Conditions |> List.map _.Status)
            let rec allEvents after snapshot acc =
                let page = ChallengeWorkflow.getPage binding "Agent 3" id 16 after snapshot |> ok
                let combined = acc @ page.History
                if page.NextEventSequence = 0L then combined
                else allEvents page.NextEventSequence snapshot combined
            let events =
                if first.NextEventSequence = 0L then first.History
                else allEvents first.NextEventSequence first.SnapshotCommit first.History
            let sequences = events |> List.map _.Sequence
            if events.Length <> expected.Version || sequences <> (sequences |> List.distinct |> List.sort) then
                failwithf "slot=%d expectedEvents=%d actualSequences=%A" slot expected.Version sequences

    let runTrace seed (commands: Command list) =
        withTwoClones (fun a b ->
            let mutable state = ChallengeModel.empty
            let mutable previous = Map.empty
            let mutable ids: Map<int, string list> = Map.empty
            for command in commands do
                match ChallengeModel.chooseCall state command previous with
                | None -> ()
                | Some(call, original) ->
                    if not (match command.Action with Repeat _ -> true | _ -> false) then
                        previous <- Map.add command.Number call previous
                    let opKey = ChallengeModel.operationKey original call
                    let expected, next = ChallengeModel.apply state opKey call
                    let binding = if command.Number % 2 = 0 then a else b
                    let opId = ChallengeModel.uuid seed opKey
                    let outcome = actual seed binding opId ids call
                    let observed =
                        match outcome with
                        | Ok(replay, newIds) ->
                            match newIds with
                            | Some(slot, conditions) -> ids <- Map.add slot conditions ids
                            | None -> ()
                            if replay then Replayed else Applied
                        | Error _ -> Rejected
                    if expected <> observed then
                        failwithf "seed=%d command=%A call=%A expected=%A observed=%A result=%A"
                            seed command call expected observed outcome
                    state <- next
                    checkActual seed (if command.Number % 2 = 0 then b else a) state
            let snapshot = LedgerStore.fetch a |> ok
            let allNotifications = LedgerStore.listAt a snapshot "notifications" |> ok
            if not allNotifications.IsEmpty then
                failwithf "seed=%d challenge workflow wrote notifications: %A" seed allNotifications)

    // Deletion shrinking preserves command IDs, UUIDs and replay payloads.
    // A missing referenced command becomes an inert step, not a rewritten call.
    let shrink seed (original: Command list) =
        let mutable best = original
        let mutable changed = true
        let mutable attempts = 0
        while changed && attempts < 64 do
            changed <- false
            let mutable index = 0
            while index < best.Length && not changed && attempts < 64 do
                let candidate = best |> List.mapi (fun i command -> i, command)
                                     |> List.choose (fun (i, command) -> if i = index then None else Some command)
                attempts <- attempts + 1
                let stillFails =
                    try runTrace seed candidate; false
                    with _ -> true
                if stillFails then
                    best <- candidate
                    changed <- true
                index <- index + 1
        best, attempts, (not changed)

[<TestFixture>]
type ChallengeStateMachinePropertyTests() =
    [<Test>]
    member _.SeededReferenceTracesMaintainConjunctionAndTerminality() =
        let results =
            [ for seed in 1 .. 256 ->
                ChallengeModel.generate seed 80
                |> ChallengeModel.exerciseReference seed ]
        Assert.That(results |> List.sumBy (fun (a, _, _) -> a), Is.GreaterThan(200))
        Assert.That(results |> List.sumBy (fun (_, r, _) -> r), Is.GreaterThan(200))
        Assert.That(results |> List.sumBy (fun (_, _, p) -> p), Is.GreaterThan(10))

    [<Test>]
    member _.TwoCloneStateMachineTracesMatchCanonicalChallengeAndAudit() =
        for seed in [ 0xC0FFEE; 0xBADA55; 0xA11CE ] do
            let commands = ChallengeModel.generate seed 22
            try
                ChallengeFixture.runTrace seed commands
            with originalFailure ->
                let minimal, reductions, deletionMinimal = ChallengeFixture.shrink seed commands
                Assert.Fail(
                    sprintf "Challenge model mismatch source=%s seed=%d initial=%A shrinkTrials=%d oneMinimal=%b minimal=%A failure=%s"
                        (ChallengeFixture.sourceSha()) seed commands reductions deletionMinimal minimal (originalFailure.ToString()))


module private ChallengeDependencyChecks =
    let assign binding task =
        ReviewWorkflow.createTask binding (Guid.NewGuid()) task task |> ChallengeFixture.ok |> ignore
        let attemptId = Guid.NewGuid().ToString("N")
        ReviewWorkflow.assignTask binding (Guid.NewGuid()) task (Guid.Parse attemptId) "Agent 3"
        |> ChallengeFixture.ok |> ignore
        ReviewWorkflow.beginAttempt binding (Guid.NewGuid()) task "Agent 3" 1 1
        |> ChallengeFixture.ok |> ignore
        let _, taskRecord, maybeAttempt = ReviewWorkflow.getTask binding task |> ChallengeFixture.ok
        let attempt = maybeAttempt.Value
        let inbox = ReviewWorkflow.getAgentInbox binding "Agent 3" |> ChallengeFixture.ok
        taskRecord, attempt, inbox.Agent, attemptId

    let gate binding task name owner =
        ChallengeWorkflow.create binding (Guid.NewGuid()) "Agent 3" name task
            [ { OwnerKind = ""; OwnerAgentId = owner
                SuggestedAgentId = ""; Description = "Release prerequisite" } ]
        |> ChallengeFixture.ok

    let verifyEdges binding task attempt generation dependencies =
        let snapshot = LedgerStore.fetch binding |> ChallengeFixture.ok
        let owner = ReviewWorkflow.ownerWaitIndexPrefix "Agent 3"
        let owned = LedgerStore.listAt binding snapshot owner |> ChallengeFixture.ok
        let expectedOwner =
            if List.isEmpty dependencies then []
            else [ ReviewWorkflow.waitIndexPath owner task attempt generation ]
        if Set.ofList owned <> Set.ofList expectedOwner then
            failwithf "Wrong by-owner reverse edges: expected=%A found=%A" expectedOwner owned
        for dependency in dependencies do
            let prefix = ReviewWorkflow.challengeWaitIndexPrefix dependency
            let linked = LedgerStore.listAt binding snapshot prefix |> ChallengeFixture.ok
            let expected = [ ReviewWorkflow.waitIndexPath prefix task attempt generation ]
            if Set.ofList linked <> Set.ofList expected then
                failwithf "Wrong by-challenge reverse edges for %s: %A" dependency linked
        let path = sprintf "tasks/%s/attempts/%s/challenge-wait.json" task attempt
        let body, _ = LedgerStore.readAt binding snapshot path |> ChallengeFixture.ok
        match body with
        | None -> failwithf "Missing canonical wait: %s" path
        | Some text ->
            use json = System.Text.Json.JsonDocument.Parse(text)
            let root = json.RootElement
            let deps =
                root.GetProperty("challengeIds").EnumerateArray()
                |> Seq.map (fun item -> item.GetString())
                |> Set.ofSeq
            if deps <> Set.ofList dependencies then
                failwithf "Wait dependencies differ from indices: %A vs %A" deps dependencies
        let notifications = LedgerStore.listAt binding snapshot "notifications" |> ChallengeFixture.ok
        if not notifications.IsEmpty then
            failwithf "Challenge dependency created notifications: %A" notifications

    let trace seed =
        ChallengeFixture.withTwoClones (fun a b ->
            let mode = seed % 4
            let task = sprintf "dependency-model-%d" seed
            let initialTask, initialAttempt, initialAgent, attemptId = assign a task
            let gates =
                if mode = 0 then []
                else [ gate a task "Gate A" "Lead"; gate b task "Gate B" "Agent 8" ]
            let dependencies = gates |> List.map _.ChallengeId
            if not dependencies.IsEmpty then
                let duplicate = [ dependencies.Head; dependencies.Head.ToUpperInvariant() ]
                match ChallengeWorkflow.blockAttempt a (Guid.NewGuid()) "Agent 3" task attemptId
                          initialTask.Version initialAttempt.Version initialAgent.Version 1
                          "duplicate UUID case" duplicate with
                | Error(InvalidBinding _) -> ()
                | result -> failwithf "seed=%d admitted duplicate normalized dependencies: %A" seed result
            match ChallengeWorkflow.blockAttempt b (Guid.NewGuid()) "Intruder" task attemptId
                      initialTask.Version initialAttempt.Version initialAgent.Version 1
                      "foreign owner" dependencies with
            | Error _ -> ()
            | result -> failwithf "seed=%d foreign actor blocked other's assignment: %A" seed result
            let blockedOp = Guid.NewGuid()
            ChallengeWorkflow.blockAttempt b blockedOp "Agent 3" task attemptId
                initialTask.Version initialAttempt.Version initialAgent.Version 1
                "Await all prerequisites" dependencies |> ChallengeFixture.ok |> ignore
            let afterBlock = ReviewWorkflow.getAgentInbox a "Agent 3" |> ChallengeFixture.ok
            if afterBlock.Agent.State <> "NeedsWork"
               || afterBlock.BlockedChallengeWaits.Length <> 1
               || afterBlock.BlockedChallengeWaits.Head.Status <> "Waiting" then
                failwithf "seed=%d did not enter canonical Waiting state" seed
            verifyEdges b task attemptId 1 dependencies
            if mode = 0 then
                // Empty dependency lists have no automatic wakeup.
                ChallengeWorkflow.requeueAttempt b (Guid.NewGuid()) "Agent 3" task attemptId
                    initialTask.Version afterBlock.Blocked.Head.Attempt.Version
                    afterBlock.Agent.Version 2 "wrong generation"
                |> fun result -> if Result.isOk result then failwith "Accepted stale generation"
                ChallengeWorkflow.requeueAttempt a (Guid.NewGuid()) "Agent 3" task attemptId
                    initialTask.Version afterBlock.Blocked.Head.Attempt.Version
                    afterBlock.Agent.Version 1 "manual clear"
                |> ChallengeFixture.ok |> ignore
            else
                let first, second = gates.[0], gates.[1]
                let firstReason = if mode = 3 then "needs revision" else ""
                ChallengeWorkflow.resolve a (Guid.NewGuid()) "Lead"
                    first.ChallengeId first.ConditionIds.Head 1 "review" firstReason
                |> ChallengeFixture.ok |> ignore
                let midway = ReviewWorkflow.getAgentInbox b "Agent 3" |> ChallengeFixture.ok
                if midway.Agent.State <> "NeedsWork" || midway.Blocked.Length <> 1 then
                    failwithf "seed=%d requeued before all dependencies were satisfied" seed
                if mode = 2 then
                    ChallengeWorkflow.cancel b (Guid.NewGuid()) "Agent 3"
                        second.ChallengeId 1 "withdrawn" |> ChallengeFixture.ok |> ignore
                    let blocked = ReviewWorkflow.getAgentInbox a "Agent 3" |> ChallengeFixture.ok
                    ChallengeWorkflow.requeueAttempt b (Guid.NewGuid()) "Agent 3" task attemptId
                        initialTask.Version blocked.Blocked.Head.Attempt.Version
                        blocked.Agent.Version 1 "try cancelled dependency"
                    |> fun result -> if Result.isOk result then failwith "Cancelled dependency requeued"
                else
                    if mode = 3 then
                        ChallengeWorkflow.reopen b (Guid.NewGuid()) "Agent 3" first.ChallengeId
                            first.ConditionIds.Head 2 "recheck" |> ChallengeFixture.ok |> ignore
                        ChallengeWorkflow.resolve a (Guid.NewGuid()) "Lead" first.ChallengeId
                            first.ConditionIds.Head 3 "accepted" "" |> ChallengeFixture.ok |> ignore
                    ChallengeWorkflow.resolve b (Guid.NewGuid()) "Agent 8" second.ChallengeId
                        second.ConditionIds.Head 1 "passed" "" |> ChallengeFixture.ok |> ignore
            let finalInbox = ReviewWorkflow.getAgentInbox b "Agent 3" |> ChallengeFixture.ok
            if mode = 2 then
                if finalInbox.Agent.State <> "NeedsWork"
                   || finalInbox.Blocked.Head.Attempt.AssignmentGeneration <> 1 then
                    failwithf "seed=%d cancelled wait gained runnable work" seed
            else
                if finalInbox.Agent.State <> "Ready"
                   || finalInbox.RunnableQueue.Length <> 1
                   || finalInbox.RunnableQueue.Head.Attempt.AssignmentGeneration <> 2 then
                    failwithf "seed=%d did not requeue exactly once to generation 2" seed
                let next = finalInbox.RunnableQueue.Head.Attempt
                if next.TaskId <> initialAttempt.TaskId
                   || next.AttemptId <> initialAttempt.AttemptId
                   || next.OwnerAgentId <> initialAttempt.OwnerAgentId
                   || next.SpecBlobOid <> initialAttempt.SpecBlobOid
                   || next.BaseCommit <> initialAttempt.BaseCommit
                   || next.TargetRef <> initialAttempt.TargetRef
                   || next.SourceRef <> initialAttempt.SourceRef then
                    failwithf "seed=%d requeue changed pinned ownership or source" seed
                ChallengeWorkflow.requeueAttempt a (Guid.NewGuid()) "Agent 3" task attemptId
                    initialTask.Version initialAttempt.Version initialAgent.Version 1 "replayed old generation"
                |> fun result -> if Result.isOk result then failwith "Duplicate/stale requeue accepted"
            verifyEdges a task attemptId 1 dependencies)

[<TestFixture>]
type ChallengeDependencyPropertyTests() =
    [<Test>]
    member _.DependencyTracesRequireConjunctionAndPreserveHistoricReverseEdges() =
        for seed in [ 44; 45; 46; 47 ] do
            ChallengeDependencyChecks.trace seed

    [<Test>]
    member _.ConcurrentTwoCloneWritersAcceptAtMostOneVersionWinner() =
        for seed in [ 31; 32; 33 ] do
            ChallengeFixture.withTwoClones (fun a b ->
                let created =
                    ChallengeWorkflow.create a (Guid.NewGuid()) "Agent 3"
                        (sprintf "CAS seed %d" seed) "" ChallengeFixture.definitions
                    |> ChallengeFixture.ok
                let firstBinding, secondBinding =
                    if seed % 2 = 0 then a, b else b, a
                let first =
                    System.Threading.Tasks.Task.Run(fun () ->
                        ChallengeWorkflow.resolve firstBinding (Guid.NewGuid()) "Lead"
                            created.ChallengeId created.ConditionIds.[0] 1 "review pass" "")
                let second =
                    System.Threading.Tasks.Task.Run(fun () ->
                        ChallengeWorkflow.resolve secondBinding (Guid.NewGuid()) "Agent 8"
                            created.ChallengeId created.ConditionIds.[1] 1 "benchmark pass" "")
                System.Threading.Tasks.Task.WaitAll([| first :> System.Threading.Tasks.Task; second :> System.Threading.Tasks.Task |])
                let results = [ first.Result; second.Result ]
                if (results |> List.filter Result.isOk).Length <> 1 then
                    failwithf "seed=%d CAS admitted wrong number of winners: %A" seed results
                let authoritative = ChallengeWorkflow.get a "Agent 3" created.ChallengeId
                                    |> ChallengeFixture.ok
                if authoritative.Challenge.Version <> 2
                   || authoritative.History.Length <> 2
                   || (authoritative.Challenge.Conditions
                       |> List.filter (fun item -> item.Status = "Satisfied")).Length <> 1 then
                    failwithf "seed=%d did not preserve single-winner state" seed
                let snapshot = LedgerStore.fetch b |> ChallengeFixture.ok
                if not (LedgerStore.listAt b snapshot "notifications" |> ChallengeFixture.ok |> List.isEmpty) then
                    failwithf "seed=%d CAS race created notifications" seed)


// Corruption probes deliberately use the real project CAS ledger, not mock JSON
// readers. Each fixture has one bare remote and two clones, and the mutation is
// inserted only into that disposable remote. These are fail-closed assertions.
module private ChallengeIndexFaults =
    let inject binding file body =
        let snapshot = LedgerStore.fetch binding |> ChallengeFixture.ok
        let _, oldOid = LedgerStore.readAt binding snapshot file |> ChallengeFixture.ok
        let intent : LedgerIntent =
            { OperationId = Guid.NewGuid()
              Actor = "Fixture"
              Kind = "AdversarialChallengeWaitIndex"
              RequestJson = System.Text.Json.JsonSerializer.Serialize(
                  {| path = file; body = body |}) }
        LedgerStore.transact binding intent (fun _ ->
            Ok [ { Path = file; ExpectedBlobOid = oldOid; Content = body } ])
        |> ChallengeFixture.ok |> ignore

    let indexBody task attempt owner generation =
        System.Text.Json.JsonSerializer.Serialize(
            {| schemaVersion = 2; taskId = task; attemptId = attempt
               ownerAgentId = owner; assignmentGeneration = generation |})

    let probe mode =
        ChallengeFixture.withTwoClones (fun a b ->
            let task = sprintf "fault-index-%d" mode
            let taskRecord, attemptRecord, agent, attemptId =
                ChallengeDependencyChecks.assign a task
            let gate = ChallengeDependencyChecks.gate b task "Corruption gate" "Lead"
            ChallengeWorkflow.blockAttempt a (Guid.NewGuid()) "Agent 3" task attemptId
                taskRecord.Version attemptRecord.Version agent.Version 1
                "Await gate" [ gate.ChallengeId ] |> ChallengeFixture.ok |> ignore
            let challengePrefix = ReviewWorkflow.challengeWaitIndexPrefix gate.ChallengeId
            let challengeEdge =
                ReviewWorkflow.waitIndexPath challengePrefix task attemptId 1
            let ownerPrefix = ReviewWorkflow.ownerWaitIndexPrefix "Agent 3"
            let faultPath, faultBody =
                match mode with
                | 0 ->
                    // An orphan next-generation challenge edge cannot be silently
                    // treated as a valid historical edge of the active wait.
                    ReviewWorkflow.waitIndexPath challengePrefix task attemptId 2,
                    indexBody task attemptId "Agent 3" 2
                | 1 ->
                    // The canonical wait says Agent 3, but its claimed index owner
                    // is different. Skipping it would strand the real dependency.
                    challengeEdge, indexBody task attemptId "Intruder" 1
                | 2 ->
                    // An unpaired by-owner next-generation edge has no canonical
                    // wait for generation 2 and must not project runnable work.
                    ReviewWorkflow.waitIndexPath ownerPrefix task attemptId 2,
                    indexBody task attemptId "Agent 3" 2
                | _ ->
                    // A mismatched task identity in the by-challenge locator
                    // must be rejected before completing the challenge.
                    challengeEdge, indexBody "wrong-task" attemptId "Agent 3" 1
            inject a faultPath faultBody
            if mode = 2 then
                let read = ReviewWorkflow.getAgentInbox b "Agent 3"
                match read with
                | Error(LedgerIntegrityError _) -> ()
                | result ->
                    failwithf "source=%s faultSeed=%d steps=[create task; assign N=1; create challenge; block; inject orphan by-owner N+1; inbox read] expected=LedgerIntegrityError actual=%s"
                        (ChallengeFixture.sourceSha()) (9000 + mode)
                        (if Result.isOk result then "Ok" else sprintf "%A" result)
            else
                let resolution =
                    ChallengeWorkflow.resolve b (Guid.NewGuid()) "Lead"
                        gate.ChallengeId gate.ConditionIds.Head 1 "passed" ""
                match resolution with
                | Error(LedgerIntegrityError _) -> ()
                | result ->
                    let head = LedgerStore.fetch a |> ChallengeFixture.ok
                    let remote = ChallengeWorkflow.get a "Agent 3" gate.ChallengeId |> ChallengeFixture.ok
                    failwithf "source=%s faultSeed=%d steps=[create task; assign N=1; create challenge; block; inject reverse-index mode=%d; resolve] expected=LedgerIntegrityError actual=%s remoteHead=%s challengeStatus=%s attemptGenerationExpected=1"
                        (ChallengeFixture.sourceSha()) (9000 + mode) mode
                        (if Result.isOk result then "Ok" else sprintf "%A" result)
                        head.Commit remote.Challenge.Status)

[<TestFixture>]
type ChallengeReverseIndexFaultTests() =
    [<TestCase(0)>]
    [<TestCase(1)>]
    [<TestCase(2)>]
    [<TestCase(3)>]
    member _.CorruptOrOrphanedReverseIndicesFailClosed(mode: int) =
        ChallengeIndexFaults.probe mode


[<TestFixture>]
type ChallengeReceiptProperties() =
    [<Test>]
    member _.TwoCloneReplayKeepsImmutableReceiptAndRejectsMutatedSameUuid() =
        for seed in [ 77; 78; 79 ] do
            ChallengeFixture.withTwoClones (fun a b ->
                let createId = ChallengeModel.uuid seed "create"
                let created =
                    ChallengeWorkflow.create a createId "Agent 3"
                        "Receipt equality" "" ChallengeFixture.definitions
                    |> ChallengeFixture.ok
                let replay =
                    ChallengeWorkflow.create b createId "Agent 3"
                        "Receipt equality" "" ChallengeFixture.definitions
                    |> ChallengeFixture.ok
                if not replay.Receipt.AlreadyApplied
                   || replay.Receipt.Operation.Sequence <> created.Receipt.Operation.Sequence
                   || replay.Receipt.Operation.RequestHash <> created.Receipt.Operation.RequestHash
                   || replay.Receipt.Operation.OriginalResultHash <> created.Receipt.Operation.OriginalResultHash
                   || replay.ChallengeId <> created.ChallengeId
                   || replay.ConditionIds <> created.ConditionIds then
                    failwithf "seed=%d source=%s create replay altered immutable receipt"
                        seed (ChallengeFixture.sourceSha())
                let beforeMutatedCreate = LedgerStore.fetch a |> ChallengeFixture.ok
                match ChallengeWorkflow.create b createId "Agent 3"
                          "Different payload" "" ChallengeFixture.definitions with
                | Error _ -> ()
                | Ok _ -> failwithf "seed=%d accepted mutated create UUID" seed
                let afterMutatedCreate = LedgerStore.fetch a |> ChallengeFixture.ok
                if beforeMutatedCreate.Commit <> afterMutatedCreate.Commit then
                    failwithf "seed=%d mutated create modified canonical remote" seed
                let resolveId = ChallengeModel.uuid seed "resolve"
                let resolved =
                    ChallengeWorkflow.resolve a resolveId "Lead"
                        created.ChallengeId created.ConditionIds.[0] 1 "approved" ""
                    |> ChallengeFixture.ok
                let resolveReplay =
                    ChallengeWorkflow.resolve b resolveId "Lead"
                        created.ChallengeId created.ConditionIds.[0] 1 "approved" ""
                    |> ChallengeFixture.ok
                if not resolveReplay.AlreadyApplied
                   || resolveReplay.Operation.Sequence <> resolved.Operation.Sequence
                   || resolveReplay.Operation.RequestHash <> resolved.Operation.RequestHash
                   || resolveReplay.Operation.OriginalResultHash <> resolved.Operation.OriginalResultHash then
                    failwithf "seed=%d source=%s resolve replay altered immutable receipt"
                        seed (ChallengeFixture.sourceSha())
                let beforeMutatedResolve = LedgerStore.fetch b |> ChallengeFixture.ok
                match ChallengeWorkflow.resolve a resolveId "Lead"
                          created.ChallengeId created.ConditionIds.[0] 1 "forged evidence" "" with
                | Error _ -> ()
                | Ok _ -> failwithf "seed=%d accepted mutated resolve UUID" seed
                let afterMutatedResolve = LedgerStore.fetch b |> ChallengeFixture.ok
                if beforeMutatedResolve.Commit <> afterMutatedResolve.Commit then
                    failwithf "seed=%d mutated resolve modified canonical remote" seed
                let details = ChallengeWorkflow.get b "Agent 3" created.ChallengeId |> ChallengeFixture.ok
                if details.Challenge.Version <> 2 || details.History.Length <> 2 then
                    failwithf "seed=%d replay duplicated event or version" seed
                let notices = LedgerStore.listAt b afterMutatedResolve "notifications"
                              |> ChallengeFixture.ok
                if not notices.IsEmpty then failwithf "seed=%d replay emitted notifications" seed)
