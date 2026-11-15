module Client.WorkQueueGitLedger

open System
open System.Collections
open System.Reflection
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Microsoft.FSharp.Reflection
open Jarvis.WorkQueues
open Client.LedgerStore

// WQ-02: isolated, non-activated canonical project Git CAS implementation.
// The existing task/orchestration handlers are intentionally not redirected here.
// Account-global admission, production clock attestation and migration are separate gates.

module private CanonicalCodec =
    let private publicStatic = BindingFlags.Public ||| BindingFlags.Static
    let private mapOfSeq =
        typeof<Map<string, int>>.Assembly.GetType("Microsoft.FSharp.Collections.MapModule", true).GetMethods(publicStatic)
        |> Array.find (fun methodInfo ->
            methodInfo.Name = "OfSeq" && methodInfo.IsGenericMethodDefinition
            && methodInfo.GetGenericArguments().Length = 2)
    let private setOfSeq =
        typeof<Set<string>>.Assembly.GetType("Microsoft.FSharp.Collections.SetModule", true).GetMethods(publicStatic)
        |> Array.find (fun methodInfo ->
            methodInfo.Name = "OfSeq" && methodInfo.IsGenericMethodDefinition
            && methodInfo.GetGenericArguments().Length = 1)

    let rec private encodeType (shape: Type) (value: obj) : JsonNode =
        if shape = typeof<string> then JsonValue.Create(value :?> string) :> JsonNode
        elif shape = typeof<int> then JsonValue.Create(unbox<int> value) :> JsonNode
        elif shape = typeof<int64> then JsonValue.Create(unbox<int64> value) :> JsonNode
        elif shape = typeof<bool> then JsonValue.Create(unbox<bool> value) :> JsonNode
        elif shape.IsGenericType && shape.GetGenericTypeDefinition() = typedefof<Map<_,_>> then
            let arguments = shape.GetGenericArguments()
            if arguments.[0] <> typeof<string> then invalidOp "Only string-keyed canonical maps are supported."
            let result = JsonObject()
            let entries =
                (value :?> IEnumerable)
                |> Seq.cast<obj>
                |> Seq.map (fun entry ->
                    let entryType = entry.GetType()
                    entryType.GetProperty("Key").GetValue(entry) :?> string,
                    entryType.GetProperty("Value").GetValue(entry))
                |> Seq.sortWith (fun (a, _) (b, _) -> StringComparer.Ordinal.Compare(a, b))
            for key, item in entries do result.Add(key, encodeType arguments.[1] item)
            result :> JsonNode
        elif shape.IsGenericType && shape.GetGenericTypeDefinition() = typedefof<Set<_>> then
            let elementType = shape.GetGenericArguments().[0]
            let result = JsonArray()
            for item in value :?> IEnumerable do result.Add(encodeType elementType item)
            result :> JsonNode
        elif FSharpType.IsRecord shape then
            let result = JsonObject()
            let fields = FSharpType.GetRecordFields shape
            let values = FSharpValue.GetRecordFields value
            for field, fieldValue in Array.zip fields values |> Array.sortBy (fun (f, _) -> f.Name) do
                result.Add(field.Name, encodeType field.PropertyType fieldValue)
            result :> JsonNode
        elif FSharpType.IsUnion shape then
            let case, values = FSharpValue.GetUnionFields(value, shape)
            let result = JsonObject()
            result.Add("case", JsonValue.Create(case.Name))
            let elements = JsonArray()
            for field, item in Array.zip (case.GetFields()) values do
                elements.Add(encodeType field.PropertyType item)
            result.Add("fields", elements)
            result :> JsonNode
        else invalidOp ("Unsupported canonical contract type: " + shape.FullName)

    let rec private decodeType (shape: Type) (node: JsonNode) : obj =
        if isNull node then invalidOp "Unexpected null in canonical work record."
        elif shape = typeof<string> then box (node.GetValue<string>())
        elif shape = typeof<int> then box (node.GetValue<int>())
        elif shape = typeof<int64> then box (node.GetValue<int64>())
        elif shape = typeof<bool> then box (node.GetValue<bool>())
        elif shape.IsGenericType && shape.GetGenericTypeDefinition() = typedefof<Map<_,_>> then
            let arguments = shape.GetGenericArguments()
            if arguments.[0] <> typeof<string> then invalidOp "Unsupported canonical map key."
            let tupleType = FSharpType.MakeTupleType arguments
            let entries = node.AsObject() |> Seq.toArray
            let values = Array.CreateInstance(tupleType, entries.Length)
            for index = 0 to entries.Length - 1 do
                let entry = entries.[index]
                values.SetValue(
                    FSharpValue.MakeTuple(
                        [| box entry.Key; decodeType arguments.[1] entry.Value |], tupleType), index)
            mapOfSeq.MakeGenericMethod(arguments).Invoke(null, [| values |])
        elif shape.IsGenericType && shape.GetGenericTypeDefinition() = typedefof<Set<_>> then
            let elementType = shape.GetGenericArguments().[0]
            let elements = node.AsArray()
            let values = Array.CreateInstance(elementType, elements.Count)
            for index = 0 to elements.Count - 1 do
                values.SetValue(decodeType elementType elements.[index], index)
            setOfSeq.MakeGenericMethod([| elementType |]).Invoke(null, [| values |])
        elif FSharpType.IsRecord shape then
            let source = node.AsObject()
            let fields = FSharpType.GetRecordFields shape
            if source.Count <> fields.Length then invalidOp "Canonical record field count mismatch."
            let values =
                fields |> Array.map (fun field ->
                    if not (source.ContainsKey(field.Name)) then
                        invalidOp ("Missing canonical field " + field.Name)
                    decodeType field.PropertyType source.[field.Name])
            FSharpValue.MakeRecord(shape, values)
        elif FSharpType.IsUnion shape then
            let source = node.AsObject()
            if source.Count <> 2 then invalidOp "Canonical union shape mismatch."
            let tag = source.["case"].GetValue<string>()
            let unionCase =
                FSharpType.GetUnionCases shape
                |> Array.tryFind (fun candidate -> candidate.Name = tag)
                |> Option.defaultWith (fun () -> invalidOp ("Unknown canonical union case " + tag))
            let fields = unionCase.GetFields()
            let items = source.["fields"].AsArray()
            if items.Count <> fields.Length then invalidOp "Canonical union field count mismatch."
            let values =
                fields |> Array.mapi (fun index field -> decodeType field.PropertyType items.[index])
            FSharpValue.MakeUnion(unionCase, values)
        else invalidOp ("Unsupported canonical contract type: " + shape.FullName)

    let encode<'T> (value: 'T) =
        encodeType typeof<'T> (box value) |> fun node -> node.ToJsonString()

    let decode<'T> (raw: string) =
        let node = JsonNode.Parse(raw)
        decodeType typeof<'T> node :?> 'T

    let digest (text: string) =
        SHA256.HashData(Encoding.UTF8.GetBytes(text))
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

