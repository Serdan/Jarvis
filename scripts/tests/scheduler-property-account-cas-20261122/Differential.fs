
// Bounded seeded native differential checks. Compiled after the exact frozen fixture.
// No simulator result is accepted as a native CAS result.
module private NativeCasDifferential =
    type Command =
        | Reserve of int
        | ReplayReserve of int
        | MutateReplay of int
        | DuplicateCanonical of int
        | Transition of int * string
        | ForgedRelease of int * string
        | Release of int
        | ReplayRelease of int
    type Outcome =
        | Committed
        | Replayed
        | PolicyDenied
        | Full of int * int
        | Reused
    type Entry =
        { Index: int; OriginalOperationId: Guid; Status: string
          Generation: int64; TerminalOperationId: Guid option }

    let deterministicGuid seed salt =
        let bytes =
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(sprintf "account-cas-native-v1/%d/%d" seed salt))
            |> Array.take 16
        bytes[7] <- (bytes[7] &&& 0x0fuy) ||| 0x40uy
        bytes[8] <- (bytes[8] &&& 0x3fuy) ||| 0x80uy
        Guid(bytes)

    let generate seed =
        let rng = Random(seed)
        let firstWorker = rng.Next(2)
        let otherWorker = 1 - firstWorker
        let firstReviewer = 2 + rng.Next(2)
        let otherReviewer = 5 - firstReviewer
        [ Reserve firstWorker
          ReplayReserve firstWorker
          Reserve otherWorker
          Reserve firstReviewer
          ReplayReserve firstReviewer
          Reserve otherReviewer
          Transition(firstWorker, "Dispatching")
          Transition(firstWorker, "Uncertain")
          Transition(firstWorker, "Unknown")
          ForgedRelease(firstWorker, "head")
          ForgedRelease(firstWorker, "terminal-origin")
          ForgedRelease(firstWorker, "fence")
          ForgedRelease(firstWorker, "missing-operation")
          Release firstWorker
          ReplayRelease firstWorker
          Reserve otherWorker
          MutateReplay firstWorker
          DuplicateCanonical firstWorker
          Transition(firstReviewer, "Dispatching")
          Transition(firstReviewer, "Uncertain")
          Transition(firstReviewer, "Unknown")
          ForgedRelease(firstReviewer, "terminal-blob")
          Release firstReviewer
          Reserve otherReviewer
          ReplayReserve otherReviewer ]

    let kind i = if i < 2 then "Worker" else "Reviewer"
    let project i = i % 2

    let predict (state: Map<int,Entry>) action =
        match action with
        | Reserve i ->
            if Map.containsKey i state then PolicyDenied
            else
                let used =
                    state
                    |> Map.toSeq
                    |> Seq.filter (fun (_,e) -> e.Status <> "Released" && kind e.Index = kind i)
                    |> Seq.length
                if used >= 1 then Full(used,1) else Committed
        | ReplayReserve _ | ReplayRelease _ -> Replayed
        | MutateReplay _ -> Reused
        | DuplicateCanonical _ | ForgedRelease _ -> PolicyDenied
        | Transition _ | Release _ -> Committed

    let advance (state: Map<int,Entry>) action outcome (reservationIds: Guid array) (terminalIds: Guid array) =
        if outcome <> Committed then state
        else
            match action with
            | Reserve i ->
                state.Add(i, { Index=i; OriginalOperationId=reservationIds[i]
                               Status="Reserved"; Generation=1L; TerminalOperationId=None })
            | Transition(i,toStatus) ->
                let old = state[i]
                state.Add(i,{old with Status=toStatus; Generation=old.Generation+1L})
            | Release i ->
                let old = state[i]
                state.Add(i,{old with Status="Released"; Generation=old.Generation+1L
                                      TerminalOperationId=Some terminalIds[i]})
            | _ -> failwithf "Model unexpectedly committed %A" action

    let classify (result: Result<AccountWrite,AccountFailure>) =
        match result with
        | Ok write -> if write.AlreadyApplied then Replayed else Committed
        | Error(AccountPolicyNotAuthoritative _) -> PolicyDenied
        | Error(AccountCapacityFull(used,limit)) -> Full(used,limit)
        | Error AccountOperationIdReused -> Reused
        | Error other -> failwithf "Unexpected native typed result: %A" other

    // Generic trace reducer for a deliberately mutated recorded native observation.
    // This checks comparator sensitivity, not a claimed second Git execution.
    let shrinkMismatch (trace: (Command * Outcome * Outcome) list) =
        let fails xs = xs |> List.exists (fun (_,expected,observed) -> expected <> observed)
        let rec loop size current =
            if size < 1 then current
            else
                let mutable candidate = None
                let mutable offset = 0
                while offset < List.length current && candidate.IsNone do
                    let smaller =
                        current |> List.mapi (fun i x -> i,x)
                                |> List.choose (fun (i,x) ->
                                    if i >= offset && i < offset+size then None else Some x)
                    if fails smaller then candidate <- Some smaller
                    offset <- offset + size
                match candidate with
                | Some reduced -> loop (min size (List.length reduced / 2)) reduced
                | None -> loop (size / 2) current
        loop (List.length trace / 2) trace

    let executeSeed seed =
        let mutable completed: (Command * Outcome * Outcome) list = []
        NativeFixture.withGit (fun f ->
            NativeReservationFixture.establish f
                (NativeReservationFixture.limits 1 1)
                [| NativeReservationFixture.limits 1 1; NativeReservationFixture.limits 1 1 |]
            // All four candidates are authentic, independently materialized project Git
            // issuer records. Only account transaction IDs are seed-deterministic.
            let candidates =
                [| NativeReservationFixture.worker f 0
                   NativeReservationFixture.worker f 1
                   NativeReservationFixture.reviewer f 0
                   NativeReservationFixture.reviewer f 1 |]
            let commands = generate seed
            let reservationIds = [| for i in 0..3 -> deterministicGuid seed (100+i) |]
            let terminalIds = [| for i in 0..3 -> deterministicGuid seed (200+i) |]
            let releaseIds = [| for i in 0..3 -> deterministicGuid seed (300+i) |]
            let fences = Array.create 4 ""
            let sessions = [| for i in 0..3 -> sprintf "provider-differential-%d-%d" seed i |]
            let originalTerminal = System.Collections.Generic.Dictionary<int,NativeTerminalLocator * NativeTerminalProof>()
            let receipts = System.Collections.Generic.Dictionary<Guid,AccountOperationReceipt>()
            let reservationCoverage = System.Collections.Generic.Dictionary<int,NativeDisposableCoverage>()
            let mutable model: Map<int,Entry> = Map.empty
            let initial = AccountCapacityLedger.fetch f.AccountOne |> NativeFixture.success
            let mutable expectedSeq = initial.State.NextSequence
            let mutable expectedVersion = initial.State.StateVersion
            let observations = ResizeArray<Command * Outcome * Outcome>()

            for index,action in commands |> List.indexed do
                let before = AccountCapacityLedger.fetch f.AccountOne |> NativeFixture.success
                let expected = predict model action
                let account,projects =
                    if index % 2 = 0 then f.AccountOne,f.ProjectsOne else f.AccountTwo,f.ProjectsTwo
                let fresh () = NativeReservationFixture.coverage f account projects
                let guid = deterministicGuid seed (1000+index)
                let candidate i = candidates[i]
                let reservation i operationId value replay =
                    let coverage =
                        if replay then reservationCoverage[i]
                        else
                            let observed = fresh()
                            reservationCoverage[i] <- observed // Refresh after a prior denied attempt; replay uses this exact payload.
                            observed
                    NativeReservationFixture.reserve f account projects coverage operationId value
                let effect i = Guid.Parse(candidates[i].EffectOperationId)
                let dispatch i = Guid.Parse(candidates[i].DispatchId)
                let fence i =
                    if fences[i] = "" then
                        fences[i] <- (LedgerStore.fetch f.ProjectsOne[project i] |> NativeFixture.success).Commit
                    fences[i]
                let locator i terminalId =
                    { DispatchId=dispatch i; OriginalReservationId=reservationIds[i]
                      EffectOperationId=effect i; TerminalOperationId=terminalId }
                let terminal i terminalId badFence =
                    let record =
                        (NativeReservationFixture.snapshot f f.AccountOne f.ProjectsOne).Records
                        |> Array.find (fun r -> r.Candidate.DispatchId = candidates[i].DispatchId)
                    let loc = locator i terminalId
                    let proof =
                        NativeReservationFixture.terminalProof f candidates[i] record loc
                            (if badFence then String.replicate 40 "f" else fence i) sessions[i]
                    loc,proof
                let releaseWith op (loc: NativeTerminalLocator) (proof: NativeTerminalProof) =
                    let verifier =
                        { new INativeDisposableTerminalVerifier with
                            member _.VerifyCurrentTerminal _ = Ok proof }
                    NativeSchedulerAccountReservation.releaseVerifiedDisposable
                        f.Root account f.PublicKey f.Subject projects (fresh())
                        verifier op "Seeded native differential" loc
                let operationId,result =
                    match action with
                    | Reserve i -> reservationIds[i], reservation i reservationIds[i] (candidate i) false
                    | ReplayReserve i -> reservationIds[i], reservation i reservationIds[i] (candidate i) true
                    | MutateReplay i ->
                        reservationIds[i],
                        reservation i reservationIds[i] {candidate i with OwnerAgentId="forged replay actor"} true
                    | DuplicateCanonical i ->
                        guid, reservation i guid
                            {candidate i with DispatchId=Guid.NewGuid().ToString("D")
                                              EffectOperationId=Guid.NewGuid().ToString("D")} false
                    | Transition(i,status) ->
                        let current = model[i]
                        let session = if status = "Unknown" then sessions[i] else ""
                        guid, NativeSchedulerAccountReservation.transitionVerifiedDisposable
                            f.Root account f.PublicKey f.Subject projects (fresh())
                            guid "Seeded native differential" (dispatch i) (effect i)
                            current.Generation status (fence i) session
                    | ForgedRelease(i,mutation) ->
                        let trialId = deterministicGuid seed (2000+index)
                        let loc,proof = terminal i trialId (mutation = "fence")
                        let badLoc,badProof =
                            match mutation with
                            | "head" -> loc,{proof with ProjectHead=String.replicate 40 "0"}
                            | "terminal-origin" -> loc,{proof with ProjectTerminalOid=String.replicate 40 "0"}
                            | "terminal-blob" -> loc,{proof with ProviderTerminalOid=String.replicate 40 "0"}
                            | "fence" -> loc,proof
                            | "missing-operation" ->
                                let missing = {loc with TerminalOperationId=deterministicGuid seed (3000+index)}
                                let p = {proof with TerminalOperationId=missing.TerminalOperationId}
                                // There is no authenticated operation with missing.TerminalOperationId.
                                let snapshot = LedgerStore.fetch f.ProjectsOne[project i] |> NativeFixture.success
                                let absent =
                                    LedgerStore.getOperationAt f.ProjectsOne[project i] snapshot missing.TerminalOperationId
                                    |> NativeFixture.success
                                Assert.That(absent.IsNone, Is.True, "Missing terminal operation was unexpectedly present")
                                missing,p
                            | other -> failwithf "Unknown mutation %s" other
                        guid,releaseWith guid badLoc badProof
                    | Release i ->
                        let loc,proof = terminal i terminalIds[i] false
                        originalTerminal[i] <- (loc,proof)
                        releaseIds[i],releaseWith releaseIds[i] loc proof
                    | ReplayRelease i ->
                        let loc,proof = originalTerminal[i]
                        releaseIds[i],releaseWith releaseIds[i] loc proof
                let observed = classify result
                observations.Add(action,expected,observed)
                Assert.That(observed,Is.EqualTo(expected),
                    sprintf "seed=%d step=%d action=%A expected=%A observed=%A" seed index action expected observed)
                match expected,result with
                | Committed,Ok write ->
                    Assert.That(write.AlreadyApplied,Is.False)
                    Assert.That(write.Receipt.Sequence,Is.EqualTo(before.State.NextSequence))
                    Assert.That(write.Receipt.StateVersion,Is.EqualTo(before.State.StateVersion+1L))
                    Assert.That(write.Receipt.OperationId,Is.EqualTo(operationId.ToString("D")))
                    receipts[operationId] <- write.Receipt
                    expectedSeq <- expectedSeq+1L
                    expectedVersion <- expectedVersion+1L
                | Replayed,Ok write ->
                    Assert.That(write.Receipt.ResultSha256,Is.EqualTo(receipts[operationId].ResultSha256))
                    Assert.That(write.Receipt.Sequence,Is.EqualTo(receipts[operationId].Sequence))
                | _ -> ()
                model <- advance model action expected reservationIds terminalIds
                let after = AccountCapacityLedger.fetch f.AccountOne |> NativeFixture.success
                Assert.That(after.State.NextSequence,Is.EqualTo(expectedSeq),
                    sprintf "seed=%d step=%d account sequence" seed index)
                Assert.That(after.State.StateVersion,Is.EqualTo(expectedVersion),
                    sprintf "seed=%d step=%d account version" seed index)
                if expected = Committed then
                    Assert.That(after.Head,Is.Not.EqualTo(before.Head),sprintf "seed=%d step=%d changed head" seed index)
                else
                    Assert.That(after.Head,Is.EqualTo(before.Head),sprintf "seed=%d step=%d unchanged head" seed index)
                let remoteHead =
                    NativeFixture.git f.Root
                        [ "--git-dir";f.AccountRemote;"rev-parse";"refs/heads/jarvis-account-admission" ]
                Assert.That(after.Head,Is.EqualTo(remoteHead),sprintf "seed=%d step=%d remote CAS head" seed index)
                let _,persisted = AccountCapacityLedger.readOperation f.AccountOne operationId |> NativeFixture.success
                if receipts.ContainsKey operationId then
                    Assert.That(persisted.IsSome,Is.True,sprintf "seed=%d step=%d receipt absent" seed index)
                    Assert.That(persisted.Value.ResultSha256,
                                Is.EqualTo(receipts[operationId].ResultSha256))
                else
                    Assert.That(persisted.IsNone,Is.True,
                        sprintf "seed=%d step=%d denied request committed receipt" seed index)
                let native =
                    (NativeReservationFixture.snapshot f f.AccountOne f.ProjectsOne).Records
                Assert.That(native.Length,Is.EqualTo(model.Count),sprintf "seed=%d step=%d record count" seed index)
                Assert.That(native |> Array.map (fun r -> r.CanonicalKey) |> Array.distinct |> Array.length,
                            Is.EqualTo(native.Length))
                Assert.That(native |> Array.map (fun r -> r.OriginalOperationId) |> Array.distinct |> Array.length,
                            Is.EqualTo(native.Length))
                for KeyValue(i,entry) in model do
                    let matches = native |> Array.filter (fun r -> r.Candidate.DispatchId = candidates[i].DispatchId)
                    Assert.That(matches.Length,Is.EqualTo(1),sprintf "seed=%d step=%d candidate=%d" seed index i)
                    let actual = matches[0]
                    Assert.That(actual.Candidate.EffectOperationId,Is.EqualTo(candidates[i].EffectOperationId))
                    Assert.That(actual.Candidate.Kind,Is.EqualTo(kind i))
                    Assert.That(actual.OriginalOperationId,Is.EqualTo(entry.OriginalOperationId.ToString("D")))
                    Assert.That(actual.Status,Is.EqualTo(entry.Status))
                    Assert.That(actual.Generation,Is.EqualTo(entry.Generation))
                    match entry.TerminalOperationId with
                    | Some terminalId -> Assert.That(actual.TerminalOperationId,Is.EqualTo(terminalId.ToString("D")))
                    | None -> Assert.That(actual.TerminalOperationId,Is.Empty)
                for role in ["Worker";"Reviewer"] do
                    let modelUsed =
                        model |> Map.toSeq |> Seq.filter (fun (_,e) ->
                            kind e.Index = role && e.Status <> "Released") |> Seq.length
                    let nativeUsed =
                        native |> Array.filter (fun r -> r.Candidate.Kind=role && r.Status<>"Released")
                        |> Array.length
                    Assert.That(nativeUsed,Is.EqualTo(modelUsed),
                        sprintf "seed=%d step=%d %s occupied" seed index role)
                    Assert.That(nativeUsed,Is.LessThanOrEqualTo(1))
            TestContext.Progress.WriteLine(sprintf
                "NATIVE_DIFFERENTIAL seed=%d steps=%d matched=%d nextSequence=%d remote=%s"
                seed commands.Length observations.Count expectedSeq (NativeFixture.head f.AccountOne))
            completed <- List.ofSeq observations)
        completed

