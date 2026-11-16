module Client.WorkQueueSchedulerHeadBinding

open System
open Client.LedgerStore
open Client.AccountCapacityLedger
open Client.WorkQueueRoleAuthority
open Client.WorkQueueReactiveWake
open Client.WorkQueueSchedulerHostLoop

// This is an opt-in READ boundary, not a membership registry or an admission
// grant. Approval must come from an independently authorized operator/registry;
// a project discovered on disk or in an account token is never auto-enrolled.
type ApprovedSchedulerProject =
    { Alias: string
      Binding: LedgerBinding }

// An independently approved exact WQ04 catalog identity. Subscription names
// alone cannot authorize a later role version, enablement or instruction edit.
type ApprovedSchedulerRoleCatalog =
    { BlobOid: string
      Revision: int }

type SchedulerMembershipEvidence =
    { AccountId: Guid
      // This is the COMPLETE approved set, not a filter over discovered projects.
      ProjectIds: Set<Guid>
      // All current roles and their exact subscriptions must be accounted for.
      // A changed role catalog requires renewed approval before a host callback.
      RoleSubscriptions: Map<Guid, Map<string, Set<string>>>
      ApprovedRoleCatalogs: Map<Guid, ApprovedSchedulerRoleCatalog>
      AuthorityDescription: string }

type SchedulerHeadScope =
    { Account: AccountBinding
      Projects: ApprovedSchedulerProject list
      Membership: SchedulerMembershipEvidence option }

type SchedulerHeadReadPorts =
    { ReadAccount: AccountBinding -> Result<AccountSnapshot, string>
      ReadProject: LedgerBinding -> Result<LedgerSnapshot * PinnedRoleView, string> }

type VerifiedSchedulerProject =
    { Alias: string
      ProjectId: Guid
      LedgerId: Guid
      Epoch: int
      Ref: string
      Head: string
      ProjectVersion: int
      RoleCatalogRevision: int
      RoleVersions: Map<string, int>
      RoleVersionHistory: Map<string, Set<int>> }

type VerifiedSchedulerHeads =
    { AccountId: Guid
      AccountRef: string
      AccountHead: string
      AccountCapacity: int
      OccupiedCapacity: int
      AccountMigrationPhase: string
      Projects: Map<Guid, VerifiedSchedulerProject> }

type SchedulerHeadHint =
    { ProjectId: Guid
      AccountHead: string
      ProjectHead: string
      Trigger: WorkWakeTrigger }

[<RequireQualifiedAccess>]
type SchedulerHeadScan =
    | Disabled
    | NeedsAttention of code: string * nextAction: string
    | Dormant
    | Hints of SchedulerHeadHint list

[<RequireQualifiedAccess>]
type SchedulerManualStep =
    | Disabled
    | NeedsAttention of code: string * nextAction: string
    | Dormant
    | Reconciled of (Guid * SchedulerHostOutcome) list

// Canonical remote refs used by the WQ07 and WQ02 readers. In particular,
// neither origin/main nor a local worktree HEAD is an authority.
[<Literal>]
let ProjectRef = "refs/heads/jarvis-orchestration"

let realReadPorts : SchedulerHeadReadPorts =
    { ReadAccount = fun binding ->
          AccountCapacityLedger.fetch binding
          |> Result.mapError (sprintf "%A")
      ReadProject = fun binding ->
          LedgerStore.fetch binding
          |> Result.bind (fun snapshot ->
              WorkQueueRoleAuthority.loadAt binding snapshot
              |> Result.map (fun roles -> snapshot, roles))
          |> Result.mapError (sprintf "%A") }