type ProjectEnvelope =
    { SchemaVersion: int
      ProjectId: string
      StateHash: string
      State: Project }

type WorkMutation =
    | PublishStage of WorkStage
    | PostMessage of SubjectMessage * WorkStage option
    | AdmitProject of Launch
    | DispatchOnce of launchId: string * outcome: DispatchState
    | InitializeAgent of launchId: string * sessionKey: string
    | Heartbeat of launchId: string * leaseGeneration: int
    | StopStalled of launchId: string * leaseGeneration: int * stalledAfter: int64
    | ClaimNext of launchId: string
    | CompleteOrdinary of launchId: string * workId: string
    | CompleteMessage of launchId: string * workId: string * messageId: string * handlerGeneration: int * replyId: string
    | ClaimAncillary of launchId: string * messageId: string
    | CompleteAncillary of launchId: string * messageId: string * handlerGeneration: int * replyId: string
    | SetHardPrerequisite of workId: string * prerequisite: string
    | CancelUnclaimed of workId: string * reason: string
    | TerminalizeNoClaim of launchId: string
    | YieldReadOnly of launchId: string * workId: string * expectedClaimGeneration: int
    | CheckpointReadOnly of launchId: string * workId: string * expectedClaimGeneration: int * summarySha256: string
    | ResolveReadOnlyGate of launchId: string * workId: string * expectedClaimGeneration: int
    | ResolveAncillaryGate of launchId: string * messageId: string * workId: string * expectedLaunchVersion: int * expectedClaimGeneration: int * expectedGraphVersion: int
    // Evidence-dependent integration, account and source-writing transitions
    // remain explicitly unavailable rather than accepting caller proof booleans.

