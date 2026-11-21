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

// Expected error families are part of the oracle, not merely "some Error".
type private FailureFamily = Invalid | Stale | Denied | Reused | Busy
type private Expected = Applied | Replayed | Rejected of FailureFamily

type private ModelCounterexample(category: string, detail: string) =
    inherit Exception(detail)
    member _.Category = category

let private counterexample category detail =
    raise (ModelCounterexample(category, detail))

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
        | Some _ -> Rejected Reused, state
        | None ->
            let update slot transform =
                match Map.tryFind slot state.Challenges with
                | None -> Rejected Stale, state
                | Some challenge ->
                    match transform challenge with
                    | Error family -> Rejected family, state
                    | Ok next ->
                        Applied,
                        { state with
                            Challenges = Map.add slot next state.Challenges
                            Receipts = Map.add operationId call state.Receipts }
            match call with
            | NewChallenge(slot, variant) ->
                if state.Challenges.ContainsKey slot then Rejected Reused, state
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
                    if terminal challenge || challenge.Version <> version || i < 0 || i > 1 then
                        Error Stale
                    elif actor who <> (if i = 0 then "Lead" else "Agent 8") then
                        Error Denied
                    elif challenge.Conditions.[i] <> Waiting then Error Stale
                    else
                        Ok { challenge with
                                Conditions =
                                    challenge.Conditions
                                    |> List.mapi (fun j old ->
                                        if j = i then (if blocked then Obstructed else Done) else old)
                                Version = version + 1 })
            | Reopen(slot, i, who, version, _) ->
                update slot (fun challenge ->
                    if terminal challenge || challenge.Version <> version then Error Stale
                    elif who <> 0 then Error Denied
                    elif i < 0 || i > 1 || challenge.Conditions.[i] <> Obstructed then Error Stale
                    else
                        Ok { challenge with
                                Conditions =
                                    challenge.Conditions
                                    |> List.mapi (fun j old -> if j = i then Waiting else old)
                                Version = version + 1 })
            | Cancel(slot, who, version, _) ->
                update slot (fun challenge ->
                    if terminal challenge || challenge.Version <> version then Error Stale
                    elif who <> 0 then Error Denied
                    else Ok { challenge with Cancelled = true; Version = version + 1 })

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
                | Rejected _ -> rejected <- rejected + 1
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
                |> Result.map (fun created -> created.Receipt.AlreadyApplied, Some(slot, created.ConditionIds), created.Receipt)
            | Answer(slot, index, who, version, blocked, variant) ->
                let evidence = if variant then "evidence-modified" else "evidence"
                let reason = if blocked then "blocked" else ""
                ChallengeWorkflow.resolve binding opId (ChallengeModel.actor who)
                    (challenge slot) (condition slot index) version evidence reason
                |> Result.map (fun write -> write.AlreadyApplied, None, write)
            | Reopen(slot, index, who, version, variant) ->
                ChallengeWorkflow.reopen binding opId (ChallengeModel.actor who)
                    (challenge slot) (condition slot index) version
                    (if variant then "retry-modified" else "retry")
                |> Result.map (fun write -> write.AlreadyApplied, None, write)
            | Cancel(slot, who, version, variant) ->
                ChallengeWorkflow.cancel binding opId (ChallengeModel.actor who)
                    (challenge slot) version
                    (if variant then "withdrawn-modified" else "withdrawn")
                |> Result.map (fun write -> write.AlreadyApplied, None, write)
        result


    // The audit oracle records the actor/action independently of the production
    // event serializer and checks the *full* immutable event history after each step.
    let private auditIdentity = function
        | NewChallenge(slot, _) -> slot, "Agent 3", "Created"
        | Answer(slot, _, who, _, _, _) -> slot, ChallengeModel.actor who, "ResolveChallengeCondition"
        | Reopen(slot, _, who, _, _) -> slot, ChallengeModel.actor who, "ReopenChallengeCondition"
        | Cancel(slot, who, _, _) -> slot, ChallengeModel.actor who, "CancelChallenge"

    let classifyError = function
        | InvalidBinding _ -> Rejected Invalid
        | StaleEntityVersion _ -> Rejected Stale
        | ReviewAccessDenied _ -> Rejected Denied
        | OperationIdReused -> Rejected Reused
        | AgentBusy _ -> Rejected Busy
        | LedgerIntegrityError reason ->
            counterexample "UnexpectedLedgerIntegrity" ("Unexpected integrity error in valid fixture: " + reason)
        | problem ->
            failwithf "Infrastructure or unmodeled ledger failure, not a shrinkable counterexample: %A" problem

    let checkActual seed binding (state: Reference) (expectedAudit: Map<int, (string * string) list>) =
        for KeyValue(slot, expected) in state.Challenges do
            let id = ChallengeModel.challengeId seed slot
            let first = ChallengeWorkflow.getPage binding "Agent 3" id 16 0L "" |> ok
            let found = first.Challenge
            if found.Version <> expected.Version || found.Status <> ChallengeModel.status expected
               || found.Conditions |> List.map _.Status <> (expected.Conditions |> List.map ChallengeModel.phase) then
                counterexample (sprintf "ChallengeState#%d" slot)
                    (sprintf "slot=%d expected=%A actual={version=%d;status=%s;conditions=%A}"
                        slot expected found.Version found.Status (found.Conditions |> List.map _.Status))
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
                counterexample (sprintf "AuditSequence#%d" slot)
                    (sprintf "slot=%d expectedEvents=%d actualSequences=%A" slot expected.Version sequences)
            let actualActorsAndActions = events |> List.map (fun e -> e.ActorId, e.Action)
            let expectedActorsAndActions = expectedAudit |> Map.tryFind slot |> Option.defaultValue []
            if actualActorsAndActions <> expectedActorsAndActions then
                counterexample (sprintf "AuditActorAction#%d" slot)
                    (sprintf "slot=%d expected=%A actual=%A" slot expectedActorsAndActions actualActorsAndActions)
            if events |> List.exists (fun e -> e.ChallengeId <> id) then
                counterexample (sprintf "AuditIdentity#%d" slot) (sprintf "Wrong challenge identity in %A" events)

    let runTrace seed (commands: Command list) =
        withTwoClones (fun a b ->
            let mutable state = ChallengeModel.empty
            let mutable previous = Map.empty
            let mutable ids: Map<int, string list> = Map.empty
            let mutable receipts: Map<string, LedgerOperation> = Map.empty
            let mutable audit: Map<int, (string * string) list> = Map.empty
            for command in commands do
                match ChallengeModel.chooseCall state command previous with
                | None -> ()
                | Some(call, original) ->
                    if not (match command.Action with Repeat _ -> true | _ -> false) then
                        previous <- Map.add command.Number call previous
                    let opKey = ChallengeModel.operationKey original call
                    let expected, next = ChallengeModel.apply state opKey call
                    let binding = if command.Number % 2 = 0 then a else b
                    let other = if command.Number % 2 = 0 then b else a
                    let opId = ChallengeModel.uuid seed opKey
                    // fetch from both independent clones so a stale checkout cannot
                    // accidentally stand in for the canonical remote authority.
                    let before = LedgerStore.fetch other |> ok
                    let beforeIndex = LedgerStore.listAt other before (ReviewWorkflow.ownerWaitIndexPrefix "Agent 3") |> ok
                    let outcome = actual seed binding opId ids call
                    let observed =
                        match outcome with
                        | Ok(replay, newIds, _) ->
                            match newIds with
                            | Some(slot, conditions) -> ids <- Map.add slot conditions ids
                            | None -> ()
                            if replay then Replayed else Applied
                        | Error error -> classifyError error
                    if expected <> observed then
                        counterexample (sprintf "Outcome#%d:%A:%A" command.Number expected observed)
                            (sprintf "seed=%d command=%A call=%A expected=%A observed=%A raw=%A"
                                seed command call expected observed outcome)
                    let after = LedgerStore.fetch other |> ok
                    if observed <> Applied then
                        if before.Commit <> after.Commit || before.Header.NextSequence <> after.Header.NextSequence then
                            counterexample (sprintf "RejectedOrReplayMutation#%d" command.Number)
                                (sprintf "seed=%d command=%A before=%s after=%s" seed command before.Commit after.Commit)
                        let afterIndex = LedgerStore.listAt other after (ReviewWorkflow.ownerWaitIndexPrefix "Agent 3") |> ok
                        if beforeIndex <> afterIndex then
                            counterexample (sprintf "RejectedOrReplayIndex#%d" command.Number)
                                (sprintf "seed=%d command=%A index before=%A after=%A" seed command beforeIndex afterIndex)
                    let slot, actor, action = auditIdentity call
                    match outcome with
                    | Ok(replay, _, receipt) ->
                        let originalReceipt = receipts |> Map.tryFind opKey
                        if replay then
                            match originalReceipt with
                            | None -> counterexample (sprintf "MissingReplayOrigin#%d" command.Number) opKey
                            | Some source when
                                source.RequestHash <> receipt.Operation.RequestHash
                                || source.OriginalResultHash <> receipt.Operation.OriginalResultHash
                                || source.Sequence <> receipt.Operation.Sequence
                                || source.Actor <> receipt.Operation.Actor
                                || source.Type <> receipt.Operation.Type ->
                                counterexample (sprintf "ChangedImmutableReceipt#%d" command.Number)
                                    (sprintf "original=%A replay=%A" source receipt.Operation)
                            | Some _ -> ()
                        else
                            if receipt.Operation.Sequence <> before.Header.NextSequence
                               || after.Header.NextSequence <> before.Header.NextSequence + 1L
                               || receipt.CurrentHead <> after.Commit
                               || receipt.Operation.Actor <> actor
                               || receipt.Operation.Type <> (match call with NewChallenge _ -> "CreateChallenge" | _ -> action) then
                                counterexample (sprintf "OperationAudit#%d" command.Number)
                                    (sprintf "before=%A after=%A operation=%A actor=%s action=%s"
                                        before after receipt.Operation actor action)
                            receipts <- receipts |> Map.add opKey receipt.Operation
                            let prior = audit |> Map.tryFind slot |> Option.defaultValue []
                            audit <- audit |> Map.add slot (prior @ [ actor, action ])
                    | Error _ -> ()
                    state <- next
                    checkActual seed other state audit
            let snapshot = LedgerStore.fetch a |> ok
            let allNotifications = LedgerStore.listAt a snapshot "notifications" |> ok
            if not allNotifications.IsEmpty then
                counterexample "UnexpectedNotification" (sprintf "seed=%d notifications=%A" seed allNotifications))
    // Deletion shrinking preserves command IDs, UUIDs and replay payloads.
    // A missing referenced command becomes an inert step, not a rewritten call.
    let shrink seed (original: Command list) (failure: ModelCounterexample) =
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
                    with
                    | :? ModelCounterexample as next -> next.Category = failure.Category
                    | ex ->
                        raise (InvalidOperationException(
                            sprintf "Shrinker encountered infrastructure failure, not category=%s" failure.Category, ex))
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
            with :? ModelCounterexample as originalFailure ->
                let minimal, reductions, deletionMinimal = ChallengeFixture.shrink seed commands originalFailure
                let minimizedFailure =
                    try
                        ChallengeFixture.runTrace seed minimal
                        "SHRINKER ERROR: no longer fails"
                    with :? ModelCounterexample as failure ->
                        if failure.Category <> originalFailure.Category then
                            failwithf "Shrinker changed category from %s to %s" originalFailure.Category failure.Category
                        failure.Message
                Assert.Fail(
                    sprintf "Challenge model mismatch source=%s seed=%d category=%s initial=%A shrinkTrials=%d oneMinimal=%b minimal=%A minimizedFailure=%s originalFailure=%s"
                        (ChallengeFixture.sourceSha()) seed originalFailure.Category commands reductions deletionMinimal minimal minimizedFailure originalFailure.Message)


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


