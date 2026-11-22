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
                let before = LedgerStore.fetch a |> ChallengeFixture.ok
                let initialEvents = ChallengeWorkflow.get b "Agent 3" created.ChallengeId |> ChallengeFixture.ok
                let firstId = ChallengeModel.uuid seed "cas:first"
                let secondId = ChallengeModel.uuid seed "cas:second"
                let firstBinding, secondBinding =
                    if seed % 2 = 0 then a, b else b, a
                let first =
                    System.Threading.Tasks.Task.Run(fun () ->
                        ChallengeWorkflow.resolve firstBinding firstId "Lead"
                            created.ChallengeId created.ConditionIds.[0] 1 "review pass" "")
                let second =
                    System.Threading.Tasks.Task.Run(fun () ->
                        ChallengeWorkflow.resolve secondBinding secondId "Agent 8"
                            created.ChallengeId created.ConditionIds.[1] 1 "benchmark pass" "")
                System.Threading.Tasks.Task.WaitAll([| first :> System.Threading.Tasks.Task; second :> System.Threading.Tasks.Task |])
                let results =
                    [ firstId, "Lead", 0, first.Result
                      secondId, "Agent 8", 1, second.Result ]
                let winners = results |> List.filter (fun (_, _, _, result) -> Result.isOk result)
                let losers = results |> List.filter (fun (_, _, _, result) -> Result.isError result)
                if winners.Length <> 1 || losers.Length <> 1 then
                    failwithf "seed=%d CAS admitted wrong number of winners: %A" seed results
                let winnerId, winnerActor, winnerCondition, winnerResult = winners.Head
                let loserId, _, _, loserResult = losers.Head
                // The only independently authorized losing families are a changed
                // entity version and an explicitly exhausted CAS-contention retry.
                // Access denial, identity errors, integrity errors, uncertain push,
                // or any other unrelated Error must fail this test.
                match loserResult with
                | Error(StaleEntityVersion _) | Error LedgerContended -> ()
                | _ -> counterexample (sprintf "CASWrongLoserError#%d" seed)
                           (sprintf "expected stale-version/CAS contention; actual=%A" loserResult)
                let after = LedgerStore.fetch b |> ChallengeFixture.ok
                let winnerReceipt = LedgerStore.getOperationAt b after winnerId |> ChallengeFixture.ok
                let loserReceipt = LedgerStore.getOperationAt b after loserId |> ChallengeFixture.ok
                let applied = winnerResult |> ChallengeFixture.ok
                match winnerReceipt, loserReceipt with
                | Some committed, None when
                    committed.OperationId = winnerId.ToString("D")
                    && committed.Actor = winnerActor
                    && committed.Type = "ResolveChallengeCondition"
                    && committed.Sequence = before.Header.NextSequence
                    && applied.Operation.OperationId = committed.OperationId
                    && applied.Operation.RequestHash = committed.RequestHash
                    && applied.Operation.OriginalResultHash = committed.OriginalResultHash
                    && applied.CurrentHead = after.Commit -> ()
                | _ ->
                    counterexample (sprintf "CASReceiptMismatch#%d" seed)
                        (sprintf "winner=%A loser=%A result=%A" winnerReceipt loserReceipt applied)
                if before.Commit = after.Commit
                   || after.Header.NextSequence <> before.Header.NextSequence + 1L then
                    counterexample (sprintf "CASRemoteSequence#%d" seed)
                        (sprintf "before=%A after=%A" before after)
                let authoritative = ChallengeWorkflow.get a "Agent 3" created.ChallengeId
                                    |> ChallengeFixture.ok
                if authoritative.Challenge.Version <> 2
                   || authoritative.History.Length <> 2
                   || (authoritative.Challenge.Conditions
                       |> List.filter (fun item -> item.Status = "Satisfied")).Length <> 1
                   || authoritative.Challenge.Conditions.[winnerCondition].Status <> "Satisfied"
                   || (authoritative.History
                       |> List.map (fun event -> event.ActorId, event.Action))
                      <> [ "Agent 3", "Created"; winnerActor, "ResolveChallengeCondition" ]
                   || initialEvents.History.Length <> 1 then
                    failwithf "seed=%d did not preserve single-winner state" seed
                let eventPrefix = sprintf "challenges/%s/events" created.ChallengeId
                let eventPaths = LedgerStore.listAt b after eventPrefix |> ChallengeFixture.ok
                if eventPaths.Length <> 2
                   || not (List.contains (sprintf "%s/%d.json" eventPrefix before.Header.NextSequence) eventPaths) then
                    counterexample (sprintf "CASCanonicalAudit#%d" seed) (sprintf "%A" eventPaths)
                let indexBefore = LedgerStore.listAt a before (ReviewWorkflow.ownerWaitIndexPrefix "Agent 3") |> ChallengeFixture.ok
                let indexAfter = LedgerStore.listAt b after (ReviewWorkflow.ownerWaitIndexPrefix "Agent 3") |> ChallengeFixture.ok
                if indexBefore <> indexAfter
                   || not (LedgerStore.listAt b after "notifications" |> ChallengeFixture.ok |> List.isEmpty) then
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
    type LifecycleExpectation =
        { CommandNumber: int; ActorId: string; Action: string; Reason: string
          PreviousPhase: string; NewPhase: string
          PreviousGeneration: int; NewGeneration: int }
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
          Audits: LifecycleExpectation list
          Receipts: Map<int, Command> }

    let initial taskVersion attemptVersion agentVersion =
        { Gates = List.replicate 4 Open; GateVersions = List.replicate 4 1
          Phase = "Started"; Owner = "Working"
          Generation = 1; TaskVersion = taskVersion
          AttemptVersion = attemptVersion; AgentVersion = agentVersion
          WaitStatus = "Missing"; WaitVersion = 0; Dependencies = []
          Historical = []; Audits = []; Receipts = Map.empty }

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

    let className = function
        | WaitOn -> "WaitOn" | ManualRequeue -> "ManualRequeue"
        | StartAttempt -> "StartAttempt" | PassGate -> "PassGate"
        | BlockGate -> "BlockGate" | ReopenGate -> "ReopenGate"
        | CancelGate -> "CancelGate" | Observe -> "Observe"
        | Retry _ -> "Retry"

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
            let commit (next: State) =
                // Independent reference task lifecycle, including automatic
                // requeue initiated by a Challenge resolution.
                let lifecycleAction =
                    match call.Kind with
                    | WaitOn -> Some "WaitOnChallenges"
                    | ManualRequeue -> Some "RequeueChallengeBlockedAttempt"
                    | PassGate when next.Generation > state.Generation ->
                        Some "RequeueChallengeBlockedAttempt"
                    | _ -> None
                let audits =
                    match lifecycleAction with
                    | None -> state.Audits
                    | Some action ->
                        state.Audits @
                        [ { CommandNumber = opNumber; ActorId = actor call.Actor; Action = action
                            Reason =
                                match call.Kind with
                                | WaitOn -> "model-wait" + (if call.Variant then "-mutated" else "")
                                | ManualRequeue -> "model-requeue" + (if call.Variant then "-mutated" else "")
                                | PassGate -> "All explicitly linked challenges satisfied"
                                | _ -> failwith "Unexpected lifecycle command"
                            PreviousPhase = state.Phase; NewPhase = next.Phase
                            PreviousGeneration = state.Generation; NewGeneration = next.Generation } ]
                Applied, { next with Audits = audits; Receipts = Map.add opNumber call state.Receipts }
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
                // Guaranteed per-class transitions: positive, stale, unauthorized,
                // replay, automatic and manual requeue, and multi-gate contexts.
                // These same numbered commands run in bounded real-Git fixtures.
                | 8 -> BlockGate, 2, 1, []
                | 9 -> ReopenGate, 2, 0, []
                | 10 -> PassGate, 2, 3, [] // denied while gate is Open
                | 11 -> CancelGate, 3, 0, []
                | 12 -> WaitOn, 2, 0, [ 2; 3 ] // cancelled dependency
                | 13 -> WaitOn, 2, 0, [ 2 ]
                | 14 -> ManualRequeue, 2, 0, [] // pending dependency
                | 15 -> PassGate, 2, 1, [] // automatic N3 -> N4
                | 16 -> StartAttempt, 2, 0, []
                | 17 -> WaitOn, 0, 0, []
                | 18 -> ManualRequeue, 0, 0, [] // manual N4 -> N5
                | 19 -> StartAttempt, 0, 0, []
                | 20 -> WaitOn, 0, 0, [ 0; 1 ] // both satisfied
                | 21 -> Observe, 0, 0, []
                | 22 -> Retry(1, false), 0, 0, []
                | 23 -> Retry(1, true), 0, 0, []
                | 24 -> WaitOn, 0, 0, [ 0; 3 ] // partly cancelled
                | 25 -> PassGate, 0, 1, []
                | 26 -> BlockGate, 2, 1, []
                | 27 -> ReopenGate, 2, 0, []
                | 28 -> CancelGate, 3, 0, []
                | 29 -> ManualRequeue, 0, 0, []
                | 30 -> StartAttempt, 0, 0, []
                | 31 -> Retry(0, false), 0, 0, []
                | 32 -> Retry(0, true), 0, 0, []
                // Replay each mutating class after the canonical state advances.
                | 33 -> Retry(5, false), 0, 0, []  // WaitOn
                | 34 -> Retry(6, false), 0, 0, []  // ManualRequeue
                | 35 -> Retry(7, false), 0, 0, []  // StartAttempt
                | 36 -> Retry(8, false), 0, 0, []  // BlockGate
                | 37 -> Retry(9, false), 0, 0, []  // ReopenGate
                | 38 -> Retry(11, false), 0, 0, [] // CancelGate
                | 39 -> Retry(3, false), 0, 0, []  // PassGate
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
            let stale = number > 39 && rng.Next(7) = 0
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

    type IssueExpectation =
        { Sequence: int64; Issuer: string; Reason: string; Instructions: string }

    // No getTaskLifecycleHistory, AssignmentIssue deserializer or production
    // transition helper is used as an oracle. Read the actual immutable Git
    // blobs and compare their JSON fields to independently predicted records.
    let checkCanonical seed binding task attemptId (pinned: ReviewAttempt)
                       (state: State) (auditSequences: Map<int,int64>)
                       (issues: Map<int,IssueExpectation>) (frozen: Map<string,string>) =
        let snapshot = LedgerStore.fetch binding |> ChallengeFixture.ok
        let mutable verified = frozen
        let equals category field expected actual =
            if expected <> actual then
                counterexample category (sprintf "%s: expected=%A actual=%A" field expected actual)
        let raw path =
            match LedgerStore.readAt binding snapshot path |> ChallengeFixture.ok with
            | Some content, Some _ -> content
            | other -> counterexample "MixedMissingCanonicalBlob"
                           (sprintf "path=%s record=%A" path other)
        let inspect path verify =
            let content = raw path
            try
                use document = System.Text.Json.JsonDocument.Parse(content)
                verify document.RootElement
            with
            | :? ModelCounterexample -> reraise()
            | ex -> counterexample "MixedMalformedCanonicalBlob"
                        (sprintf "path=%s parse/field error=%s" path ex.Message)
            content
        let remember path content =
            match Map.tryFind path verified with
            | Some original when original <> content ->
                counterexample "MixedHistoricalBlobMutation" (sprintf "path=%s" path)
            | _ -> ()
            verified <- Map.add path content verified
        let str (record: System.Text.Json.JsonElement) (key: string) = record.GetProperty(key).GetString()
        let int (record: System.Text.Json.JsonElement) (key: string) = record.GetProperty(key).GetInt32()
        let int64 (record: System.Text.Json.JsonElement) (key: string) = record.GetProperty(key).GetInt64()
        let taskPrefix = sprintf "tasks/%s" task
        let attemptPrefix = sprintf "%s/attempts/%s" taskPrefix attemptId
        let pin category (record: System.Text.Json.JsonElement) isIssue =
            equals category "specBlobOid" pinned.SpecBlobOid (str record "specBlobOid")
            equals category "targetRef" pinned.TargetRef (str record "targetRef")
            equals category "sourceRepositoryId" pinned.SourceRepositoryId (str record "sourceRepositoryId")
            equals category "sourceRemote" pinned.SourceRemote (str record "sourceRemote")
            equals category "sourcePushRemote" pinned.SourcePushRemote (str record "sourcePushRemote")
            equals category "sourceRef" pinned.SourceRef (str record "sourceRef")
            equals category "baseCommit"
                pinned.BaseCommit (str record (if isIssue then "sourceBaseCommit" else "baseCommit"))
        let _, specOid = LedgerStore.readAt binding snapshot (taskPrefix + "/spec.html") |> ChallengeFixture.ok
        equals "MixedPinnedSpecBlob" "spec blob" (Some pinned.SpecBlobOid) specOid
        inspect (attemptPrefix + ".json") (fun record ->
            pin "MixedAttemptSourcePin" record false
            equals "MixedAttemptSourcePin" "taskId" task (str record "taskId")
            equals "MixedAttemptSourcePin" "attemptId" attemptId (str record "attemptId")
            equals "MixedAttemptSourcePin" "owner" "Agent 3" (str record "ownerAgentId")
            equals "MixedAttemptSourcePin" "generation" state.Generation (int record "assignmentGeneration")
            let issued = (Map.find state.Generation issues).Sequence
            equals "MixedAttemptSourcePin" "issued sequence" issued (int64 record "issuedSequence"))
        |> ignore
        let issuePrefix = attemptPrefix + "/assignments"
        // LedgerStore.listAt deliberately accepts only top-level allowlisted
        // roots. Enumerate "tasks" once, then independently select exact
        // canonical subpaths instead of requesting unsupported prefixes.
        let taskPaths = LedgerStore.listAt binding snapshot "tasks" |> ChallengeFixture.ok
        let actualIssues =
            taskPaths
            |> List.filter (fun p -> p.StartsWith(issuePrefix + "/", StringComparison.Ordinal)
                                      && p.EndsWith(".json", StringComparison.Ordinal)
                                      && not (p.EndsWith(".ack.json", StringComparison.Ordinal)))
            |> Set.ofList
        let expectedIssues =
            issues |> Map.toList
            |> List.map (fun (generation, _) -> sprintf "%s/%d.json" issuePrefix generation)
            |> Set.ofList
        equals "MixedAssignmentPathSet" "generation issue paths" expectedIssues actualIssues
        let generations = issues |> Map.toList |> List.map fst
        equals "MixedGenerationContinuity" "issued generations" [1 .. state.Generation] generations
        for KeyValue(generation, expected) in issues do
            let path = sprintf "%s/%d.json" issuePrefix generation
            let content = inspect path (fun record ->
                pin "MixedAssignmentSourcePin" record true
                equals "MixedAssignmentRecord" "task" task (str record "taskId")
                equals "MixedAssignmentRecord" "attempt" attemptId (str record "attemptId")
                equals "MixedAssignmentRecord" "generation" generation (int record "generation")
                equals "MixedAssignmentRecord" "prior generation" (generation - 1) (int record "previousGeneration")
                equals "MixedAssignmentRecord" "owner" "Agent 3" (str record "ownerAgentId")
                equals "MixedAssignmentRecord" "issuer" expected.Issuer (str record "issuerId")
                equals "MixedAssignmentRecord" "reason" expected.Reason (str record "reason")
                equals "MixedAssignmentRecord" "instructions" expected.Instructions (str record "instructions")
                equals "MixedAssignmentRecord" "sequence" expected.Sequence (int64 record "issuedSequence"))
            remember path content
        let auditPrefix = taskPrefix + "/lifecycle"
        let expectedAudits =
            state.Audits
            |> List.map (fun event ->
                let sequence =
                    match Map.tryFind event.CommandNumber auditSequences with
                    | Some value -> value
                    | None -> counterexample "MixedAuditReferenceSequence"
                                  (sprintf "No expected sequence for %A" event)
                sequence, event)
        let sequences = expectedAudits |> List.map fst
        equals "MixedAuditMonotonic" "event order" (List.sort sequences) sequences
        equals "MixedAuditMonotonic" "unique sequence" sequences (List.distinct sequences)
        let actualAudits =
            taskPaths
            |> List.filter (fun path -> path.StartsWith(auditPrefix + "/", StringComparison.Ordinal))
            |> Set.ofList
        let expectedPaths =
            expectedAudits
            |> List.map (fun (sequence, _) -> sprintf "%s/%d.json" auditPrefix sequence)
            |> Set.ofList
        equals "MixedLifecyclePathSet" "lifecycle events" expectedPaths actualAudits
        for sequence, expected in expectedAudits do
            let path = sprintf "%s/%d.json" auditPrefix sequence
            let content = inspect path (fun record ->
                equals "MixedLifecycleIdentity" "task" task (str record "taskId")
                equals "MixedLifecycleIdentity" "attempt" attemptId (str record "attemptId")
                equals "MixedLifecycleIdentity" "operation id"
                    ((ChallengeModel.uuid seed (sprintf "mixed:%d" expected.CommandNumber)).ToString("D"))
                    (str record "operationId")
                equals "MixedLifecycleIdentity" "sequence" sequence (int64 record "sequence")
                equals "MixedLifecycleActorAction" "actor" expected.ActorId (str record "actorId")
                equals "MixedLifecycleActorAction" "action" expected.Action (str record "action")
                equals "MixedLifecycleActorAction" "reason" expected.Reason (str record "reason")
                equals "MixedLifecycleTransition" "old task lifecycle" "InProgress" (str record "previousLifecycle")
                equals "MixedLifecycleTransition" "new task lifecycle" "InProgress" (str record "newLifecycle")
                equals "MixedLifecycleTransition" "old phase" expected.PreviousPhase (str record "previousPhase")
                equals "MixedLifecycleTransition" "new phase" expected.NewPhase (str record "newPhase")
                equals "MixedLifecycleTransition" "old owner" "Agent 3" (str record "previousOwnerId")
                equals "MixedLifecycleTransition" "new owner" "Agent 3" (str record "newOwnerId")
                equals "MixedLifecycleTransition" "old generation" expected.PreviousGeneration (int record "previousGeneration")
                equals "MixedLifecycleTransition" "new generation" expected.NewGeneration (int record "newGeneration")
                equals "MixedLifecycleTransition" "review disposition" "Unchanged" (str record "reviewDisposition"))
            remember path content
        verified

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
            let mutable auditSequences: Map<int,int64> = Map.empty
            let mutable issues =
                Map.ofList
                    [ 1, { Sequence = originalAttempt.IssuedSequence
                           Issuer = "Orchestrator"; Reason = "Assignment"
                           Instructions =
                               sprintf "Implement task %s against the pinned task specification." task } ]
            let mutable frozen: Map<string,string> = Map.empty
            let mutable realCoverage: Map<string * string,int> = Map.empty
            checkSnapshot a task attemptId gates state
            frozen <- checkCanonical seed a task attemptId originalAttempt state auditSequences issues frozen
            for command in commands do
                match command.Kind with
                | Observe ->
                    let before = LedgerStore.fetch a |> ChallengeFixture.ok
                    checkSnapshot b task attemptId gates state
                    let unchanged = checkCanonical seed b task attemptId originalAttempt state auditSequences issues frozen
                    let after = LedgerStore.fetch a |> ChallengeFixture.ok
                    if before.Commit <> after.Commit || before.Header.NextSequence <> after.Header.NextSequence
                       || unchanged <> frozen then
                        counterexample (sprintf "MixedReadMutation#%d" command.Number)
                            "Read changed canonical HEAD, sequence or historical records"
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
                        let outcomeName =
                            match observed with Applied -> "Applied" | Replayed -> "Replayed" | Rejected _ -> "Rejected"
                        let record name =
                            let key = name, outcomeName
                            let count = realCoverage |> Map.tryFind key |> Option.defaultValue 0
                            realCoverage <- realCoverage |> Map.add key (count + 1)
                        record (className call.Kind)
                        match command.Kind with Retry _ -> record "Retry" | _ -> ()
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
                        if observed = Applied then
                            if next.Audits.Length = state.Audits.Length + 1 then
                                auditSequences <- auditSequences |> Map.add original before.Header.NextSequence
                            elif next.Audits.Length <> state.Audits.Length then
                                counterexample "MixedAuditModelTransition" (sprintf "old=%A next=%A" state.Audits next.Audits)
                            if next.Generation > state.Generation then
                                if next.Generation <> state.Generation + 1 then
                                    counterexample "MixedGenerationJump" (sprintf "%d -> %d" state.Generation next.Generation)
                                let text =
                                    if call.Kind = ManualRequeue then
                                        "model-requeue" + (if call.Variant then "-mutated" else "")
                                    else "All explicitly linked challenges satisfied"
                                issues <- issues |> Map.add next.Generation
                                    { Sequence = before.Header.NextSequence
                                      Issuer = actor call.Actor; Reason = "ChallengeUnblock"
                                      Instructions = text }
                        let beforeFrozen = frozen
                        state <- next
                        checkSnapshot other task attemptId gates state
                        frozen <- checkCanonical seed other task attemptId originalAttempt state auditSequences issues frozen
                        if observed <> Applied && frozen <> beforeFrozen then
                            counterexample "MixedRejectedHistoricalMutation"
                                (sprintf "step=%A old=%A current=%A" command beforeFrozen frozen)
            if originalTask.TaskId <> task || originalAttempt.AttemptId <> attemptId then
                failwith "Fixture identity drift"
            realCoverage)

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
                    try runCommands seed candidate |> ignore; false
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
        let mutable coverage: Map<string * string,int> = Map.empty
        let mutable typedRejects: Map<string * FailureFamily,int> = Map.empty
        let mutable contexts: Set<string> = Set.empty
        let mutable observations = 0
        let incr key (counts: Map<_,int>) =
            counts |> Map.add key ((counts |> Map.tryFind key |> Option.defaultValue 0) + 1)
        let count kind outcome = coverage |> Map.tryFind (kind, outcome) |> Option.defaultValue 0
        for seed in 1 .. 192 do
            let commands = MixedChallengeAttemptModel.generate seed 56 (MixedChallengeAttemptModel.initial 2 2 5)
            let mutable state = MixedChallengeAttemptModel.initial 2 2 5
            let mutable attempted = Map.empty
            for command in commands do
                match command.Kind with
                | MixedChallengeAttemptModel.Observe ->
                    observations <- observations + 1
                | _ ->
                    if (match command.Kind with MixedChallengeAttemptModel.Retry _ -> false | _ -> true) then
                        attempted <- attempted |> Map.add command.Number command
                    match MixedChallengeAttemptModel.materialize attempted command with
                    | None -> ()
                    | Some(call, original) ->
                        let before = state
                        let outcome, next = MixedChallengeAttemptModel.apply state original call
                        state <- next
                        let kind = MixedChallengeAttemptModel.className call.Kind
                        let outcomeName =
                            match outcome with Applied -> "Applied" | Replayed -> "Replayed" | Rejected _ -> "Rejected"
                        coverage <- incr (kind, outcomeName) coverage
                        match command.Kind with
                        | MixedChallengeAttemptModel.Retry _ ->
                            coverage <- incr ("Retry", outcomeName) coverage
                        | _ -> ()
                        match outcome with
                        | Rejected family ->
                            typedRejects <- incr (kind, family) typedRejects
                            match command.Kind with
                            | MixedChallengeAttemptModel.Retry _ ->
                                typedRejects <- incr ("Retry", family) typedRejects
                            | _ -> ()
                        | _ -> ()
                        if before.Generation > 1 && outcome = Applied then
                            contexts <- Set.add "accepted-cross-generation" contexts
                        if call.Kind = MixedChallengeAttemptModel.WaitOn
                           && call.Dependencies.Length > 1 && outcome = Applied then
                            contexts <- Set.add "multi-dependency-wait" contexts
                        if call.Kind = MixedChallengeAttemptModel.ManualRequeue
                           && before.Dependencies.Length > 1 && outcome <> Applied then
                            contexts <- Set.add "partially-satisfied-manual-rejection" contexts
                        if call.Kind = MixedChallengeAttemptModel.PassGate
                           && outcome = Applied && next.Generation > before.Generation then
                            contexts <- Set.add "automatic-requeue" contexts
                        if call.Kind = MixedChallengeAttemptModel.ManualRequeue
                           && outcome = Applied && next.Generation > before.Generation then
                            contexts <- Set.add "manual-requeue" contexts
                        if not (List.isEmpty call.Dependencies)
                           && (call.Dependencies
                               |> List.exists (fun i -> before.Gates.[i] = MixedChallengeAttemptModel.Abandoned))
                           && outcome <> Applied then
                            contexts <- Set.add "cancelled-dependency-rejection" contexts
                        if call.Actor = 3 && outcome = Rejected Denied then
                            contexts <- Set.add "typed-unauthorized-rejection" contexts
                        if outcome = Rejected Stale &&
                           (call.ChallengeVersion <> before.GateVersions.[call.Gate]
                            || call.AttemptVersion <> before.AttemptVersion) then
                            contexts <- Set.add "typed-version-staleness" contexts
        let actionable =
            [ "WaitOn"; "ManualRequeue"; "StartAttempt"; "PassGate"
              "BlockGate"; "ReopenGate"; "CancelGate" ]
        for kind in actionable do
            Assert.That(count kind "Applied", Is.GreaterThan(0), kind + " lacks accepted path")
            Assert.That(count kind "Rejected", Is.GreaterThan(0), kind + " lacks rejected path")
            Assert.That(count kind "Replayed", Is.GreaterThan(0), kind + " lacks identical receipt replay")
        Assert.That(count "Retry" "Replayed", Is.GreaterThan(0), "No immutable operation replay")
        Assert.That(count "Retry" "Rejected", Is.GreaterThan(0), "No mutated-UUID rejection")
        Assert.That(observations, Is.GreaterThan(0), "Observation-only steps were not exercised")
        Assert.That(typedRejects |> Map.tryFind ("PassGate", Denied) |> Option.defaultValue 0,
                    Is.GreaterThan(0), "Unauthorized gate call did not reach typed denial")
        Assert.That(typedRejects |> Map.tryFind ("Retry", Reused) |> Option.defaultValue 0,
                    Is.GreaterThan(0), "Mutated replay did not reach typed UUID rejection")
        for context in
            [ "accepted-cross-generation"; "multi-dependency-wait"
              "partially-satisfied-manual-rejection"; "automatic-requeue"
              "manual-requeue"; "cancelled-dependency-rejection"
              "typed-unauthorized-rejection"; "typed-version-staleness" ] do
            Assert.That(Set.contains context contexts, Is.True, "Missing model precondition: " + context)
        // Creation has an independently controlled operation UUID: the same
        // create must apply, replay identically, and reject a changed payload.
        let mutable creationState = ChallengeModel.empty
        let creation =
            [ NewChallenge(0, false); NewChallenge(0, false); NewChallenge(0, true) ]
            |> List.map (fun request ->
                let outcome, next = ChallengeModel.apply creationState "create:0" request
                creationState <- next
                outcome)
        Assert.That((creation = [ Applied; Replayed; Rejected Reused ]), Is.True,
                    sprintf "Unexpected create/replay/changed-UUID outcomes: %A" creation)

    [<Test>]
    member _.GeneratedTwoCloneDependencyTracesMatchTaskWaitGenerationAndIndexModel() =
        let mutable coverage: Map<string * string,int> = Map.empty
        for seed in [ 101; 102; 103; 104 ] do
            let commands =
                MixedChallengeAttemptFixture.prepare (fun _ _ _ _ _ _ _ agentState ->
                    MixedChallengeAttemptModel.generate seed 56 agentState)
            try
                let observed = MixedChallengeAttemptFixture.runCommands seed commands
                for KeyValue(key, count) in observed do
                    coverage <- coverage |> Map.add key
                        (count + (coverage |> Map.tryFind key |> Option.defaultValue 0))
            with :? ModelCounterexample as original ->
                let shrunk, attempts = MixedChallengeAttemptFixture.shrink seed commands original
                Assert.Fail(
                    sprintf "Mixed dependency model source=%s seed=%d category=%s original=%A shrinkAttempts=%d minimal=%A detail=%s"
                        (ChallengeFixture.sourceSha()) seed original.Category commands attempts shrunk original.Message)
        let count name status = coverage |> Map.tryFind (name, status) |> Option.defaultValue 0
        for name in [ "WaitOn"; "ManualRequeue"; "StartAttempt"; "PassGate"
                      "BlockGate"; "ReopenGate"; "CancelGate" ] do
            Assert.That(count name "Applied", Is.GreaterThan(0), "Real Git missing accepted " + name)
            Assert.That(count name "Rejected", Is.GreaterThan(0), "Real Git missing rejected " + name)
            Assert.That(count name "Replayed", Is.GreaterThan(0), "Real Git missing replay of " + name)
        Assert.That(count "Retry" "Replayed", Is.GreaterThan(0), "Real Git missing replay")
        Assert.That(count "Retry" "Rejected", Is.GreaterThan(0), "Real Git missing changed-payload rejection")


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
            elif mode = 24 || mode = 25 then block [ first.ChallengeId; second.ChallengeId ]
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
                // Unknown Challenge prefixes are a bounded-reader discovery
                // non-goal. The normal inbox may return its blocked assignment;
                // it must not perform writes or emit audit/notification records.
                let abandonedPrefix =
                    ReviewWorkflow.challengeWaitIndexPrefix (Guid.NewGuid().ToString("N"))
                ChallengeIndexFaults.inject a
                    (ReviewWorkflow.waitIndexPath abandonedPrefix task attemptId 1) body
                let before = LedgerStore.fetch b |> ChallengeFixture.ok
                let beforeHead = remoteHead b
                let beforeTree = ChallengeFixture.git b.RepositoryPath [ "ls-tree"; "-r"; before.Commit ]
                match ReviewWorkflow.getAgentInbox b "Agent 3" with
                | Ok inbox when inbox.Blocked |> List.exists (fun entry -> entry.Attempt.AssignmentGeneration = 1) -> ()
                | Ok inbox ->
                    counterexample "UnknownChallengeBoundedInbox"
                        (sprintf "Expected the existing N1 blocked assignment, got %A" inbox)
                // A future reader may detect this orphan; that is permitted,
                // not an integrity failure required by the bounded contract.
                | Error(LedgerIntegrityError _) -> ()
                | Error error ->
                    counterexample "UnknownChallengeBoundedInbox"
                        (sprintf "Bounded read unexpectedly failed: %A" error)
                let after = LedgerStore.fetch b |> ChallengeFixture.ok
                let afterHead = remoteHead b
                let afterTree = ChallengeFixture.git b.RepositoryPath [ "ls-tree"; "-r"; after.Commit ]
                // The complete Git tree includes audit, receipt, and notice
                // records: matching tree and HEAD proves no reader-side effects.
                if beforeHead <> afterHead || before.Commit <> after.Commit
                   || before.Header.NextSequence <> after.Header.NextSequence
                   || beforeTree <> afterTree then
                    counterexample "UnknownChallengeBoundedReaderMutation"
                        (sprintf "HEAD=%s->%s sequence=%d->%d treeUnchanged=%b"
                            beforeHead afterHead before.Header.NextSequence
                            after.Header.NextSequence (beforeTree = afterTree))
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
                // Pin clean-ledger versions before corrupting the owner index.
                // No post-injection inbox preflight may mask the public writer.
                let _, pristineTask, pristineAttempt = ReviewWorkflow.getTask b task |> ChallengeFixture.ok
                let pristineInbox = ReviewWorkflow.getAgentInbox b "Agent 3" |> ChallengeFixture.ok
                let pinnedAttempt = pristineAttempt.Value
                if pristineTask.Version <> taskRecord.Version
                   || pinnedAttempt.AssignmentGeneration <> 1
                   || pristineInbox.Blocked.Head.Attempt.AttemptId <> attemptId then
                    counterexample "Mode15PristineVersions"
                        (sprintf "task=%A attempt=%A inbox=%A"
                            pristineTask pinnedAttempt pristineInbox)
                let taskVersion = pristineTask.Version
                let attemptVersion = pinnedAttempt.Version
                let agentVersion = pristineInbox.Agent.Version
                ChallengeIndexFaults.inject a ownerEdge body
                let corrupted = LedgerStore.fetch a |> ChallengeFixture.ok
                let beforeTree = ChallengeFixture.git a.RepositoryPath
                                    [ "ls-tree"; "-r"; corrupted.Commit ]
                let beforeOwner = LedgerStore.listAt a corrupted ownerPrefix |> ChallengeFixture.ok
                if beforeOwner <> [ ownerEdge ] then
                    counterexample "Mode15CorruptionMissing"
                        (sprintf "expected forged edge=%s actual=%A" ownerEdge beforeOwner)
                checkRejected "extra-owner-before-manual-requeue" a "Integrity" (fun () ->
                    ChallengeWorkflow.requeueAttempt b (Guid.NewGuid()) "Agent 3" task attemptId
                        taskVersion attemptVersion agentVersion 1
                        "manual wait must validate reverse indices")
                let unchanged = LedgerStore.fetch b |> ChallengeFixture.ok
                let afterTree = ChallengeFixture.git b.RepositoryPath
                                    [ "ls-tree"; "-r"; unchanged.Commit ]
                let afterOwner = LedgerStore.listAt b unchanged ownerPrefix |> ChallengeFixture.ok
                // Exact Git tree equality covers lifecycle audit, receipts,
                // wait state, assignment JSON and every reverse index path.
                if corrupted.Commit <> unchanged.Commit
                   || corrupted.Header.NextSequence <> unchanged.Header.NextSequence
                   || beforeTree <> afterTree || beforeOwner <> afterOwner then
                    counterexample "Mode15RejectedMutation"
                        (sprintf "before=%s/%d after=%s/%d owner=%A -> %A"
                            corrupted.Commit corrupted.Header.NextSequence
                            unchanged.Commit unchanged.Header.NextSequence beforeOwner afterOwner)
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
            | 21 ->
                // Identity matters independently of the project UUID and task locator.
                let wrong = { b with LedgerId = Guid.NewGuid() }
                checkRejected "wrong-ledger-identity" a "Integrity" (fun () -> LedgerStore.fetch wrong)
            | 22 ->
                let wrong = { b with Epoch = b.Epoch + 1 }
                checkRejected "wrong-ledger-epoch" a "Integrity" (fun () -> LedgerStore.fetch wrong)
            | 23 ->
                // The same local clone cannot silently bind a different remote/ref
                // to the approved canonical ledger.
                let wrong = { b with RemoteName = "unapproved-remote" }
                checkRejected "wrong-approved-remote-ref" a "Invalid" (fun () -> LedgerStore.fetch wrong)
            | 24 | 25 ->
                let waiting = sprintf "tasks/%s/attempts/%s/challenge-wait.json" task attemptId
                let snapshotBefore = LedgerStore.fetch a |> ChallengeFixture.ok
                let ownerBefore = LedgerStore.listAt a snapshotBefore ownerPrefix |> ChallengeFixture.ok
                let firstBefore =
                    LedgerStore.listAt a snapshotBefore (ReviewWorkflow.challengeWaitIndexPrefix first.ChallengeId)
                    |> ChallengeFixture.ok
                let secondBefore =
                    LedgerStore.listAt a snapshotBefore (ReviewWorkflow.challengeWaitIndexPrefix second.ChallengeId)
                    |> ChallengeFixture.ok
                if ownerBefore <> [ ownerEdge ] || firstBefore <> [ challengeEdge ]
                   || secondBefore <>
                        [ ReviewWorkflow.waitIndexPath
                            (ReviewWorkflow.challengeWaitIndexPrefix second.ChallengeId) task attemptId 1 ] then
                    counterexample "MultiDependencyEdgesMissing" (sprintf "mode=%d" mode)
                if mode = 25 then
                    resolveFirst () |> ChallengeFixture.ok |> ignore
                let canceled = if mode = 24 then first else second
                ChallengeWorkflow.cancel b (Guid.NewGuid()) "Agent 3"
                    canceled.ChallengeId 1 "partial dependency canceled"
                |> ChallengeFixture.ok |> ignore
                let beforeReject = LedgerStore.fetch a |> ChallengeFixture.ok
                let waitBefore, _ = LedgerStore.readAt a beforeReject waiting |> ChallengeFixture.ok
                let blocked = ReviewWorkflow.getAgentInbox b "Agent 3" |> ChallengeFixture.ok
                checkRejected
                    (if mode = 24 then "cancelled-one-other-pending"
                     else "cancelled-one-other-satisfied")
                    a "Stale" (fun () ->
                    ChallengeWorkflow.requeueAttempt b (Guid.NewGuid()) "Agent 3" task attemptId
                        taskRecord.Version blocked.Blocked.Head.Attempt.Version blocked.Agent.Version 1
                        "partial canceled dependency must never wake")
                let afterReject = LedgerStore.fetch b |> ChallengeFixture.ok
                let waitAfter, _ = LedgerStore.readAt b afterReject waiting |> ChallengeFixture.ok
                let ownerAfter = LedgerStore.listAt b afterReject ownerPrefix |> ChallengeFixture.ok
                let firstAfter =
                    LedgerStore.listAt b afterReject (ReviewWorkflow.challengeWaitIndexPrefix first.ChallengeId)
                    |> ChallengeFixture.ok
                let secondAfter =
                    LedgerStore.listAt b afterReject (ReviewWorkflow.challengeWaitIndexPrefix second.ChallengeId)
                    |> ChallengeFixture.ok
                let finalInbox = ReviewWorkflow.getAgentInbox a "Agent 3" |> ChallengeFixture.ok
                if beforeReject.Commit <> afterReject.Commit
                   || beforeReject.Header.NextSequence <> afterReject.Header.NextSequence
                   || waitBefore <> waitAfter || ownerBefore <> ownerAfter
                   || firstBefore <> firstAfter || secondBefore <> secondAfter
                   || finalInbox.Blocked.Head.Attempt.AssignmentGeneration <> 1 then
                    counterexample "PartialCanceledDependencyMutation"
                        (sprintf "mode=%d before=%s after=%s" mode beforeReject.Commit afterReject.Commit)
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
    [<TestCase(21)>]
    [<TestCase(22)>]
    [<TestCase(23)>]
    [<TestCase(24)>]
    [<TestCase(25)>]
    member _.MissingExtraStaleCanceledWrongIdentityAndHistoricalModesFailClosed(mode: int) =
        ChallengeIndexFaultMatrix.probe mode