module WorkQueueGitLedger =
    [<Literal>]
    let RecordPath = "records/work-queue/project.json"

    let private failure (reason: Rejection) =
        match reason with
        | Rejection.CasConflict -> StaleEntityVersion "Work ledger project version changed."
        | Rejection.OperationIdReused -> OperationIdReused
        | Rejection.IncompleteCoverage reason -> LedgerIntegrityError ("Incomplete work ledger: " + reason)
        | other -> InvalidBinding ("Rejected WQ-02 transition: " + string other)

    let private envelope binding project =
        { SchemaVersion = 1
          ProjectId = (binding: LedgerBinding).ProjectId.ToString("D")
          StateHash = CanonicalCodec.digest (CanonicalCodec.encode project)
          State = project }

    let private content binding project =
        CanonicalCodec.encode (envelope binding project)

    let private nonBlank (value: string) = not (String.IsNullOrWhiteSpace value)

    let private validStagePayload (stage: WorkStage) =
        nonBlank stage.Id
        && nonBlank stage.ProjectId
        && nonBlank stage.SubjectId
        && nonBlank stage.QueueId
        && (match stage.Kind with WorkKind.RoleMessage id -> nonBlank id | _ -> true)
        && (stage.Dependencies |> Set.forall nonBlank)
        && (stage.WaivedDependencies |> Map.forall (fun id receipt -> nonBlank id && nonBlank receipt))
        && (stage.SourceLineage
            |> Option.forall (fun lineage ->
                nonBlank lineage.RepositoryId
                && nonBlank lineage.TaskId
                && nonBlank lineage.TargetRef
                && nonBlank lineage.LineageId
                && nonBlank lineage.PinnedBaseCommit
                && lineage.Generation > 0
                && (lineage.ReachableRef |> Option.forall nonBlank)
                && (lineage.ContinuationSha |> Option.forall nonBlank)))

    let private validMessagePayload (message: SubjectMessage) =
        nonBlank message.Id
        && nonBlank message.Body
        && (match message.Subject with
            | Subject.Task id | Subject.Role id -> nonBlank id)
        && (message.WorkId |> Option.forall nonBlank)
        && (match message.State with MessageState.Handled reply -> nonBlank reply | _ -> true)

    // The state is an authoritative *single-tree* projection of its own WQ-02
    // events; these checks do not replace verification of external producer
    // journals or account-global authority at production cutover.
    let private structurallyCoherent (binding: LedgerBinding) (state: Project) =
        let projectId = binding.ProjectId.ToString("D")
        let workOwner id (work: WorkStage) =
            match work.State with
            | WorkState.Claimed launchId ->
                state.Launches.TryFind launchId
                |> Option.exists (fun launch ->
                    launch.PrimaryWorkId = Some id
                    || (match work.Kind with
                        | WorkKind.RoleMessage messageId ->
                            launch.AncillaryMessages.Contains messageId
                        | _ -> false))
            | _ -> true
        let validMessage id (message: SubjectMessage) =
            let subjectWork =
                match message.Subject, message.WorkId with
                | Subject.Task _, None -> true
                | Subject.Role roleId, Some workId ->
                    state.Works.TryFind workId
                    |> Option.exists (fun work ->
                        work.Kind = WorkKind.RoleMessage id
                        && work.QueueId = "role:" + roleId
                        && work.SubjectId = roleId
                        && work.StageGeneration > 0
                        && work.SourceLineage.IsNone && work.Push.IsNone)
                | _ -> false
            let handler =
                match message.State with
                | MessageState.Pending | MessageState.Handled _ -> true
                | MessageState.ClaimedPrimary launchId ->
                    message.WorkId
                    |> Option.exists (fun workId ->
                        state.Launches.TryFind launchId
                        |> Option.exists (fun launch ->
                            launch.PrimaryWorkId = Some workId
                            && state.Works.[workId].State = WorkState.Claimed launchId))
                | MessageState.ClaimedAncillary launchId ->
                    state.Launches.TryFind launchId
                    |> Option.exists (fun launch -> launch.AncillaryMessages.Contains id)
            validMessagePayload message && subjectWork && handler && message.HandlerGeneration >= 0
        let validLaunchPhase (launch: Launch) =
            match launch.State with
            | LaunchState.ReservedUnregistered ->
                launch.Version = 0
                && launch.DispatchCount = 0
                && launch.Lease.IsNone
                && not launch.EverClaimed
                && launch.PrimaryWorkId.IsNone
            | LaunchState.SubmittedUnregistered _ ->
                launch.Version >= 1
                && launch.DispatchCount = 1
                && launch.Lease.IsNone
                && not launch.EverClaimed
                && launch.PrimaryWorkId.IsNone
            | LaunchState.Registered (agentUuid, sessionKey) ->
                launch.Version >= 2
                && launch.DispatchCount = 1
                && not (String.IsNullOrWhiteSpace agentUuid)
                && not (String.IsNullOrWhiteSpace sessionKey)
                && (launch.Lease
                    |> Option.exists (fun lease -> lease.Generation > 0 && lease.LastSeenAt >= 0L))
            | LaunchState.DemandInvalidatedPendingTerminal prior ->
                match prior with
                | LaunchState.ReservedUnregistered
                | LaunchState.SubmittedUnregistered _
                | LaunchState.Registered _ -> true
                | _ -> false
            | LaunchState.TerminalPendingRelease receipt ->
                not (String.IsNullOrWhiteSpace receipt)
        state.Version >= 0
        && state.Version = state.Receipts.Count
        && state.GraphVersion >= 0
        && state.NextAvailability >= 0L
        && state.Works |> Map.forall (fun id work ->
            id = work.Id && work.ProjectId = projectId && validStagePayload work
            && work.StageGeneration > 0 && work.ClaimGeneration >= 0
            && work.AvailableSequence >= 0L
            && work.AvailableSequence <= state.NextAvailability
            && work.Dependencies |> Set.forall state.Works.ContainsKey
            // This frozen adapter has no authority-backed waiver mutation.
            && work.WaivedDependencies.IsEmpty
            && workOwner id work)
        && state.Messages |> Map.forall (fun id message ->
            id = message.Id && validMessage id message)
        && state.Launches |> Map.forall (fun id launch ->
            id = launch.Id && launch.ProjectId = projectId
            && launch.Version >= 0 && launch.DispatchCount >= 0
            && launch.RoleVersion > 0
            && not (String.IsNullOrWhiteSpace launch.RoleId)
            && not (String.IsNullOrWhiteSpace launch.AccountTokenId)
            && validLaunchPhase launch
            && (launch.PrimaryWorkId
                |> Option.forall state.Works.ContainsKey)
            && (launch.AncillaryMessages
                |> Set.forall (fun messageId ->
                    state.Messages.TryFind messageId
                    |> Option.exists (fun message ->
                        message.State = MessageState.ClaimedAncillary launch.Id))))

    // Check the exact proposed Git bytes against their decoded projection
    // before granting the CAS writer permission to publish them.
    let private validatedContent binding (state: Project) =
        try
            if not (structurallyCoherent binding state) then
                Error(InvalidBinding "WQ-02 canonical candidate is structurally incomplete.")
            else
                let raw = content binding state
                let decoded = CanonicalCodec.decode<ProjectEnvelope> raw
                if decoded.SchemaVersion <> 1
                   || decoded.ProjectId <> (binding: LedgerBinding).ProjectId.ToString("D")
                   || decoded.State <> state
                   || decoded.StateHash <> CanonicalCodec.digest (CanonicalCodec.encode decoded.State)
                   || CanonicalCodec.encode decoded <> raw
                   || not (structurallyCoherent binding decoded.State) then
                    Error(InvalidBinding "WQ-02 canonical candidate failed roundtrip validation.")
                else Ok raw
        with ex ->
            Error(InvalidBinding ("WQ-02 malformed canonical candidate: " + ex.Message))

    let private validateMutationPayload mutation =
        try
            let raw = CanonicalCodec.encode mutation
            let decoded = CanonicalCodec.decode<WorkMutation> raw
            if decoded <> mutation || CanonicalCodec.encode decoded <> raw then
                Error(InvalidBinding "WQ-02 mutation payload failed canonical roundtrip.")
            else Ok()
        with ex ->
            Error(InvalidBinding ("WQ-02 malformed mutation payload: " + ex.Message))

    let private witnessNewStage (state: Project) now (stage: WorkStage) =
        // Caller-supplied AvailableAt/Sequence are never scheduling authority.
        // A hard-edge-blocked stage receives its FIFO position only on wake.
        let unmet =
            stage.Dependencies
            |> Set.exists (fun prereq ->
                not (stage.WaivedDependencies.ContainsKey prereq
                    || Contracts.isSatisfied state prereq))
        if stage.State = WorkState.Available && not unmet then
            { stage with
                AvailableAt = now
                AvailableSequence = state.NextAvailability + 1L }
        else
            { stage with AvailableAt = 0L; AvailableSequence = 0L }

    let private validInitialStage (stage: WorkStage) =
        validStagePayload stage
        && (stage.State = WorkState.Available || stage.State = WorkState.Blocked)
        && stage.StageGeneration > 0
        && stage.ClaimGeneration = 0
        && stage.Gate = GateState.Clear
        && stage.Domain = DomainState.Undecided
        && stage.Push.IsNone
        && not stage.IntegrationAuthorized
        && stage.WaivedDependencies.IsEmpty

    let private validInitialLaunch (binding: LedgerBinding) (launch: Launch) =
        launch.ProjectId = binding.ProjectId.ToString("D")
        && not (String.IsNullOrWhiteSpace launch.Id)
        && not (String.IsNullOrWhiteSpace launch.RoleId)
        && launch.RoleVersion > 0
        && not (String.IsNullOrWhiteSpace launch.AccountTokenId)
        && launch.State = LaunchState.ReservedUnregistered
        && launch.Lease.IsNone
        && launch.Version = 0
        && launch.Credit.IsNone
        && launch.PrimaryWorkId.IsNone
        && not launch.EverClaimed
        && launch.AncillaryMessages.IsEmpty
        && launch.DispatchCount = 0

    let private readPinned (binding: LedgerBinding) (snapshot: LedgerSnapshot) =
        LedgerStore.readAt binding snapshot RecordPath
        |> Result.bind (fun (raw, oid) ->
            match raw with
            | None -> Ok(None, oid)
            | Some json ->
                try
                    let persisted = CanonicalCodec.decode<ProjectEnvelope> json
                    if persisted.SchemaVersion <> 1
                       || persisted.ProjectId <> binding.ProjectId.ToString("D")
                       || persisted.StateHash <> CanonicalCodec.digest (CanonicalCodec.encode persisted.State)
                       || json <> CanonicalCodec.encode persisted
                       || not (structurallyCoherent binding persisted.State) then
                        Error(LedgerIntegrityError "Work ledger schema, identity, projection or state hash mismatch.")
                    else Ok(Some persisted.State, oid)
                with ex ->
                    Error(LedgerIntegrityError ("Malformed canonical work ledger: " + ex.Message)))

    let load (binding: LedgerBinding) =
        LedgerStore.fetch binding
        |> Result.bind (fun snapshot ->
            readPinned binding snapshot
            |> Result.bind (fun (state, _) ->
                match state with
                | None -> Error(InvalidBinding "WQ-02 ledger is not initialized; no migration is permitted.")
                | Some state -> Ok(snapshot, state)))

    // Explicit initialization is for disposable WQ-02 ledgers. It is not wired
    // to the live orchestration branch or any deployment path.
    let initialize (binding: LedgerBinding) actor operationId subscriptions =
        let state = { Contracts.emptyProject subscriptions with Migration = MigrationEpoch.Frozen }
        let request = JsonObject()
        request.Add("schemaVersion", JsonValue.Create(1))
        request.Add("command", JsonValue.Create("InitializeWQ02"))
        request.Add("subscriptions", JsonNode.Parse(CanonicalCodec.encode subscriptions))
        let intent =
            { OperationId = operationId; Actor = actor; Kind = "WQ02.Initialize"
              RequestJson = request.ToJsonString() }
        LedgerStore.transact binding intent (fun snapshot ->
            readPinned binding snapshot
            |> Result.bind (fun (previous, oid) ->
                match previous with
                | Some _ -> Error(InvalidBinding "WQ-02 ledger already exists.")
                | None ->
                    validatedContent binding state
                    |> Result.map (fun raw ->
                        [ { Path = RecordPath; ExpectedBlobOid = oid
                            Content = raw } ])))

    // All state and the complete original semantic command become a single
    // versioned project Git tree alongside LedgerStore's immutable operation receipt.
    // A competing clone must replay its planner against the winning remote head.
    // Clock time is transition context, not semantic request identity:
    // delayed retries of the same UUID must recover the original receipt.
    // The winning transition retains its witness in persisted project state.
    let private change (binding: LedgerBinding) (actor: string) (operationId: Guid) (expectedVersion: int) (now: int64) (mutation: WorkMutation) =
        let request = JsonObject()
        request.Add("schemaVersion", JsonValue.Create(1))
        request.Add("expectedProjectVersion", JsonValue.Create(expectedVersion))
        request.Add("mutation", JsonNode.Parse(CanonicalCodec.encode mutation))
        let intent =
            { OperationId = operationId; Actor = actor; Kind = "WQ02.Mutate"
              RequestJson = request.ToJsonString() }
        LedgerStore.transact binding intent (fun snapshot ->
            readPinned binding snapshot
            |> Result.bind (fun (previous, oid) ->
                match previous with
                | None -> Error(InvalidBinding "WQ-02 ledger is not initialized.")
                | Some state when state.Version <> expectedVersion ->
                    Error(StaleEntityVersion "Stale WQ-02 project version.")
                | Some state ->
                    let op = operationId.ToString("D")
                    let transition =
                        match mutation with
                        | PublishStage stage ->
                            if not (validInitialStage stage) then
                                Error(Rejection.InvalidState "invalid published stage generation")
                            else
                                Contracts.publishStage expectedVersion op (witnessNewStage state now stage) state
                        | PostMessage (message, roleWork) ->
                            if not (validMessagePayload message)
                               || (roleWork |> Option.exists (validInitialStage >> not)) then
                                Error(Rejection.InvalidState "invalid message or role stage payload")
                            else
                                Contracts.postMessage expectedVersion op message
                                    (roleWork |> Option.map (witnessNewStage state now)) state
                        | AdmitProject launch ->
                            // An account reservation is still a pre-activation gate;
                            // this project mutation cannot accept a forged live launch.
                            if not (validInitialLaunch binding launch)
                               || (state.Launches
                                   |> Map.exists (fun _ existing ->
                                       existing.AccountTokenId = launch.AccountTokenId)) then
                                Error(Rejection.InvalidState "invalid unverified launch admission")
                            else
                                Contracts.admitProject expectedVersion op launch state
                        | DispatchOnce (launch, outcome) -> Contracts.dispatchOnce expectedVersion op launch outcome state
                        | InitializeAgent (launch, sessionKey) ->
                            Contracts.initializeAgent expectedVersion op launch sessionKey (Guid.NewGuid().ToString("D")) now state
                        | Heartbeat (launch, generation) -> Contracts.heartbeat expectedVersion op launch generation now state
                        | StopStalled (launch, generation, timeout) ->
                            Contracts.markStalled expectedVersion op launch generation now timeout state
                        | ClaimNext launch -> Contracts.claimNext expectedVersion op launch state
                        | CompleteOrdinary (launch, work) ->
                            match state.Works.TryFind work with
                            | Some stage when stage.Kind = WorkKind.Implementation
                                              || stage.Kind = WorkKind.Integration ->
                                // Remote source ref/SHA and mutating-job proofs have no
                                // verified completion transition in this foundation.
                                Error Rejection.MissingProof
                            | _ -> Contracts.completeOrdinary expectedVersion op launch work now state
                        | CompleteMessage (launch, work, message, generation, reply) ->
                            Contracts.completeMessageWork expectedVersion op launch work message generation reply now state
                        | ClaimAncillary (launch, message) ->
                            Contracts.claimAncillaryMessage expectedVersion op launch message state
                        | CompleteAncillary (launch, message, generation, reply) ->
                            Contracts.completeAncillaryMessage expectedVersion op launch message generation reply now state
                        | SetHardPrerequisite (work, prerequisite) ->
                            Contracts.setHardPrerequisite expectedVersion op work prerequisite now state
                        | CancelUnclaimed (work, reason) ->
                            Contracts.cancelUnclaimedStage expectedVersion op work reason now state
                        | TerminalizeNoClaim _ ->
                            // A live or stopped lease is not authenticated session-terminal
                            // and settled dispatch-journal proof of release eligibility.
                            Error Rejection.MissingProof
                        | YieldReadOnly (launch, work, claimGeneration) ->
                            Contracts.yieldReadOnly expectedVersion op launch work claimGeneration now state
                        | CheckpointReadOnly (launch, work, claimGeneration, summarySha256) ->
                            match state.Launches.TryFind launch, state.Works.TryFind work with
                            | Some owner, Some claimed
                                when owner.PrimaryWorkId = Some work
                                     && (match owner.State with LaunchState.Registered _ -> true | _ -> false)
                                     && claimed.State = WorkState.Claimed launch
                                     && claimed.ClaimGeneration = claimGeneration
                                     && claimed.Kind <> WorkKind.Implementation
                                     && claimed.Kind <> WorkKind.Integration
                                     && (match claimed.Kind with WorkKind.RoleMessage _ -> false | _ -> true)
                                     && claimed.Domain = DomainState.Undecided
                                     && claimed.SourceLineage.IsNone
                                     && claimed.Push.IsNone
                                     && not claimed.IntegrationAuthorized
                                     && not (isNull summarySha256)
                                     && Regex.IsMatch(summarySha256, "^[a-f0-9]{64}$") ->
                                let evidence = "checkpoint-readonly|" + launch + "|" + work +
                                               "|" + string claimGeneration + "|" + summarySha256
                                Ok { state with
                                         Version = state.Version + 1
                                         Receipts = state.Receipts.Add(op, ProjectOperationPayload.OtherMutation evidence) }
                            | _ -> Error(Rejection.InvalidState "stale/read-write checkpoint generation")
                        | ResolveReadOnlyGate (launch, work, claimGeneration) ->
                            match state.Works.TryFind work with
                            | Some claimed when claimed.State = WorkState.Claimed launch
                                                && claimed.ClaimGeneration = claimGeneration
                                                && claimed.SourceLineage.IsNone
                                                && claimed.Push.IsNone
                                                && claimed.Kind <> WorkKind.Implementation
                                                && claimed.Kind <> WorkKind.Integration ->
                                Contracts.resolveDependencyGate expectedVersion op launch work
                                    (SourceHandoffProof.ReadOnly ("wq02:" + op)) now state
                            | _ -> Error(Rejection.InvalidState "stale or source-writing dependency gate")
                        | ResolveAncillaryGate (launch, message, work, launchVersion, claimGeneration, gateVersion) ->
                            Contracts.resolveAncillaryDependencyGate expectedVersion op launch message work launchVersion claimGeneration gateVersion now state
                    transition
                    |> Result.mapError failure
                    |> Result.bind (fun changed ->
                        validatedContent binding changed
                        |> Result.map (fun raw ->
                            [ { Path = RecordPath; ExpectedBlobOid = oid
                                Content = raw } ]))))

    // The timestamp is captured for the winning transition's persisted state,
    // but is excluded from the original command's stable semantic identity.
    // Server clock authority must be added before live cutover.
    let execute binding actor operationId expectedVersion mutation =
        let now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        validateMutationPayload mutation
        |> Result.bind (fun () -> change binding actor operationId expectedVersion now mutation)

    // Deterministic tests can replay adversarial interleavings with virtual time.
    let internal executeAt binding actor operationId expectedVersion now mutation =
        validateMutationPayload mutation
        |> Result.bind (fun () -> change binding actor operationId expectedVersion now mutation)
