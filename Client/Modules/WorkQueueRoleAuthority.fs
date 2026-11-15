module Client.WorkQueueRoleAuthority

open System
open System.Collections.Generic
open System.Text.Json
open Jarvis.WorkQueues
open Client.LedgerStore
open Client.WorkQueueGitLedger

// WQ-04: isolated durable role authority. This is NOT a live Scheduler route.
// Every positive project transition must eventually use this check in the SAME
// LedgerStore.transact planner as the project mutation and canonical producer
// evidence. An inspection result or a caller-supplied proof is never authority.
module Producer = Jarvis.WorkQueues.WorkQueueProducers

[<CLIMutable>]
type StoredRole =
    { RoleId: string
      Version: int
      Instructions: string
      QueueSubscriptions: string array
      Enabled: bool }

[<CLIMutable>]
type StoredRolePointer =
    { RoleId: string
      Version: int }

[<CLIMutable>]
type StoredRoleReceipt =
    { OperationId: string
      RoleId: string
      Version: int }

[<CLIMutable>]
type StoredRoleCatalog =
    { SchemaVersion: int
      ProjectId: string
      Revision: int
      Current: StoredRolePointer array
      Versions: StoredRole array
      Receipts: StoredRoleReceipt array }

// A locator is checked against the trusted Git snapshot again on every use;
// constructing this record is never sufficient proof of admission or claim.
type RoleRevisionProof =
    { ProjectId: string
      LedgerId: string
      Epoch: int
      Head: string
      ProjectBlobOid: string
      ProjectVersion: int
      CatalogBlobOid: string
      CatalogRevision: int }

type PinnedRoleView =
    { Proof: RoleRevisionProof
      Project: Project
      Catalog: RoleCatalog }

type RoleUse =
    | PlanNewDemand of roleId: string
    | AdmitNew of launch: Launch
    | ClaimWork of launchId: string * workId: string
    | ReconcileCredits
    | RoleMessageDemand of roleId: string * messageId: string
    | FirstConnect of launchId: string

// A read-only feasibility assessment, deliberately not a launch capability.
// The original task/review/challenge source and cross-ledger WQ05 proof must
// still be verified by the combined project-CAS writer before any effect.
type RolePreflight =
    { Proof: RoleRevisionProof
      Use: RoleUse
      RequiresCanonicalSourceAndAccountProof: bool }