[<TestFixture>]
type SchedulerAccountCasNativeDifferentialTests() =
    [<Test>]
    member _.SeededModelDerivedNativeGitCompareReplayAndMutantShrink() =
        let first = NativeCasDifferential.executeSeed 17
        let replay = NativeCasDifferential.executeSeed 17
        let alternate = NativeCasDifferential.executeSeed 31
        Assert.That(first.Length,Is.GreaterThanOrEqualTo(10))
        Assert.That((first = replay),Is.True,"Same seed native trace changed normalized outcomes")
        Assert.That(alternate.Length,Is.GreaterThanOrEqualTo(10))
        let mutated =
            first |> List.mapi (fun i (cmd,expected,observed) ->
                if i=0 then cmd,expected,NativeCasDifferential.Replayed
                else cmd,expected,observed)
        Assert.That(mutated |> List.exists (fun (_,expected,observed) -> expected<>observed),
                    Is.True,"Native comparator failed to detect a poisoned commit/replay observation")
        let minimized = NativeCasDifferential.shrinkMismatch mutated
        Assert.That(minimized.Length,Is.EqualTo(1),"Mismatch did not shrink to one recorded native observation")
        Assert.That(minimized |> List.exists (fun (_,expected,observed) -> expected<>observed),Is.True)
        TestContext.Progress.WriteLine(
            sprintf "NATIVE_COMPARATOR_MUTANT nativeSteps=%d+%d+%d shrunk=%d->%d"
                first.Length replay.Length alternate.Length first.Length minimized.Length)


    [<Test>]
    member _.PostPushLostAcknowledgmentAndPrePushFailureReconcileRealNativeCAS() =
        NativeFixture.withGit (fun f ->
            NativeReservationFixture.establish f
                (NativeReservationFixture.limits 1 1)
                [| NativeReservationFixture.limits 1 1; NativeReservationFixture.limits 1 1 |]
            let worker = NativeReservationFixture.worker f 0
            let reviewer = NativeReservationFixture.reviewer f 1
            let operationId = Guid.NewGuid()
            let observed = NativeReservationFixture.coverage f f.AccountOne f.ProjectsOne
            let before = AccountCapacityLedger.fetch f.AccountOne |> NativeFixture.success
            let lostAck =
                { BeforeLeasePush = fun _ _ -> ()
                  GitCommand = function
                    | "push" :: _ -> AccountGitTestFault.ExecuteThenLoseAcknowledgment
                    | _ -> AccountGitTestFault.PassThrough }
            let result =
                AccountCapacityLedger.withTestInterposition lostAck (fun () ->
                    NativeReservationFixture.reserve f f.AccountOne f.ProjectsOne
                        observed operationId worker)
            let after = AccountCapacityLedger.fetch f.AccountOne |> NativeFixture.success
            let _,receipt = AccountCapacityLedger.readOperation f.AccountOne operationId
                            |> NativeFixture.success
            Assert.That(receipt.IsSome,Is.True,
                        "ExecuteThenLoseAcknowledgment committed no authenticated account receipt")
            match result with
            | Ok write ->
                Assert.That(write.Receipt.ResultSha256,Is.EqualTo(receipt.Value.ResultSha256))
            | Error(AccountOutcomeUnknown unknown) ->
                Assert.That(unknown,Is.EqualTo(operationId))
            | other ->
                Assert.Fail(sprintf "Post-push uncertainty lost its typed boundary: %A" other)
            Assert.That(after.Head,Is.Not.EqualTo(before.Head))
            Assert.That(after.State.NextSequence,Is.EqualTo(before.State.NextSequence+1L))
            Assert.That((NativeReservationFixture.snapshot f f.AccountOne f.ProjectsOne).Records.Length,
                        Is.EqualTo(1))
            let replay =
                NativeReservationFixture.reserve f f.AccountTwo f.ProjectsTwo
                    observed operationId worker |> NativeFixture.success
            Assert.That(replay.AlreadyApplied,Is.True)
            Assert.That(replay.Receipt.ResultSha256,Is.EqualTo(receipt.Value.ResultSha256))
            Assert.That(NativeFixture.head f.AccountOne,Is.EqualTo(after.Head))
            let duplicate =
                NativeReservationFixture.reserve f f.AccountTwo f.ProjectsTwo
                    (NativeReservationFixture.coverage f f.AccountTwo f.ProjectsTwo)
                    (Guid.NewGuid()) worker
            match duplicate with
            | Error(AccountPolicyNotAuthoritative _) -> ()
            | other -> Assert.Fail(sprintf "Uncertain push permitted second reservation: %A" other)
            Assert.That(NativeFixture.head f.AccountOne,Is.EqualTo(after.Head))

            let failedId = Guid.NewGuid()
            let failPush =
                { BeforeLeasePush = fun _ _ -> ()
                  GitCommand = function
                    | "push" :: _ -> AccountGitTestFault.FailBeforeExecution
                    | _ -> AccountGitTestFault.PassThrough }
            let preFailure =
                AccountCapacityLedger.withTestInterposition failPush (fun () ->
                    NativeReservationFixture.reserve f f.AccountOne f.ProjectsOne
                        (NativeReservationFixture.coverage f f.AccountOne f.ProjectsOne)
                        failedId reviewer)
            match preFailure with
            | Error(AccountOutcomeUnknown unknown) ->
                Assert.That(unknown,Is.EqualTo(failedId))
            | Error(AccountGitError _) | Error AccountContended -> ()
            | other ->
                Assert.Fail(sprintf "Pre-push failure unexpectedly committed or had untyped result: %A" other)
            let afterFailure = AccountCapacityLedger.fetch f.AccountOne |> NativeFixture.success
            Assert.That(afterFailure.Head,Is.EqualTo(after.Head))
            Assert.That(afterFailure.State.NextSequence,Is.EqualTo(after.State.NextSequence))
            let _,absent = AccountCapacityLedger.readOperation f.AccountOne failedId
                           |> NativeFixture.success
            Assert.That(absent.IsNone,Is.True,"Pre-push failure manufactured a receipt")
            Assert.That((NativeReservationFixture.snapshot f f.AccountOne f.ProjectsOne).Records.Length,
                        Is.EqualTo(1))
            TestContext.Progress.WriteLine(
                "NATIVE_GIT_FAULTS: post-push lost acknowledgment reconciled; pre-push fault no head/receipt"))