// Independent real-Git control for *nonempty* frozen source identity. The
// initial legacy assignment is necessarily null-pinned; bindAttemptSource is
// the public API that supplies genuine source identity after BeginAttempt.
// Its first subsequently issued assignment and all later issues must retain
// the exact verified pins. This complements, rather than replaces, the
// generated legacy/null-source mixed model above.
module private PinnedSourceAssignmentOracle =
    let run () =
        ChallengeFixture.withTwoClones (fun a b ->
            let task = "pinned-source-assignment-oracle"
            let originalTask, originalAttempt, _, attemptId = ChallengeDependencyChecks.assign a task
            let sourceRef = ChallengeFixture.git b.RepositoryPath [ "symbolic-ref"; "HEAD" ]
            let baseCommit = ChallengeFixture.git b.RepositoryPath [ "rev-parse"; "HEAD" ]
            let targetRef = sourceRef
            let repositoryId =
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(
                        "jarvis-source-repository-v1\n" + a.ApprovedFetchUrl + "\n" + a.ApprovedPushUrl))
                |> Convert.ToHexString
                |> fun hash -> hash.ToLowerInvariant()
            if baseCommit.Length <> 40 || not (sourceRef.StartsWith("refs/heads/", StringComparison.Ordinal))
               || String.IsNullOrWhiteSpace(repositoryId) then
                counterexample "PinnedFixtureSourceIdentity"
                    (sprintf "base=%s source=%s repo=%s" baseCommit sourceRef repositoryId)
            let attemptPath = sprintf "tasks/%s/attempts/%s.json" task attemptId
            let issuePrefix = sprintf "tasks/%s/attempts/%s/assignments" task attemptId
            let issuePath generation = sprintf "%s/%d.json" issuePrefix generation
            let raw binding snapshot path =
                match LedgerStore.readAt binding snapshot path |> ChallengeFixture.ok with
                | Some content, Some _ -> content
                | value -> counterexample "PinnedCanonicalMissing"
                               (sprintf "path=%s value=%A" path value)
            let equal field expected actual =
                if expected <> actual then
                    counterexample "PinnedSourceIdentity"
                        (sprintf "field=%s expected=%A actual=%A" field expected actual)
            let mutable historic: Map<int,string> = Map.empty
            let checkCanonical maxGeneration =
                let snapshot = LedgerStore.fetch b |> ChallengeFixture.ok
                let _, actualSpecOid =
                    LedgerStore.readAt b snapshot (sprintf "tasks/%s/spec.html" task)
                    |> ChallengeFixture.ok
                equal "canonical spec blob" (Some originalAttempt.SpecBlobOid) actualSpecOid
                let attemptText = raw b snapshot attemptPath
                use attemptDoc = System.Text.Json.JsonDocument.Parse(attemptText)
                let attempt = attemptDoc.RootElement
                let str (node: System.Text.Json.JsonElement) (key: string) = node.GetProperty(key).GetString()
                let num (node: System.Text.Json.JsonElement) (key: string) = node.GetProperty(key).GetInt32()
                let seq (node: System.Text.Json.JsonElement) (key: string) = node.GetProperty(key).GetInt64()
                let pins prefix (node: System.Text.Json.JsonElement) isIssue sourcePinned =
                    let value whenPinned = if sourcePinned then whenPinned else null
                    equal (prefix + " base") (value baseCommit)
                        (str node (if isIssue then "sourceBaseCommit" else "baseCommit"))
                    equal (prefix + " target") (value targetRef) (str node "targetRef")
                    equal (prefix + " repository") (value repositoryId) (str node "sourceRepositoryId")
                    equal (prefix + " fetch") (value a.ApprovedFetchUrl) (str node "sourceRemote")
                    equal (prefix + " push") (value a.ApprovedPushUrl) (str node "sourcePushRemote")
                    equal (prefix + " ref") (value sourceRef) (str node "sourceRef")
                    equal (prefix + " spec") originalAttempt.SpecBlobOid (str node "specBlobOid")
                pins "current attempt" attempt false true
                equal "attempt task" task (str attempt "taskId")
                equal "attempt ID" attemptId (str attempt "attemptId")
                equal "attempt owner" "Agent 3" (str attempt "ownerAgentId")
                equal "attempt generation" maxGeneration (num attempt "assignmentGeneration")
                let taskPaths = LedgerStore.listAt b snapshot "tasks" |> ChallengeFixture.ok
                let found =
                    taskPaths
                    |> List.filter (fun path -> path.StartsWith(issuePrefix + "/", StringComparison.Ordinal)
                                               && path.EndsWith(".json", StringComparison.Ordinal)
                                               && not (path.EndsWith(".ack.json", StringComparison.Ordinal)))
                    |> Set.ofList
                let expected = [ 1 .. maxGeneration ] |> List.map issuePath |> Set.ofList
                equal "canonical assignment path set" expected found
                let mutable lastSequence = 0L
                for generation in 1 .. maxGeneration do
                    let contents = raw b snapshot (issuePath generation)
                    use document = System.Text.Json.JsonDocument.Parse(contents)
                    let issue = document.RootElement
                    // Generation 1 precedes public source binding. N2 is the
                    // first actual source-pinned *issuance*, not a fabricated N1.
                    pins (sprintf "issue%d" generation) issue true (generation > 1)
                    equal "issue task" task (str issue "taskId")
                    equal "issue attempt" attemptId (str issue "attemptId")
                    equal "issue owner" "Agent 3" (str issue "ownerAgentId")
                    equal "issue generation" generation (num issue "generation")
                    equal "issue previous generation" (generation - 1) (num issue "previousGeneration")
                    let issued = seq issue "issuedSequence"
                    if issued <= lastSequence then
                        counterexample "PinnedIssueSequence" (sprintf "generation=%d issued=%d previous=%d"
                                                                  generation issued lastSequence)
                    lastSequence <- issued
                    match Map.tryFind generation historic with
                    | Some bytes -> equal (sprintf "immutable issue%d bytes" generation) bytes contents
                    | None -> historic <- Map.add generation contents historic
                snapshot
            let assertUnchanged label before =
                let after = LedgerStore.fetch a |> ChallengeFixture.ok
                let firstTree = ChallengeFixture.git a.RepositoryPath [ "ls-tree"; "-r"; before.Commit ]
                let secondTree = ChallengeFixture.git a.RepositoryPath [ "ls-tree"; "-r"; after.Commit ]
                if before.Commit <> after.Commit
                   || before.Header.NextSequence <> after.Header.NextSequence
                   || firstTree <> secondTree then
                    counterexample "PinnedRejectedMutation"
                        (sprintf "%s HEAD=%s->%s seq=%d->%d"
                            label before.Commit after.Commit
                            before.Header.NextSequence after.Header.NextSequence)
            let versions () =
                let _, currentTask, maybeAttempt = ReviewWorkflow.getTask b task |> ChallengeFixture.ok
                let inbox = ReviewWorkflow.getAgentInbox b "Agent 3" |> ChallengeFixture.ok
                currentTask.Version, maybeAttempt.Value.Version, inbox.Agent.Version
            let afterCreation = LedgerStore.fetch b |> ChallengeFixture.ok
            let legacyIssue = raw b afterCreation (issuePath 1)
            ReviewWorkflow.bindAttemptSource b (Guid.NewGuid()) task "Agent 3"
                originalAttempt.Version 1 sourceRef baseCommit targetRef false false
            |> ChallengeFixture.ok |> ignore
            let _, _, boundAttempt = ReviewWorkflow.getTask a task |> ChallengeFixture.ok
            equal "public binding increments attempt version" (originalAttempt.Version + 1) boundAttempt.Value.Version
            equal "initial legacy issue immutable on binding" legacyIssue
                (raw a (LedgerStore.fetch a |> ChallengeFixture.ok) (issuePath 1))
            checkCanonical 1 |> ignore
            let block generation dependencies =
                let taskVersion, attemptVersion, agentVersion = versions ()
                ChallengeWorkflow.blockAttempt a (Guid.NewGuid()) "Agent 3" task attemptId
                    taskVersion attemptVersion agentVersion generation
                    "pinned source wait" dependencies
                |> ChallengeFixture.ok |> ignore
            let requeue operationId generation reason =
                let taskVersion, attemptVersion, agentVersion = versions ()
                ChallengeWorkflow.requeueAttempt b operationId "Agent 3" task attemptId
                    taskVersion attemptVersion agentVersion generation reason
                |> ChallengeFixture.ok
            block 1 []
            checkCanonical 1 |> ignore
            let firstManualId = Guid.NewGuid()
            let manualTaskVersion, manualAttemptVersion, manualAgentVersion = versions ()
            let manualCall reason =
                ChallengeWorkflow.requeueAttempt b firstManualId "Agent 3" task attemptId
                    manualTaskVersion manualAttemptVersion manualAgentVersion 1 reason
            let manualN2 = manualCall "pinned manual N1 to N2" |> ChallengeFixture.ok
            if manualN2.AlreadyApplied then counterexample "PinnedManualN2" "Fresh manual requeue replayed"
            checkCanonical 2 |> ignore
            let beforeReplay = LedgerStore.fetch a |> ChallengeFixture.ok
            let manualReplay = manualCall "pinned manual N1 to N2" |> ChallengeFixture.ok
            if not manualReplay.AlreadyApplied
               || manualReplay.Operation.RequestHash <> manualN2.Operation.RequestHash
               || manualReplay.Operation.Sequence <> manualN2.Operation.Sequence then
                counterexample "PinnedManualReplay" (sprintf "initial=%A replay=%A" manualN2 manualReplay)
            assertUnchanged "identical manual UUID replay" beforeReplay
            let beforeChanged = LedgerStore.fetch a |> ChallengeFixture.ok
            let staleArgs = manualCall "pinned manual N1 to N2 changed"
            match staleArgs with
            | Error OperationIdReused -> ()
            | other -> counterexample "PinnedChangedUuidType" (sprintf "%A" other)
            assertUnchanged "changed manual UUID reused" beforeChanged
            checkCanonical 2 |> ignore
            let gate = ChallengeDependencyChecks.gate b task "Pinned automatic gate" "Lead"
            // The other clone deliberately advances the remote HEAD before
            // this writer starts and subsequently blocks generation 2.
            checkCanonical 2 |> ignore
            let _, _, queuedN2 = ReviewWorkflow.getTask a task |> ChallengeFixture.ok
            ReviewWorkflow.beginAttempt a (Guid.NewGuid()) task "Agent 3"
                queuedN2.Value.Version 2 |> ChallengeFixture.ok |> ignore
            block 2 [ gate.ChallengeId ]
            checkCanonical 2 |> ignore
            let beforeAuto = LedgerStore.fetch a |> ChallengeFixture.ok
            ChallengeWorkflow.resolve b (Guid.NewGuid()) "Lead"
                gate.ChallengeId gate.ConditionIds.Head 1 "source pinned approved" ""
            |> ChallengeFixture.ok |> ignore
            let afterAuto = checkCanonical 3
            if afterAuto.Header.NextSequence <> beforeAuto.Header.NextSequence + 1L then
                counterexample "PinnedAutoSequence" (sprintf "%d -> %d"
                                                      beforeAuto.Header.NextSequence afterAuto.Header.NextSequence)
            let _, _, queuedN3 = ReviewWorkflow.getTask b task |> ChallengeFixture.ok
            ReviewWorkflow.beginAttempt a (Guid.NewGuid()) task "Agent 3"
                queuedN3.Value.Version 3 |> ChallengeFixture.ok |> ignore
            block 3 []
            let taskVersion, attemptVersion, agentVersion = versions ()
            let beforeStale = LedgerStore.fetch a |> ChallengeFixture.ok
            let staleCas =
                ChallengeWorkflow.requeueAttempt b (Guid.NewGuid()) "Agent 3" task attemptId
                    taskVersion (attemptVersion + 1) agentVersion 3 "stale pinned CAS loser"
            match staleCas with
            | Error(StaleEntityVersion _) -> ()
            | other -> counterexample "PinnedStaleCasType" (sprintf "%A" other)
            assertUnchanged "stale direct CAS" beforeStale
            checkCanonical 3 |> ignore
            let lastManual = requeue (Guid.NewGuid()) 3 "pinned manual N3 to N4"
            if lastManual.AlreadyApplied then
                counterexample "PinnedManualN4" "Fresh manual requeue replayed"
            checkCanonical 4 |> ignore
            equal "source pinned attempt identity" originalTask.TaskId task
            // The final verification re-reads all four immutable generation
            // issue blobs, including the legacy-null control, from clone B.
            checkCanonical 4 |> ignore)

[<TestFixture>]
type SourcePinnedChallengeAssignmentPropertyTests() =
    [<Test>]
    member _.TwoClonePublicBindingPreservesSourcePinsThroughManualAndAutomaticRequeue() =
        PinnedSourceAssignmentOracle.run ()