let private validOid (value: string) =
    not (String.IsNullOrWhiteSpace value)
    && (value.Length = 40 || value.Length = 64)
    && (value |> Seq.forall (fun ch -> (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f')))

let private nonBlank (s: string) = not (String.IsNullOrWhiteSpace s)

let private validateScope (scope: SchedulerHeadScope) =
    if isNull (box scope) || isNull (box scope.Account) then
        Error "AccountBindingUnknown: explicit approved account binding is required."
    elif isNull (box scope.Projects) || List.isEmpty scope.Projects || List.length scope.Projects > 64 then
        Error "ProjectMembershipUnknown: provide a complete bounded set of 1..64 project bindings."
    elif scope.Account.AccountId = Guid.Empty || scope.Account.LedgerId = Guid.Empty
         || scope.Account.Epoch < 1
         || not (nonBlank scope.Account.RepositoryPath)
         || not (nonBlank scope.Account.RemoteName)
         || not (nonBlank scope.Account.ApprovedFetchUrl)
         || not (nonBlank scope.Account.ApprovedPushUrl) then
        Error "AccountBindingUnknown: account/ledger identity or epoch missing."
    else
        match scope.Membership with
        | None ->
            Error "ProjectMembershipUnknown: no independently approved complete account/project/role membership evidence."
        | Some membership when isNull (box membership)
                               || isNull (box membership.RoleSubscriptions)
                               || isNull (box membership.ApprovedRoleCatalogs) ->
            Error "ProjectMembershipUnknown: complete role authority is absent."
        | Some membership when membership.AccountId <> scope.Account.AccountId
                               || not (nonBlank membership.AuthorityDescription) ->
            Error "AccountMembershipMismatch: membership authority/account identity is missing or contradictory."
        | Some membership ->
            let projects = scope.Projects
            let badProject =
                projects |> List.exists (fun p ->
                    isNull (box p) || isNull (box p.Binding)
                    || p.Binding.ProjectId = Guid.Empty || p.Binding.LedgerId = Guid.Empty
                    || p.Binding.Epoch < 1
                    || not (nonBlank p.Alias)
                    || not (nonBlank p.Binding.RepositoryPath)
                    || not (nonBlank p.Binding.RemoteName)
                    || not (nonBlank p.Binding.ApprovedFetchUrl)
                    || not (nonBlank p.Binding.ApprovedPushUrl))
            if badProject then Error "ProjectBindingUnknown: incomplete project authority."
            else
                let ids = projects |> List.map (fun p -> p.Binding.ProjectId)
                let aliases = projects |> List.map (fun p -> p.Alias.ToUpperInvariant())
                let remotes = projects |> List.map (fun p -> p.Binding.ApprovedFetchUrl)
                let approvedIds =
                    membership.RoleSubscriptions |> Map.toSeq |> Seq.map fst |> Set.ofSeq
                let catalogIds =
                    membership.ApprovedRoleCatalogs |> Map.toSeq |> Seq.map fst |> Set.ofSeq
                if Set.ofList ids <> membership.ProjectIds
                   || approvedIds <> membership.ProjectIds
                   || catalogIds <> membership.ProjectIds then
                    Error "ProjectMembershipMismatch: approved project IDs and role source must exactly cover configured bindings."
                elif membership.ApprovedRoleCatalogs
                     |> Map.exists (fun _ catalog ->
                         isNull (box catalog) || not (validOid catalog.BlobOid)
                         || catalog.Revision < 1) then
                    Error "RoleCatalogApprovalUnknown: every approved project needs a valid exact WQ04 catalog blob and revision."
                elif ids.Length <> (ids |> List.distinct |> List.length)
                     || aliases.Length <> (aliases |> List.distinct |> List.length)
                     || remotes.Length <> (remotes |> List.distinct |> List.length) then
                    Error "DuplicateProjectAuthority: duplicate project ID, alias, or canonical remote."
                else Ok membership

let private verifyAccount (scope: SchedulerHeadScope) (snapshot: AccountSnapshot) =
    let state =
        if isNull (box snapshot) then Unchecked.defaultof<AccountCapacityState>
        else snapshot.State
    if isNull (box snapshot) || isNull (box state) then
        Error "AccountUnknown: account reader returned no canonical state."
    elif not (validOid snapshot.Head)
         || state.AccountId <> string scope.Account.AccountId
         || state.LedgerId <> string scope.Account.LedgerId
         || state.Epoch <> scope.Account.Epoch then
        Error "AccountIdentityConflict: account head/ID/ledger/epoch does not match approved binding."
    elif state.MaxConcurrentAgents < 1
         || state.MigrationPhase |> nonBlank |> not
         || isNull state.Tokens then
        Error "AccountCapacityUnknown: approved Git account cap, migration phase or token state is unavailable."
    else
        let occupied = AccountCapacityLedger.occupied state
        if occupied > state.MaxConcurrentAgents then
            Error "AccountCapacityConflict: held account capacity exceeds the verified cap."
        else Ok occupied

let private verifyProject (membership: SchedulerMembershipEvidence)
                          (approved: ApprovedSchedulerProject)
                          (snapshot: LedgerSnapshot, pinned: PinnedRoleView) =
    let binding = approved.Binding
    if isNull (box snapshot) || isNull (box pinned)
       || isNull (box snapshot.Header)
       || isNull (box pinned.Proof) || isNull (box pinned.Catalog)
       || isNull (box pinned.Project) then
        Error(sprintf "ProjectUnknown: %s has no complete verified project/role snapshot." approved.Alias)
    elif not (validOid snapshot.Commit)
         || snapshot.Header.ProjectId <> string binding.ProjectId
         || snapshot.Header.LedgerId <> string binding.LedgerId
         || snapshot.Header.Epoch <> binding.Epoch
         || pinned.Proof.Head <> snapshot.Commit
         || pinned.Proof.ProjectId <> string binding.ProjectId
         || pinned.Proof.LedgerId <> string binding.LedgerId
         || pinned.Proof.Epoch <> binding.Epoch then
        Error(sprintf "ProjectIdentityConflict: %s has inconsistent project/ledger/ref/head/role provenance." approved.Alias)
    else
        match membership.RoleSubscriptions.TryFind binding.ProjectId,
              membership.ApprovedRoleCatalogs.TryFind binding.ProjectId with
        | None, _ | _, None ->
            Error(sprintf "RoleMembershipUnknown: no complete approved WQ04 role/catalog set for %s." approved.Alias)
        | Some _, Some catalog
            when pinned.Proof.CatalogBlobOid <> catalog.BlobOid
                 || pinned.Proof.CatalogRevision <> catalog.Revision
                 || pinned.Catalog.Revision <> catalog.Revision ->
            Error(sprintf "RoleCatalogApprovalChanged: %s has a different WQ04 role catalog; renew independent approval before host reconciliation." approved.Alias)
        | Some approvedRoles, Some _ ->
            let current = pinned.Catalog.Current
            let currentIds = current |> Map.toSeq |> Seq.map fst |> Set.ofSeq
            let approvedIds = approvedRoles |> Map.toSeq |> Seq.map fst |> Set.ofSeq
            if currentIds <> approvedIds then
                Error(sprintf "RoleMembershipMismatch: current roles for %s differ from complete approved role set." approved.Alias)
            else
                let mismatch =
                    approvedRoles
                    |> Map.toList
                    |> List.tryPick (fun (roleId, subscriptions) ->
                        current.TryFind roleId
                        |> Option.bind (fun version ->
                            pinned.Catalog.Versions.TryFind roleId
                            |> Option.bind (fun versions -> versions.TryFind version))
                        |> function
                            | None -> Some roleId
                            | Some role when role.QueueSubscriptions <> subscriptions -> Some roleId
                            | Some _ -> None)
                match mismatch with
                | Some roleId ->
                    Error(sprintf "RoleSubscriptionConflict: %s/%s subscription or version authority changed." approved.Alias roleId)
                | None ->
                    Ok
                        { Alias = approved.Alias
                          ProjectId = binding.ProjectId
                          LedgerId = binding.LedgerId
                          Epoch = binding.Epoch
                          Ref = ProjectRef
                          Head = snapshot.Commit
                          ProjectVersion = pinned.Project.Version
                          RoleCatalogRevision = pinned.Catalog.Revision
                          RoleVersions = current
                          RoleVersionHistory =
                              pinned.Catalog.Versions
                              |> Map.map (fun _ versions ->
                                  versions |> Map.toSeq |> Seq.map fst |> Set.ofSeq) }

let private verifyHeldTokens (scope: SchedulerHeadScope)
                             (projects: Map<Guid, VerifiedSchedulerProject>)
                             (account: AccountSnapshot) =
    account.State.Tokens
    |> Array.tryPick (fun token ->
        if isNull (box token) then Some "AccountTokenUnknown: null token in account state."
        elif token.Status <> "Held" then None
        else
            match Guid.TryParse token.ProjectId with
            | false, _ -> Some "AccountTokenProjectUnknown: held token has invalid project ID."
            | true, projectId ->
                match projects.TryFind projectId,
                      scope.Projects |> List.tryFind (fun p -> p.Binding.ProjectId = projectId) with
                | Some project, Some approved ->
                    let binding = approved.Binding
                    let authority =
                        { ProjectId = binding.ProjectId
                          LedgerId = binding.LedgerId
                          Epoch = binding.Epoch
                          ApprovedFetchUrl = binding.ApprovedFetchUrl
                          ApprovedPushUrl = binding.ApprovedPushUrl }
                    if token.ProjectLedgerId <> string binding.LedgerId
                       || token.ProjectLedgerEpoch <> binding.Epoch
                       || token.ProjectAuthoritySha256 <> AccountCapacityLedger.projectAuthoritySha256 authority
                       || token.RoleConfigVersion < 1L
                       || token.RoleConfigVersion > int64 Int32.MaxValue
                       || not (project.RoleVersionHistory.TryFind token.RoleId
                               |> Option.defaultValue Set.empty
                               |> Set.contains (int token.RoleConfigVersion)) then
                        Some "AccountTokenAuthorityConflict: held token refers to an unapproved project/ledger/role version."
                    else None
                | _ ->
                    Some "ProjectMembershipIncomplete: a held account token refers to an unapproved project.")

let private readPass (scope: SchedulerHeadScope) (membership: SchedulerMembershipEvidence)
                     (ports: SchedulerHeadReadPorts) =
    let readAccount () =
        try ports.ReadAccount scope.Account
        with ex -> Error("AccountReadFailed: " + ex.Message)
    let readProject (approved: ApprovedSchedulerProject) =
        let fetched =
            try ports.ReadProject approved.Binding
            with ex -> Error("ProjectReadFailed: " + ex.Message)
        fetched
        |> Result.bind (verifyProject membership approved)
        |> Result.mapError (fun e -> approved.Alias + ": " + e)
    readAccount()
    |> Result.bind (fun account ->
        verifyAccount scope account
        |> Result.bind (fun occupied ->
            (Ok Map.empty, scope.Projects)
            ||> List.fold (fun collected approved ->
                collected |> Result.bind (fun projects ->
                    readProject approved
                    |> Result.map (fun project -> projects.Add(project.ProjectId, project))))
            |> Result.bind (fun projects ->
                match verifyHeldTokens scope projects account with
                | Some error -> Error error
                | None ->
                    Ok { AccountId = scope.Account.AccountId
                         AccountRef = AccountCapacityLedger.AccountRef
                         AccountHead = account.Head
                         AccountCapacity = account.State.MaxConcurrentAgents
                         OccupiedCapacity = occupied
                         AccountMigrationPhase = account.State.MigrationPhase
                         Projects = projects })))

/// A bounded two-pass read rejects changes during a cross-ledger observation.
/// This is NOT a global multi-Git transaction. Coordinator and canonical CAS
/// must independently reread and fence all mutable authority before an effect.
let readVerified (scope: SchedulerHeadScope) (ports: SchedulerHeadReadPorts) =
    validateScope scope
    |> Result.bind (fun membership ->
        readPass scope membership ports
        |> Result.bind (fun first ->
            readPass scope membership ports
            |> Result.bind (fun second ->
                if first = second then Ok second
                else Error "ChangedVerifiedHeads: project, role or account Git head changed during observation; retry a bounded full read.")))

/// Last-seen heads are disposable hints. No timer, external connection,
/// persisted clock, model slot, server telemetry, CAS or provider call exists.
type Observer(scope: SchedulerHeadScope, ?enabled: bool, ?ports: SchedulerHeadReadPorts) =
    let enabled = defaultArg enabled false
    let ports = defaultArg ports realReadPorts
    let gate = obj ()
    let mutable lastSeen: VerifiedSchedulerHeads option = None

    member _.IsEnabled = enabled

    member _.ProjectIds =
        // Never expose a project set as approved before validating all
        // membership and catalog evidence, including missing/null scope.
        validateScope scope
        |> Result.map (fun membership -> membership.ProjectIds)

    member _.ReadAll() =
        if not enabled then
            Error "HeadObserverDisabled: an approved explicit opt-in is required even for manual reads."
        else readVerified scope ports

    // Suitable as WorkQueueSchedulerHostLoop.SchedulerHostPorts.ReadVerifiedHeads
    // for the one selected project, never a fabricated multi-project OID.
    member this.ReadVerifiedHeads(projectId: Guid) : Result<VerifiedWorkHeads, string> =
        this.ReadAll()
        |> Result.bind (fun heads ->
            heads.Projects.TryFind projectId
            |> function
                | None -> Error "ProjectBindingUnknown: selected host project is not approved."
                | Some project ->
                    Ok { AccountHead = heads.AccountHead; ProjectHead = project.Head })

    member this.Scan(forceFullRescan: bool) =
        if not enabled then SchedulerHeadScan.Disabled
        else
            match this.ReadAll() with
            | Error reason ->
                SchedulerHeadScan.NeedsAttention("VerifiedHeadReadUnknown", reason)
            | Ok current ->
                let hints =
                    lock gate (fun () ->
                        let changed =
                            current.Projects
                            |> Map.toList
                            |> List.choose (fun (id, project) ->
                                let isChanged =
                                    forceFullRescan
                                    || match lastSeen with
                                       | None -> true
                                       | Some previous ->
                                           previous.AccountHead <> current.AccountHead
                                           || match previous.Projects.TryFind id with
                                              | None -> true
                                              | Some prior -> prior.Head <> project.Head
                                if isChanged then
                                    Some { ProjectId = id
                                           AccountHead = current.AccountHead
                                           ProjectHead = project.Head
                                           Trigger = WorkWakeTrigger.ManualReconcile }
                                else None)
                        lastSeen <- Some current
                        changed)
                if List.isEmpty hints then SchedulerHeadScan.Dormant
                else SchedulerHeadScan.Hints hints

/// A PURE manual orchestration port over supplied hosts. No Host.Start(),
/// timers or browser calls are made here. The caller owns any separate opt-in
/// Coordinator, planner and original-operation executor.
let runOneManualStep (observer: Observer)
                     (hosts: Map<Guid, WorkQueueSchedulerHostLoop.Host>)
                     (forceFullRescan: bool) =
    let scanAndRun () =
        match observer.Scan forceFullRescan with
        | SchedulerHeadScan.Disabled -> SchedulerManualStep.Disabled
        | SchedulerHeadScan.NeedsAttention(code, action) ->
            SchedulerManualStep.NeedsAttention(code, action)
        | SchedulerHeadScan.Dormant -> SchedulerManualStep.Dormant
        | SchedulerHeadScan.Hints hints ->
            hints
            |> List.map (fun hint ->
                hint.ProjectId,
                hosts.[hint.ProjectId].RunOnce [hint.Trigger])
            |> SchedulerManualStep.Reconciled
    if isNull (box observer) then
        SchedulerManualStep.NeedsAttention("HeadObserverUnknown", "Supply a valid explicitly approved observer.")
    elif not observer.IsEnabled then SchedulerManualStep.Disabled
    else
        match observer.ProjectIds with
        | Error reason ->
            SchedulerManualStep.NeedsAttention("VerifiedHeadReadUnknown", reason)
        | Ok projectIds when isNull (box hosts)
                             || projectIds <> (hosts |> Map.toSeq |> Seq.map fst |> Set.ofSeq) ->
            SchedulerManualStep.NeedsAttention(
                "HostMembershipMismatch",
                "Supply an exact host for every explicitly approved project; do not silently skip or discover projects.")
        | Ok _ -> scanAndRun ()