module WorkQueueRoleAuthority =
    [<Literal>]
    let CatalogPath = "records/work-queue/role-catalog.json"

    let private nonBlank (value: string) = not (String.IsNullOrWhiteSpace value)
    let private orderStrings items =
        items |> Seq.sortWith (fun a b -> StringComparer.Ordinal.Compare(a, b)) |> Seq.toArray
    let private storedRole (role: RoleConfiguration) =
        { RoleId = role.RoleId; Version = role.Version
          Instructions = role.Instructions; Enabled = role.Enabled
          QueueSubscriptions = orderStrings role.QueueSubscriptions }
    let private roleOfStored (role: StoredRole) : RoleConfiguration =
        { RoleId = role.RoleId; Version = role.Version
          Instructions = role.Instructions; Enabled = role.Enabled
          QueueSubscriptions = Set.ofArray role.QueueSubscriptions }

    let private encode projectId (catalog: RoleCatalog) =
        let current =
            catalog.Current
            |> Map.toSeq
            |> Seq.map (fun (role, version) -> { RoleId = role; Version = version })
            |> Seq.sortWith (fun a b -> StringComparer.Ordinal.Compare(a.RoleId, b.RoleId))
            |> Seq.toArray
        let versions =
            catalog.Versions
            |> Map.toSeq
            |> Seq.collect (fun (_, history) -> history |> Map.toSeq |> Seq.map snd)
            |> Seq.sortWith (fun a b ->
                let name = StringComparer.Ordinal.Compare(a.RoleId, b.RoleId)
                if name <> 0 then name else compare a.Version b.Version)
            |> Seq.map storedRole |> Seq.toArray
        let receipts =
            catalog.Receipts
            |> Map.toSeq
            |> Seq.map (fun (op, role) ->
                { OperationId = op; RoleId = role.RoleId; Version = role.Version })
            |> Seq.sortWith (fun a b -> StringComparer.Ordinal.Compare(a.OperationId, b.OperationId))
            |> Seq.toArray
        JsonSerializer.Serialize(
            { SchemaVersion = 1; ProjectId = projectId
              Revision = catalog.Revision; Current = current
              Versions = versions; Receipts = receipts })

    let private validCatalog (catalog: RoleCatalog) =
        let versions = catalog.Versions |> Map.toSeq |> Seq.collect (fun (_, vs) -> vs |> Map.toSeq) |> Seq.length
        let validRole roleId version (role: RoleConfiguration) =
            nonBlank roleId && roleId <> "Manager" && roleId = role.RoleId
            && role.Version = version && version > 0 && nonBlank role.Instructions
            && not role.QueueSubscriptions.IsEmpty
            && (role.QueueSubscriptions |> Set.forall nonBlank)
        catalog.Covered && catalog.Revision >= 0
        && catalog.Revision = versions && catalog.Revision = catalog.Receipts.Count
        && catalog.Current.Count = catalog.Versions.Count
        && (catalog.Versions |> Map.forall (fun id history ->
            not history.IsEmpty
            && (history |> Map.forall (validRole id))
            && (history |> Map.toSeq |> Seq.map fst |> Seq.toList) = [1..history.Count]
            && catalog.Current.TryFind id = Some history.Count))
        && (catalog.Receipts |> Map.forall (fun op role ->
            nonBlank op && (catalog.Versions.TryFind role.RoleId
                |> Option.bind (fun history -> history.TryFind role.Version)) = Some role))
        && (catalog.Receipts |> Map.toSeq
            |> Seq.map (fun (_, role) -> role.RoleId, role.Version) |> Seq.distinct |> Seq.length) = versions

    let private decode projectId (raw: string) : Result<RoleCatalog, LedgerFailure> =
        try
            let persisted = JsonSerializer.Deserialize<StoredRoleCatalog>(raw)
            if isNull (box persisted)
               || persisted.SchemaVersion <> 1 || persisted.ProjectId <> projectId
               || isNull persisted.Current || isNull persisted.Versions || isNull persisted.Receipts then
                Error(LedgerIntegrityError "WQ04 role-catalog schema/project identity mismatch.")
            else
                let versions : Map<string, Map<int, RoleConfiguration>> =
                    persisted.Versions
                    |> Array.groupBy (fun role -> role.RoleId)
                    |> Array.map (fun (role, entries) ->
                        role, (entries |> Array.map (fun item -> item.Version, roleOfStored item) |> Map.ofArray))
                    |> Map.ofArray
                let receipts : Map<string, RoleConfiguration> =
                    persisted.Receipts
                    |> Array.map (fun receipt ->
                        receipt.OperationId, versions.[receipt.RoleId].[receipt.Version])
                    |> Map.ofArray
                let catalog : RoleCatalog =
                    { Revision = persisted.Revision; Covered = true
                      Current = persisted.Current |> Array.map (fun p -> p.RoleId, p.Version) |> Map.ofArray
                      Versions = versions; Receipts = receipts }
                if not (validCatalog catalog)
                   || persisted.Versions.Length <> catalog.Revision
                   || persisted.Current.Length <> catalog.Current.Count
                   || persisted.Receipts.Length <> catalog.Receipts.Count
                   || encode projectId catalog <> raw then
                    Error(LedgerIntegrityError "WQ04 role-catalog history, receipt, ordering or canonical bytes invalid.")
                else Ok catalog
        with ex ->
            Error(LedgerIntegrityError ("WQ04 invalid historical role catalog: " + ex.Message))

    let private readCatalogAt binding snapshot =
        LedgerStore.readAt binding snapshot CatalogPath
        |> Result.bind (function
            | None, None -> Error(InvalidBinding "WQ04 role-catalog not provisioned; unversioned WQ02 subscriptions are not authority.")
            | Some raw, Some oid ->
                decode ((binding: LedgerBinding).ProjectId.ToString("D")) raw
                |> Result.map (fun catalog -> catalog, oid)
            | _ -> Error(LedgerIntegrityError "WQ04 role-catalog blob/provenance incomplete."))

    let private historical (catalog: RoleCatalog) (roleId: string) (version: int) : Result<RoleConfiguration, LedgerFailure> =
        catalog.Versions.TryFind roleId
        |> Option.bind (fun history -> history.TryFind version)
        |> function
            | Some role when role.RoleId = roleId && role.Version = version -> Ok role
            | _ -> Error(LedgerIntegrityError "WQ04 missing immutable pinned historical role version.")

    let private currentEnabled (catalog: RoleCatalog) (roleId: string) : Result<RoleConfiguration, LedgerFailure> =
        match catalog.Current.TryFind roleId with
        | None -> Error(InvalidBinding "WQ04 role is not registered.")
        | Some revision ->
            historical catalog roleId revision
            |> Result.bind (fun role ->
                if role.Enabled then Ok role
                else Error(InvalidBinding "WQ04 role is disabled for new sessions."))

    let loadAt (binding: LedgerBinding) (snapshot: LedgerSnapshot) =
        WorkQueueGitLedger.loadAt binding snapshot
        |> Result.bind (fun project ->
            LedgerStore.readAt binding snapshot WorkQueueGitLedger.RecordPath
            |> Result.bind (function
                | Some _, Some projectOid ->
                    readCatalogAt binding snapshot
                    |> Result.bind (fun (catalog, catalogOid) ->
                        // Including obsolete nonterminal sessions: no history may
                        // disappear while any original session can still finish.
                        let missing =
                            project.Launches
                            |> Map.toSeq |> Seq.map snd
                            |> Seq.filter (fun launch ->
                                match launch.State with
                                | LaunchState.TerminalPendingRelease _ -> false
                                | _ -> true)
                            |> Seq.tryPick (fun (launch: Launch) ->
                                match historical catalog launch.RoleId launch.RoleVersion with
                                | Ok role when role.Enabled -> None
                                | _ -> Some launch.Id)
                        match missing with
                        | Some id -> Error(LedgerIntegrityError ("WQ04 live launch lacks its original enabled role version: " + id))
                        | None ->
                            Ok { Project = project; Catalog = catalog
                                 Proof =
                                    { ProjectId = binding.ProjectId.ToString("D")
                                      LedgerId = binding.LedgerId.ToString("D")
                                      Epoch = binding.Epoch
                                      Head = snapshot.Commit
                                      ProjectBlobOid = projectOid
                                      ProjectVersion = project.Version
                                      CatalogBlobOid = catalogOid
                                      CatalogRevision = catalog.Revision } })
                | _ -> Error(LedgerIntegrityError "WQ04 project head/blob provenance incomplete.")))

    let load binding =
        LedgerStore.fetch binding |> Result.bind (loadAt binding)

    let verifyAt binding snapshot proof =
        if proof.ProjectId <> (binding: LedgerBinding).ProjectId.ToString("D")
           || proof.LedgerId <> binding.LedgerId.ToString("D")
           || proof.Epoch <> binding.Epoch
           || proof.Head <> snapshot.Commit then
            Error(StaleEntityVersion "WQ04 pinned role/project ledger identity or head changed.")
        else
            loadAt binding snapshot
            |> Result.bind (fun view ->
                if view.Proof <> proof then
                    Error(StaleEntityVersion "WQ04 role revision, project state or blob provenance changed.")
                else Ok view)

    let verifyFresh binding proof =
        LedgerStore.fetch binding |> Result.bind (fun snapshot -> verifyAt binding snapshot proof)

    let private roleFailure reason = InvalidBinding ("WQ04 role preflight: " + reason)

    let private liveUnclaimed (project: Project) =
        project.Launches
        |> Map.toList |> List.map snd
        |> List.filter (fun launch ->
            not launch.EverClaimed &&
            (match launch.State with
             | LaunchState.TerminalPendingRelease _
             | LaunchState.DemandInvalidatedPendingTerminal _ -> false
             | _ -> true))

    let private eligible (project: Project) (role: RoleConfiguration) (work: WorkStage) =
        role.Enabled && role.QueueSubscriptions.Contains work.QueueId
        && work.State = WorkState.Available
        && (match work.Kind with
            | WorkKind.RoleMessage messageId ->
                project.Messages.TryFind messageId
                |> Option.exists (fun message ->
                    message.Subject = Subject.Role role.RoleId
                    && message.State = MessageState.Pending
                    && message.WorkId = Some work.Id
                    && work.QueueId = "role:" + role.RoleId
                    && work.SubjectId = role.RoleId)
            | _ -> true)

    let private rematch (view: PinnedRoleView) (candidates: WorkStage list) (launches: Launch list) : Result<unit, LedgerFailure> =
        let pinned =
            launches
            |> List.map (fun launch ->
                historical view.Catalog launch.RoleId launch.RoleVersion
                |> Result.map (fun role -> launch.Id, role))
        match pinned |> List.tryPick (function Error reason -> Some reason | _ -> None) with
        | Some reason -> Error reason
        | None ->
            let roles: Map<string, RoleConfiguration> = pinned |> List.choose (function Ok pair -> Some pair | _ -> None) |> Map.ofList
            let occupied = Dictionary<string, string>(StringComparer.Ordinal)
            let rec augment launchId (seen: HashSet<string>) =
                candidates
                |> List.filter (eligible view.Project roles.[launchId])
                |> List.exists (fun work ->
                    if not (seen.Add(work.Id)) then false
                    else
                        match occupied.TryGetValue(work.Id) with
                        | false, _ -> occupied.[work.Id] <- launchId; true
                        | true, another when augment another seen ->
                            occupied.[work.Id] <- launchId; true
                        | _ -> false)
            if launches |> List.sortBy (fun (launch: Launch) -> launch.Id)
                |> List.forall (fun launch -> augment launch.Id (HashSet<string>(StringComparer.Ordinal))) then
                Ok ()
            else Error(roleFailure "no injective matching for all unclaimed pinned sessions")

    // This is intentionally a preflight, not an executable contract. The
    // combined WQ04/05 adapter must prove canonical source event high-water,
    // permissions, account token and project transition in one authoritative
    // CAS planner. WQ02 Project.Subscriptions is intentionally NEVER consulted.
    let inspect (view: PinnedRoleView) (useKind: RoleUse) : Result<RolePreflight, LedgerFailure> =
        let project = view.Project
        let result =
            if not project.QueueCovered || not project.GraphCovered
               || not project.MessageCovered || not project.SourceCovered then
                Error(LedgerIntegrityError "WQ04 project queue/graph/message/source coverage is incomplete.")
            else
                Contracts.orderAvailable project
                |> Result.mapError (fun e -> LedgerIntegrityError ("WQ04 project ordering: " + string e))
                |> Result.bind (fun candidates ->
                    let live = liveUnclaimed project
                    match useKind with
                    | PlanNewDemand roleId ->
                        currentEnabled view.Catalog roleId
                        |> Result.bind (fun role ->
                            let rec choose = function
                                | [] -> Error(roleFailure "no feasible role demand")
                                | stage :: rest when eligible project role stage ->
                                    match rematch view (candidates |> List.filter (fun w -> w.Id <> stage.Id)) live with
                                    | Ok () -> Ok ()
                                    | Error _ -> choose rest
                                | _ :: rest -> choose rest
                            choose candidates)
                    | AdmitNew launch ->
                        if launch.ProjectId <> view.Proof.ProjectId
                           || launch.State <> LaunchState.ReservedUnregistered
                           || launch.Version <> 0 || launch.EverClaimed
                           || project.Launches.ContainsKey launch.Id
                           || not (nonBlank launch.AccountTokenId) then
                            Error(roleFailure "invalid or duplicate prospective launch")
                        else
                            currentEnabled view.Catalog launch.RoleId
                            |> Result.bind (fun role ->
                                if role.Version <> launch.RoleVersion then
                                    Error(StaleEntityVersion "WQ04 new launch must pin the current enabled role revision.")
                                else rematch view candidates (launch :: live))
                    | ClaimWork (launchId, workId) ->
                        match project.Launches.TryFind launchId, project.Works.TryFind workId with
                        | Some launch, Some work when
                            (match launch.State, launch.Lease with
                             | LaunchState.Registered _, Some lease -> not lease.StopRequested && not launch.EverClaimed
                             | _ -> false) ->
                            historical view.Catalog launch.RoleId launch.RoleVersion
                            |> Result.bind (fun role ->
                                if not (eligible project role work) then
                                    Error(roleFailure "claimed queue not subscribed by historical pinned version")
                                else
                                    rematch view (candidates |> List.filter (fun w -> w.Id <> workId))
                                        (live |> List.filter (fun other -> other.Id <> launchId)))
                        | _ -> Error(roleFailure "claim requires existing nonterminal launch and stage")
                    | ReconcileCredits -> rematch view candidates live
                    | RoleMessageDemand (roleId, messageId) ->
                        currentEnabled view.Catalog roleId
                        |> Result.bind (fun role ->
                            let stage =
                                candidates |> List.tryFind (fun work ->
                                    work.Kind = WorkKind.RoleMessage messageId
                                    && eligible project role work)
                            match stage with
                            | None -> Error(roleFailure "no canonical pending Role-message work for this subscription")
                            | Some candidate ->
                                rematch view (candidates |> List.filter (fun work -> work.Id <> candidate.Id)) live)
                    | FirstConnect launchId ->
                        match project.Launches.TryFind launchId with
                        | Some launch ->
                            historical view.Catalog launch.RoleId launch.RoleVersion
                            |> Result.bind (fun role ->
                                if not role.Enabled then Error(roleFailure "pinned historical role was disabled")
                                else
                                    match launch.State with
                                    | LaunchState.SubmittedUnregistered _ -> Ok ()
                                    | _ -> Error(roleFailure "first connect requires submitted unregistered launch"))
                        | None -> Error(roleFailure "first connect launch absent"))
        result |> Result.map (fun () ->
            { Proof = view.Proof; Use = useKind
              RequiresCanonicalSourceAndAccountProof = true })

    // This is the reusable joint-CAS guard. A later work-queue writer MUST call
    // it INSIDE LedgerStore.transact's planner and produce its project changes
    // from the same snapshot. It re-reads everything; no caller-provided booleans
    // or historical views can assert authorization.
    let inspectAt binding snapshot proof useKind =
        verifyAt binding snapshot proof |> Result.bind (fun view -> inspect view useKind)

    let inspectFresh binding proof useKind =
        LedgerStore.fetch binding
        |> Result.bind (fun snapshot -> inspectAt binding snapshot proof useKind)

    let private intent actor operationId command requestJson =
        { Actor = actor; OperationId = operationId
          Kind = command; RequestJson = requestJson }

    let initializeCatalog binding actor operationId =
        let request = JsonSerializer.Serialize({| schemaVersion = 1; command = "InitializeWQ04RoleCatalog" |})
        LedgerStore.transact binding (intent actor operationId "InitializeWQ04RoleCatalog" request)
            (fun snapshot ->
                WorkQueueGitLedger.loadAt binding snapshot
                |> Result.bind (fun _ ->
                    LedgerStore.readAt binding snapshot CatalogPath
                    |> Result.bind (function
                        | Some _, _ -> Error(InvalidBinding "WQ04 catalog already provisioned.")
                        | None, None ->
                            let catalog = Producer.emptyRoleCatalog
                            Ok [{ Path = CatalogPath; ExpectedBlobOid = None
                                  Content = encode ((binding: LedgerBinding).ProjectId.ToString("D")) catalog }]
                        | _ -> Error(LedgerIntegrityError "WQ04 inconsistent catalog blob."))))

    let configure binding actor operationId expectedCatalogRevision expectedPreviousVersion
                  (definition: RoleConfiguration) =
        // Semantics, not actor/time, form the stable same-UUID request identity.
        let request =
            JsonSerializer.Serialize(
                {| schemaVersion = 1; command = "ConfigureWQ04Role"
                   expectedCatalogRevision = expectedCatalogRevision
                   expectedPreviousVersion = expectedPreviousVersion
                   role = storedRole definition |})
        LedgerStore.transact binding (intent actor operationId "ConfigureWQ04Role" request)
            (fun snapshot ->
                WorkQueueGitLedger.loadAt binding snapshot
                |> Result.bind (fun _ ->
                    readCatalogAt binding snapshot
                    |> Result.bind (fun (before, oid) ->
                        Producer.configureRole expectedCatalogRevision expectedPreviousVersion
                            (operationId.ToString("D")) definition before
                        |> Result.mapError (function
                            | Rejection.CasConflict -> StaleEntityVersion "WQ04 role revision changed."
                            | Rejection.OperationIdReused -> OperationIdReused
                            | e -> InvalidBinding ("WQ04 rejected role change: " + string e))
                        |> Result.bind (fun after ->
                            if not (validCatalog after) then
                                Error(LedgerIntegrityError "WQ04 proposed catalog history is incomplete.")
                            else
                                Ok [{ Path = CatalogPath; ExpectedBlobOid = Some oid
                                      Content = encode ((binding: LedgerBinding).ProjectId.ToString("D")) after }]))))