//
// Independent mixed task/Challenge model. Unlike the four fixed dependency
// examples above, generated commands carry frozen CAS versions and can be
// replayed or deletion-shrunk without silently rewriting the original payload.
//
module private MixedChallengeAttemptModel =
    type GatePhase = Open | Obstructed | Passed | Abandoned
    type Kind =
        | WaitOn | ManualRequeue | StartAttempt | PassGate | BlockGate
        | ReopenGate | CancelGate | Observe | Retry of int * bool
    type Command =
        { Number: int; Kind: Kind; Actor: int; Gate: int
          Dependencies: int list
          TaskVersion: int; AttemptVersion: int; AgentVersion: int
          ChallengeVersion: int; Generation: int; Variant: bool }
    type State =
        { Gates: GatePhase list; GateVersions: int list
          Phase: string; Owner: string
          Generation: int; TaskVersion: int; AttemptVersion: int; AgentVersion: int
          WaitStatus: string; WaitVersion: int; Dependencies: int list
          Historical: (int * int list) list
          Receipts: Map<int, Command> }

    let initial taskVersion attemptVersion agentVersion =
        { Gates = List.replicate 4 Open; GateVersions = List.replicate 4 1
          Phase = "Started"; Owner = "Working"
          Generation = 1; TaskVersion = taskVersion
          AttemptVersion = attemptVersion; AgentVersion = agentVersion
          WaitStatus = "Missing"; WaitVersion = 0; Dependencies = []
          Historical = []; Receipts = Map.empty }

    let actor who = ChallengeModel.actor who
    let ownerOf gate = if gate % 2 = 0 then 1 else 2
    let phase = function
        | Open -> "Pending" | Obstructed -> "Blocked"
        | Passed -> "Satisfied" | Abandoned -> "Cancelled"
    let commandAction = function
        | WaitOn -> "WaitOnChallenges"
        | ManualRequeue -> "RequeueChallengeBlockedAttempt"
        | StartAttempt -> "BeginAttempt"
        | PassGate | BlockGate -> "ResolveChallengeCondition"
        | ReopenGate -> "ReopenChallengeCondition"
        | CancelGate -> "CancelChallenge"
        | Observe | Retry _ -> ""

    let editAt index replacement (xs: 'a list) =
        xs |> List.mapi (fun i old -> if i = index then replacement else old)

    let materialize (attempted: Map<int,Command>) (command: Command) =
        match command.Kind with
        | Retry(original, mutated) ->
            attempted |> Map.tryFind original
            |> Option.map (fun baseCommand ->
                { baseCommand with Variant = if mutated then not baseCommand.Variant else baseCommand.Variant },
                original)
        | _ -> Some(command, command.Number)

    let apply (state: State) (opNumber: int) (call: Command) =
        match Map.tryFind opNumber state.Receipts with
        | Some previous when previous = call -> Replayed, state
        | Some _ -> Rejected Reused, state
        | None ->
            let bad family = Rejected family, state
            let commit (next: State) = Applied, { next with Receipts = Map.add opNumber call state.Receipts }
            let versions =
                state.TaskVersion = call.TaskVersion
                && state.AttemptVersion = call.AttemptVersion
                && state.AgentVersion = call.AgentVersion
                && state.Generation = call.Generation
            let gateVersion = state.GateVersions.[call.Gate] = call.ChallengeVersion
            match call.Kind with
            | WaitOn ->
                if call.Dependencies |> List.distinct |> List.length <> call.Dependencies.Length then bad Invalid
                elif not versions || call.Actor <> 0 then bad Stale
                elif not (List.contains state.Phase [ "Started"; "Queued" ])
                     || not (List.contains state.Owner [ "Working"; "Ready" ]) then bad Busy
                elif call.Dependencies |> List.exists (fun i -> state.Gates.[i] = Abandoned) then bad Stale
                elif not (List.isEmpty call.Dependencies)
                     && call.Dependencies |> List.forall (fun i -> state.Gates.[i] = Passed) then bad Stale
                else
                    let history =
                        if List.isEmpty call.Dependencies then state.Historical
                        else state.Historical @ [ state.Generation, call.Dependencies ]
                    commit { state with Phase = "Blocked"; Owner = "NeedsWork"
                                        AttemptVersion = state.AttemptVersion + 1
                                        AgentVersion = state.AgentVersion + 1
                                        WaitStatus = "Waiting"; WaitVersion = state.WaitVersion + 1
                                        Dependencies = call.Dependencies; Historical = history }
            | ManualRequeue ->
                if not versions || call.Actor <> 0 then bad Stale
                elif state.Phase <> "Blocked" || state.WaitStatus <> "Waiting" then bad Stale
                elif state.Dependencies |> List.exists (fun i -> state.Gates.[i] <> Passed) then bad Stale
                else
                    commit { state with Phase = "Queued"; Owner = "Ready"
                                        Generation = state.Generation + 1
                                        AttemptVersion = state.AttemptVersion + 1
                                        AgentVersion = state.AgentVersion + 1
                                        WaitStatus = "Requeued"; WaitVersion = state.WaitVersion + 1 }
            | StartAttempt ->
                if call.Actor <> 0 || call.AttemptVersion <> state.AttemptVersion
                   || call.Generation <> state.Generation || call.Variant then bad Stale
                elif state.Phase <> "Queued" || state.Owner <> "Ready" then bad Stale
                else
                    commit { state with Phase = "Started"; Owner = "Working"
                                        AttemptVersion = state.AttemptVersion + 1
                                        AgentVersion = state.AgentVersion + 1 }
            | PassGate | BlockGate | ReopenGate | CancelGate ->
                let current = state.Gates.[call.Gate]
                if not gateVersion || current = Passed || current = Abandoned then bad Stale
                elif (match call.Kind with
                      | PassGate | BlockGate -> call.Actor <> ownerOf call.Gate
                      | _ -> call.Actor <> 0) then bad Denied
                elif (match call.Kind with
                      | PassGate | BlockGate -> current <> Open
                      | ReopenGate -> current <> Obstructed
                      | _ -> false) then bad Stale
                else
                    let nextPhase =
                        match call.Kind with
                        | PassGate -> Passed | BlockGate -> Obstructed
                        | ReopenGate -> Open | _ -> Abandoned
                    let gates = editAt call.Gate nextPhase state.Gates
                    let wakes =
                        call.Kind = PassGate
                        && state.Phase = "Blocked" && state.WaitStatus = "Waiting"
                        && not (List.isEmpty state.Dependencies)
                        && state.Dependencies |> List.forall (fun i -> gates.[i] = Passed)
                    let next =
                        { state with Gates = gates
                                     GateVersions = editAt call.Gate (call.ChallengeVersion + 1) state.GateVersions }
                    if wakes then
                        commit { next with Phase = "Queued"; Owner = "Ready"
                                           Generation = state.Generation + 1
                                           AttemptVersion = state.AttemptVersion + 1
                                           AgentVersion = state.AgentVersion + 1
                                           WaitStatus = "Requeued"; WaitVersion = state.WaitVersion + 1 }
                    else commit next
            | Observe | Retry _ -> bad Invalid

    let generate seed count (start: State) =
        let rng = Random(seed)
        let mutable state = start
        let mutable attempted = Map.empty
        [ for number in 0 .. count - 1 do
            let kind, gate, actor, deps =
                match number with
                | 0 -> WaitOn, 0, 0, [ 0; 1 ]
                | 1 -> PassGate, 0, 1, []
                | 2 -> ManualRequeue, 0, 0, []
                | 3 -> PassGate, 1, 2, []
                | 4 -> StartAttempt, 0, 0, []
                | 5 -> WaitOn, 0, 0, []
                | 6 -> ManualRequeue, 0, 0, []
                | 7 -> StartAttempt, 0, 0, []
                | _ ->
                    let gate = rng.Next(4)
                    let validActor = if rng.Next(5) = 0 then 3 else ownerOf gate
                    match rng.Next(11) with
                    | 0 | 1 ->
                        let sets = [| []; [ 0 ]; [ 1 ]; [ 2 ]; [ 3 ]; [ 2; 3 ]; [ 0; 1 ]; [ 2; 2 ] |]
                        WaitOn, gate, (if rng.Next(5) = 0 then 3 else 0), sets.[rng.Next(sets.Length)]
                    | 2 -> ManualRequeue, gate, (if rng.Next(5) = 0 then 3 else 0), []
                    | 3 -> StartAttempt, gate, (if rng.Next(5) = 0 then 3 else 0), []
                    | 4 | 5 -> PassGate, gate, validActor, []
                    | 6 -> BlockGate, gate, validActor, []
                    | 7 -> ReopenGate, gate, (if rng.Next(5) = 0 then 3 else 0), []
                    | 8 -> CancelGate, gate, (if rng.Next(5) = 0 then 3 else 0), []
                    | 9 -> Observe, gate, 0, []
                    | _ -> Retry(rng.Next(number), rng.Next(3) = 0), gate, 0, []
            let stale = number > 7 && rng.Next(7) = 0
            let call =
                { Number = number; Kind = kind; Actor = actor; Gate = gate
                  Dependencies = deps
                  TaskVersion = state.TaskVersion
                  AttemptVersion = state.AttemptVersion + (if stale then 1 else 0)
                  AgentVersion = state.AgentVersion; ChallengeVersion = state.GateVersions.[gate] + (if stale then 1 else 0)
                  Generation = state.Generation; Variant = false }
            if kind <> Observe && (match kind with Retry _ -> false | _ -> true) then
                attempted <- attempted |> Map.add number call
            match materialize attempted call with
            | Some(actual, original) when kind <> Observe ->
                let _, next = apply state original actual
                state <- next
            | _ -> ()
            yield call ]

module private MixedChallengeAttemptFixture =
    open MixedChallengeAttemptModel

    let prepare action =
        ChallengeFixture.withTwoClones (fun a b ->
            let task = "mixed-property-task"
            let taskRecord, attemptRecord, agent, attemptId = ChallengeDependencyChecks.assign a task
            let gates =
                [ for i in 0 .. 3 ->
                    ChallengeDependencyChecks.gate b task ("Mixed gate " + string i)
                        (if i % 2 = 0 then "Lead" else "Agent 8") ]
            let state = initial taskRecord.Version attemptRecord.Version agent.Version
            action a b task attemptId taskRecord attemptRecord gates state)

    let checkSnapshot binding task attemptId (gates: ChallengeCreated list) (state: State) =
        let snapshot = LedgerStore.fetch binding |> ChallengeFixture.ok
        let _, actualTask, maybeAttempt = ReviewWorkflow.getTask binding task |> ChallengeFixture.ok
        let attempt = maybeAttempt.Value
        let inbox = ReviewWorkflow.getAgentInbox binding "Agent 3" |> ChallengeFixture.ok
        if actualTask.Version <> state.TaskVersion
           || attempt.Version <> state.AttemptVersion
           || attempt.AssignmentGeneration <> state.Generation
           || attempt.Phase <> state.Phase
           || inbox.Agent.Version <> state.AgentVersion
           || inbox.Agent.State <> state.Owner then
            counterexample "MixedTaskState"
                (sprintf "model={phase=%s;owner=%s;gen=%d;taskV=%d;attemptV=%d;agentV=%d} actual={phase=%s;owner=%s;gen=%d;taskV=%d;attemptV=%d;agentV=%d}"
                    state.Phase state.Owner state.Generation state.TaskVersion state.AttemptVersion state.AgentVersion
                    attempt.Phase inbox.Agent.State attempt.AssignmentGeneration actualTask.Version attempt.Version inbox.Agent.Version)
        if attempt.AttemptId <> attemptId || attempt.OwnerAgentId <> "Agent 3" then
            counterexample "MixedPinnedIdentity" (sprintf "Unexpected attempt owner/id: %A" attempt)
        for gate in 0 .. 3 do
            let details = ChallengeWorkflow.get binding "Agent 3" gates.[gate].ChallengeId |> ChallengeFixture.ok
            let phase = phase state.Gates.[gate]
            if details.Challenge.Status <> phase
               || details.Challenge.Version <> state.GateVersions.[gate] then
                counterexample (sprintf "MixedGate#%d" gate)
                    (sprintf "expected=%s/%d actual=%s/%d" phase state.GateVersions.[gate]
                        details.Challenge.Status details.Challenge.Version)
        let waitPath = sprintf "tasks/%s/attempts/%s/challenge-wait.json" task attemptId
        let waitContent, _ = LedgerStore.readAt binding snapshot waitPath |> ChallengeFixture.ok
        match waitContent with
        | None when state.WaitStatus = "Missing" -> ()
        | Some content when state.WaitStatus <> "Missing" ->
            use doc = System.Text.Json.JsonDocument.Parse(content)
            let root = doc.RootElement
            let ids = root.GetProperty("challengeIds").EnumerateArray()
                      |> Seq.map _.GetString() |> List.ofSeq
            let expectedIds = state.Dependencies |> List.map (fun i -> gates.[i].ChallengeId)
            if root.GetProperty("status").GetString() <> state.WaitStatus
               || root.GetProperty("version").GetInt32() <> state.WaitVersion
               || ids <> expectedIds then
                counterexample "MixedCanonicalWait"
                    (sprintf "expected status=%s version=%d ids=%A actual=%s"
                        state.WaitStatus state.WaitVersion expectedIds content)
        | _ ->
            counterexample "MixedMissingWait" (sprintf "expected=%s actual=%A" state.WaitStatus waitContent)
        let ownerPrefix = ReviewWorkflow.ownerWaitIndexPrefix "Agent 3"
        let expectedOwner =
            state.Historical
            |> List.map (fun (gen, _) -> ReviewWorkflow.waitIndexPath ownerPrefix task attemptId gen)
            |> Set.ofList
        let actualOwner = LedgerStore.listAt binding snapshot ownerPrefix |> ChallengeFixture.ok |> Set.ofList
        if actualOwner <> expectedOwner then
            counterexample "MixedOwnerIndex" (sprintf "expected=%A actual=%A" expectedOwner actualOwner)
        for gate in 0 .. 3 do
            let prefix = ReviewWorkflow.challengeWaitIndexPrefix gates.[gate].ChallengeId
            let expectedEdges =
                state.Historical
                |> List.choose (fun (gen, deps) ->
                    if List.contains gate deps then Some(ReviewWorkflow.waitIndexPath prefix task attemptId gen)
                    else None)
                |> Set.ofList
            let found = LedgerStore.listAt binding snapshot prefix |> ChallengeFixture.ok |> Set.ofList
            if found <> expectedEdges then
                counterexample (sprintf "MixedChallengeIndex#%d" gate)
                    (sprintf "expected=%A actual=%A" expectedEdges found)
        let notices = LedgerStore.listAt binding snapshot "notifications" |> ChallengeFixture.ok
        if not notices.IsEmpty then counterexample "MixedNotifications" (sprintf "%A" notices)

    let actual binding task attemptId (gates: ChallengeCreated list) opId (call: Command) =
        let change = if call.Variant then "-mutated" else ""
        match call.Kind with
        | WaitOn ->
            ChallengeWorkflow.blockAttempt binding opId (actor call.Actor) task attemptId
                call.TaskVersion call.AttemptVersion call.AgentVersion call.Generation
                ("model-wait" + change) (call.Dependencies |> List.map (fun i -> gates.[i].ChallengeId))
        | ManualRequeue ->
            ChallengeWorkflow.requeueAttempt binding opId (actor call.Actor) task attemptId
                call.TaskVersion call.AttemptVersion call.AgentVersion call.Generation ("model-requeue" + change)
        | StartAttempt ->
            ReviewWorkflow.beginAttempt binding opId task (actor call.Actor)
                (call.AttemptVersion + (if call.Variant then 1 else 0)) call.Generation
        | PassGate | BlockGate ->
            ChallengeWorkflow.resolve binding opId (actor call.Actor)
                gates.[call.Gate].ChallengeId gates.[call.Gate].ConditionIds.Head
                call.ChallengeVersion ("model-answer" + change)
                (if call.Kind = BlockGate then "model-block" else "")
        | ReopenGate ->
            ChallengeWorkflow.reopen binding opId (actor call.Actor)
                gates.[call.Gate].ChallengeId gates.[call.Gate].ConditionIds.Head
                call.ChallengeVersion ("model-reopen" + change)
        | CancelGate ->
            ChallengeWorkflow.cancel binding opId (actor call.Actor)
                gates.[call.Gate].ChallengeId call.ChallengeVersion ("model-cancel" + change)
        | Observe | Retry _ -> failwith "Unmaterialized command"

    let runCommands seed (commands: Command list) =
        prepare (fun a b task attemptId originalTask originalAttempt gates initialState ->
            let mutable state = initialState
            let mutable attempted = Map.empty
            let mutable recorded: Map<int,LedgerOperation> = Map.empty
            checkSnapshot a task attemptId gates state
            for command in commands do
                match command.Kind with
                | Observe ->
                    let before = LedgerStore.fetch a |> ChallengeFixture.ok
                    checkSnapshot b task attemptId gates state
                    let after = LedgerStore.fetch a |> ChallengeFixture.ok
                    if before.Commit <> after.Commit then
                        counterexample (sprintf "MixedReadMutation#%d" command.Number) "Read changed canonical HEAD"
                | _ ->
                    if (match command.Kind with Retry _ -> false | _ -> true) then
                        attempted <- attempted |> Map.add command.Number command
                    match materialize attempted command with
                    | None -> ()
                    | Some(call, original) ->
                        let expected, next = apply state original call
                        let binding = if command.Number % 2 = 0 then a else b
                        let other = if command.Number % 2 = 0 then b else a
                        let before = LedgerStore.fetch other |> ChallengeFixture.ok
                        let beforeIndex = LedgerStore.listAt other before (ReviewWorkflow.ownerWaitIndexPrefix "Agent 3") |> ChallengeFixture.ok
                        let result = actual binding task attemptId gates
                                         (ChallengeModel.uuid seed (sprintf "mixed:%d" original)) call
                        let observed =
                            match result with
                            | Ok receipt -> if receipt.AlreadyApplied then Replayed else Applied
                            | Error error -> ChallengeFixture.classifyError error
                        if expected <> observed then
                            counterexample (sprintf "MixedOutcome#%d:%A:%A" command.Number expected observed)
                                (sprintf "seed=%d step=%A request=%A expected=%A observed=%A raw=%A"
                                    seed command call expected observed result)
                        let after = LedgerStore.fetch other |> ChallengeFixture.ok
                        if observed <> Applied then
                            let afterIndex = LedgerStore.listAt other after (ReviewWorkflow.ownerWaitIndexPrefix "Agent 3") |> ChallengeFixture.ok
                            if before.Commit <> after.Commit || before.Header.NextSequence <> after.Header.NextSequence
                               || beforeIndex <> afterIndex then
                                counterexample (sprintf "MixedRejectedMutation#%d" command.Number)
                                    (sprintf "seed=%d step=%A before=%s after=%s indexes=%A -> %A"
                                        seed command before.Commit after.Commit beforeIndex afterIndex)
                        match result with
                        | Ok receipt ->
                            if receipt.AlreadyApplied then
                                match recorded |> Map.tryFind original with
                                | Some previous when
                                    previous.Sequence = receipt.Operation.Sequence
                                    && previous.RequestHash = receipt.Operation.RequestHash
                                    && previous.OriginalResultHash = receipt.Operation.OriginalResultHash
                                    && previous.Actor = receipt.Operation.Actor
                                    && previous.Type = receipt.Operation.Type -> ()
                                | previous ->
                                    counterexample (sprintf "MixedReceiptReplay#%d" command.Number)
                                        (sprintf "old=%A new=%A" previous receipt.Operation)
                            else
                                let typ = commandAction call.Kind
                                if receipt.Operation.Sequence <> before.Header.NextSequence
                                   || after.Header.NextSequence <> before.Header.NextSequence + 1L
                                   || receipt.Operation.Actor <> actor call.Actor
                                   || receipt.Operation.Type <> typ
                                   || receipt.CurrentHead <> after.Commit then
                                    counterexample (sprintf "MixedReceiptAudit#%d" command.Number)
                                        (sprintf "before=%A after=%A receipt=%A" before after receipt.Operation)
                                recorded <- recorded |> Map.add original receipt.Operation
                        | Error _ -> ()
                        state <- next
                        checkSnapshot other task attemptId gates state
            if originalTask.TaskId <> task || originalAttempt.AttemptId <> attemptId then
                failwith "Fixture identity drift")

    let shrink seed (original: Command list) (failure: ModelCounterexample) =
        let mutable best = original
        let mutable trials = 0
        let mutable changed = true
        while changed && trials < 32 do
            changed <- false
            let mutable index = 0
            while index < best.Length && not changed && trials < 32 do
                let candidate = best |> List.mapi (fun i item -> i, item)
                                     |> List.choose (fun (i,item) -> if i = index then None else Some item)
                trials <- trials + 1
                let same =
                    try runCommands seed candidate; false
                    with
                    | :? ModelCounterexample as next -> next.Category = failure.Category
                    | ex -> raise (InvalidOperationException("Mixed-model shrink infrastructure failure", ex))
                if same then
                    best <- candidate
                    changed <- true
                index <- index + 1
        best, trials

[<TestFixture>]
type MixedChallengeAttemptPropertyTests() =
    [<Test>]
    member _.GeneratedTaskAndChallengeCommandsExercisePositiveAndNegativePaths() =
        let mutable accepted = 0
        let mutable rejected = 0
        let mutable replayed = 0
        let mutable seen = Set.empty
        for seed in 1 .. 192 do
            let commands = MixedChallengeAttemptModel.generate seed 44 (MixedChallengeAttemptModel.initial 2 2 5)
            let mutable state = MixedChallengeAttemptModel.initial 2 2 5
            let mutable attempted = Map.empty
            for command in commands do
                match command.Kind with
                | MixedChallengeAttemptModel.Observe -> ()
                | _ ->
                    if (match command.Kind with MixedChallengeAttemptModel.Retry _ -> false | _ -> true) then
                        attempted <- attempted |> Map.add command.Number command
                    match MixedChallengeAttemptModel.materialize attempted command with
                    | None -> ()
                    | Some(call, original) ->
                        seen <- Set.add (sprintf "%A" call.Kind) seen
                        let outcome, next = MixedChallengeAttemptModel.apply state original call
                        state <- next
                        match outcome with
                        | Applied -> accepted <- accepted + 1
                        | Replayed -> replayed <- replayed + 1
                        | Rejected _ -> rejected <- rejected + 1
        Assert.That(accepted, Is.GreaterThan(1000))
        Assert.That(rejected, Is.GreaterThan(1000))
        Assert.That(replayed, Is.GreaterThan(100))
        for kind in [ "WaitOn"; "ManualRequeue"; "StartAttempt"; "PassGate"
                      "BlockGate"; "ReopenGate"; "CancelGate" ] do
            Assert.That(Set.contains kind seen, Is.True, "Missing generated command class " + kind)

    [<Test>]
    member _.GeneratedTwoCloneDependencyTracesMatchTaskWaitGenerationAndIndexModel() =
        for seed in [ 101; 102; 103; 104 ] do
            let commands =
                MixedChallengeAttemptFixture.prepare (fun _ _ _ _ task attempt _ agentState ->
                    MixedChallengeAttemptModel.generate seed 20 agentState)
            try MixedChallengeAttemptFixture.runCommands seed commands
            with :? ModelCounterexample as original ->
                let shrunk, attempts = MixedChallengeAttemptFixture.shrink seed commands original
                Assert.Fail(
                    sprintf "Mixed dependency model source=%s seed=%d category=%s original=%A shrinkAttempts=%d minimal=%A detail=%s"
                        (ChallengeFixture.sourceSha()) seed original.Category commands attempts shrunk original.Message)


//
// Explicit one-mutation reverse-index matrix. The first four regression seeds
// (9000-9003) above are deliberately unchanged and remain immutable evidence.
// Missing paths require a real Git-tree deletion; a zero-byte replacement would
// exercise malformed JSON rather than the missing-edge case.
//
module private ChallengeIndexFaultMatrix =
    let private remoteHead (binding: LedgerBinding) =
        ChallengeFixture.git binding.RepositoryPath
            [ "ls-remote"; "origin"; "refs/heads/jarvis-orchestration" ]
        |> fun response -> response.Split('\t').[0]

    let private deleteEdge (binding: LedgerBinding) path =
        let clone = binding.RepositoryPath
        ChallengeFixture.git clone [ "fetch"; "origin"; "refs/heads/jarvis-orchestration" ] |> ignore
        let before = ChallengeFixture.git clone [ "rev-parse"; "FETCH_HEAD" ]
        let scratch = Path.Combine(Path.GetTempPath(), "challenge-index-delete-" + Guid.NewGuid().ToString("N"))
        ChallengeFixture.git clone [ "worktree"; "add"; "--detach"; scratch; before ] |> ignore
        try
            ChallengeFixture.git scratch [ "rm"; "--"; path ] |> ignore
            ChallengeFixture.git scratch
                [ "-c"; "user.name=Fixture"; "-c"; "user.email=fixture@example.test"
                  "commit"; "-m"; "deliberate missing-index corruption" ] |> ignore
            ChallengeFixture.git scratch
                [ "push"; "origin"; "HEAD:refs/heads/jarvis-orchestration" ] |> ignore
        finally
            ChallengeFixture.git clone [ "worktree"; "remove"; "--force"; scratch ] |> ignore

    let private duplicateCanonicalDependency binding task attempt (gate: string) =
        let path = sprintf "tasks/%s/attempts/%s/challenge-wait.json" task attempt
        let snapshot = LedgerStore.fetch binding |> ChallengeFixture.ok
        let source, _ = LedgerStore.readAt binding snapshot path |> ChallengeFixture.ok
        let node = System.Text.Json.Nodes.JsonNode.Parse(source.Value)
        let ids = node["challengeIds"].AsArray()
        ids.Add(System.Text.Json.Nodes.JsonValue.Create(gate))
        ChallengeIndexFaults.inject binding path (node.ToJsonString())

    let private checkRejected label (binding: LedgerBinding) expected action =
        let before = remoteHead binding
        let result = action ()
        let valid =
            match expected, result with
            | "Integrity", Error(LedgerIntegrityError _) -> true
            | "Stale", Error(StaleEntityVersion _) -> true
            | "Invalid", Error(InvalidBinding _) -> true
            | _ -> false
        let after = remoteHead binding
        if not valid || before <> after then
            let observed = match result with Ok _ -> "Ok" | Error error -> sprintf "Error %A" error
            counterexample (sprintf "ReverseIndexMatrix:%s" label)
                (sprintf "source=%s label=%s expected=%s actual=%s remoteBefore=%s remoteAfter=%s"
                    (ChallengeFixture.sourceSha()) label expected observed before after)

    let probe mode =
        ChallengeFixture.withTwoClones (fun a b ->
            let task = sprintf "matrix-index-%d" mode
            let taskRecord, attemptRecord, agent, attemptId = ChallengeDependencyChecks.assign a task
            let first = ChallengeDependencyChecks.gate b task "Primary gate" "Lead"
            let second = ChallengeDependencyChecks.gate b task "Independent gate" "Agent 8"
            let challengePrefix = ReviewWorkflow.challengeWaitIndexPrefix first.ChallengeId
            let challengeEdge = ReviewWorkflow.waitIndexPath challengePrefix task attemptId 1
            let ownerPrefix = ReviewWorkflow.ownerWaitIndexPrefix "Agent 3"
            let ownerEdge = ReviewWorkflow.waitIndexPath ownerPrefix task attemptId 1
            let body = ChallengeIndexFaults.indexBody task attemptId "Agent 3" 1
            let block ids =
                ChallengeWorkflow.blockAttempt a (Guid.NewGuid()) "Agent 3" task attemptId
                    taskRecord.Version attemptRecord.Version agent.Version 1
                    "Model integrity wait" ids |> ChallengeFixture.ok |> ignore
            if mode = 15 || mode = 20 then block []
            else block [ first.ChallengeId ]
            let resolveFirst () =
                ChallengeWorkflow.resolve b (Guid.NewGuid()) "Lead"
                    first.ChallengeId first.ConditionIds.Head 1 "approved" ""
            let readOwner who () = ReviewWorkflow.getAgentInbox b who
            match mode with
            | 4 ->
                deleteEdge a challengeEdge
                checkRejected "missing-by-challenge" a "Integrity" resolveFirst
            | 5 ->
                deleteEdge a ownerEdge
                checkRejected "missing-by-owner" a "Integrity" (readOwner "Agent 3")
            | 6 ->
                let otherPrefix = ReviewWorkflow.challengeWaitIndexPrefix second.ChallengeId
                ChallengeIndexFaults.inject a
                    (ReviewWorkflow.waitIndexPath otherPrefix task attemptId 1) body
                checkRejected "extra-by-challenge-unlinked-gate" a "Integrity" (fun () ->
                    ChallengeWorkflow.resolve b (Guid.NewGuid()) "Agent 8"
                        second.ChallengeId second.ConditionIds.Head 1 "approved" "")
            | 7 ->
                let intruderPrefix = ReviewWorkflow.ownerWaitIndexPrefix "Intruder"
                ChallengeIndexFaults.inject a
                    (ReviewWorkflow.waitIndexPath intruderPrefix task attemptId 1)
                    (ChallengeIndexFaults.indexBody task attemptId "Intruder" 1)
                checkRejected "extra-foreign-owner-edge" a "Integrity" (readOwner "Intruder")
            | 8 ->
                let abandonedPrefix =
                    ReviewWorkflow.challengeWaitIndexPrefix (Guid.NewGuid().ToString("N"))
                ChallengeIndexFaults.inject a
                    (ReviewWorkflow.waitIndexPath abandonedPrefix task attemptId 1) body
                checkRejected "nonexistent-challenge-reverse-index" a "Integrity" (readOwner "Agent 3")
            | 9 ->
                ChallengeIndexFaults.inject a ownerEdge
                    (ChallengeIndexFaults.indexBody task (Guid.NewGuid().ToString("N")) "Agent 3" 1)
                checkRejected "stale-attempt-id" a "Integrity" (readOwner "Agent 3")
            | 10 ->
                ChallengeIndexFaults.inject a
                    (ReviewWorkflow.waitIndexPath challengePrefix task attemptId 0)
                    (ChallengeIndexFaults.indexBody task attemptId "Agent 3" 0)
                checkRejected "nonexistent-generation-zero" a "Integrity" resolveFirst
            | 11 ->
                duplicateCanonicalDependency a task attemptId first.ChallengeId
                checkRejected "duplicate-canonical-dependency" a "Integrity" resolveFirst
            | 12 ->
                ChallengeWorkflow.cancel a (Guid.NewGuid()) "Agent 3"
                    first.ChallengeId 1 "dependency withdrawn" |> ChallengeFixture.ok |> ignore
                let blocked = ReviewWorkflow.getAgentInbox b "Agent 3" |> ChallengeFixture.ok
                checkRejected "cancelled-dependency-not-runnable" a "Stale" (fun () ->
                    ChallengeWorkflow.requeueAttempt b (Guid.NewGuid()) "Agent 3" task attemptId
                        taskRecord.Version blocked.Blocked.Head.Attempt.Version blocked.Agent.Version 1
                        "must not wake cancellation")
                let stillBlocked = ReviewWorkflow.getAgentInbox a "Agent 3" |> ChallengeFixture.ok
                if stillBlocked.Blocked.Head.Attempt.AssignmentGeneration <> 1 then
                    counterexample "CanceledWaitGeneration" (sprintf "%A" stillBlocked)
            | 13 ->
                let wrong = { b with ProjectId = Guid.NewGuid() }
                checkRejected "wrong-project-binding" a "Integrity" (fun () ->
                    LedgerStore.fetch wrong)
            | 14 ->
                resolveFirst () |> ChallengeFixture.ok |> ignore
                let ready = ReviewWorkflow.getAgentInbox b "Agent 3" |> ChallengeFixture.ok
                let attemptN2 = ready.RunnableQueue.Head.Attempt
                ReviewWorkflow.beginAttempt b (Guid.NewGuid()) task "Agent 3"
                    attemptN2.Version 2 |> ChallengeFixture.ok |> ignore
                let started = ReviewWorkflow.getAgentInbox a "Agent 3" |> ChallengeFixture.ok
                let _, _, currentAttempt = ReviewWorkflow.getTask a task |> ChallengeFixture.ok
                ChallengeWorkflow.blockAttempt a (Guid.NewGuid()) "Agent 3" task attemptId
                    taskRecord.Version currentAttempt.Value.Version started.Agent.Version 2
                    "second wait with real historical first edge" [ second.ChallengeId ]
                |> ChallengeFixture.ok |> ignore
                ChallengeWorkflow.resolve b (Guid.NewGuid()) "Agent 8"
                    second.ChallengeId second.ConditionIds.Head 1 "approved" ""
                |> ChallengeFixture.ok |> ignore
                let after = ReviewWorkflow.getAgentInbox a "Agent 3" |> ChallengeFixture.ok
                if after.Agent.State <> "Ready"
                   || after.RunnableQueue.Head.Attempt.AssignmentGeneration <> 3 then
                    counterexample "ValidHistoricalRequeue" (sprintf "Expected N3, actual=%A" after)
                let snapshot = LedgerStore.fetch b |> ChallengeFixture.ok
                let found = LedgerStore.listAt b snapshot ownerPrefix |> ChallengeFixture.ok |> Set.ofList
                let expected =
                    [ ReviewWorkflow.waitIndexPath ownerPrefix task attemptId 1
                      ReviewWorkflow.waitIndexPath ownerPrefix task attemptId 2 ] |> Set.ofList
                if found <> expected then
                    counterexample "ValidHistoricalEdges" (sprintf "expected=%A actual=%A" expected found)
            | 15 ->
                ChallengeIndexFaults.inject a ownerEdge body
                let blocked = ReviewWorkflow.getAgentInbox b "Agent 3" |> ChallengeFixture.ok
                checkRejected "extra-owner-before-manual-requeue" a "Integrity" (fun () ->
                    ChallengeWorkflow.requeueAttempt b (Guid.NewGuid()) "Agent 3" task attemptId
                        taskRecord.Version blocked.Blocked.Head.Attempt.Version blocked.Agent.Version 1
                        "manual wait must validate reverse indices")
            | 16 ->
                resolveFirst () |> ChallengeFixture.ok |> ignore
                let third = ReviewWorkflow.waitIndexPath challengePrefix task attemptId 3
                ChallengeIndexFaults.inject a third
                    (ChallengeIndexFaults.indexBody task attemptId "Agent 3" 3)
                checkRejected "extra-future-edge-after-auto-requeue" a "Integrity" (readOwner "Agent 3")
            | 17 ->
                ChallengeIndexFaults.inject a challengeEdge
                    (ChallengeIndexFaults.indexBody task attemptId "Agent 3" 99)
                checkRejected "stale-generation-field" a "Integrity" resolveFirst
            | 18 ->
                ChallengeIndexFaults.inject a ownerEdge
                    (ChallengeIndexFaults.indexBody task attemptId "Intruder" 1)
                checkRejected "wrong-owner-field" a "Integrity" (readOwner "Agent 3")
            | 19 ->
                let otherPath = ReviewWorkflow.waitIndexPath challengePrefix
                                    "nonexistent-task" attemptId 1
                ChallengeIndexFaults.inject a otherPath body
                checkRejected "extra-wrong-task-locator" a "Integrity" resolveFirst
            | 20 ->
                let blocked = ReviewWorkflow.getAgentInbox b "Agent 3" |> ChallengeFixture.ok
                ChallengeWorkflow.requeueAttempt b (Guid.NewGuid()) "Agent 3" task attemptId
                    taskRecord.Version blocked.Blocked.Head.Attempt.Version blocked.Agent.Version 1
                    "valid manual zero-dependency requeue" |> ChallengeFixture.ok |> ignore
                let ready = ReviewWorkflow.getAgentInbox a "Agent 3" |> ChallengeFixture.ok
                if ready.Agent.State <> "Ready"
                   || ready.RunnableQueue.Head.Attempt.AssignmentGeneration <> 2 then
                    counterexample "ValidManualControl" (sprintf "No N1->N2 manual transition: %A" ready.Agent)
                ChallengeIndexFaults.inject a
                    (ReviewWorkflow.waitIndexPath ownerPrefix task attemptId 2)
                    (ChallengeIndexFaults.indexBody task attemptId "Agent 3" 2)
                checkRejected "orphan-owner-edge-after-manual-requeue" a "Integrity" (readOwner "Agent 3")
            | _ -> failwithf "Unexpected fault-matrix mode %d" mode)

[<TestFixture>]
type ChallengeReverseIndexFaultMatrixTests() =
    [<TestCase(4)>]
    [<TestCase(5)>]
    [<TestCase(6)>]
    [<TestCase(7)>]
    [<TestCase(8)>]
    [<TestCase(9)>]
    [<TestCase(10)>]
    [<TestCase(11)>]
    [<TestCase(12)>]
    [<TestCase(13)>]
    [<TestCase(14)>]
    [<TestCase(15)>]
    [<TestCase(16)>]
    [<TestCase(17)>]
    [<TestCase(18)>]
    [<TestCase(19)>]
    [<TestCase(20)>]
    member _.MissingExtraStaleCanceledWrongIdentityAndHistoricalModesFailClosed(mode: int) =
        ChallengeIndexFaultMatrix.probe mode
