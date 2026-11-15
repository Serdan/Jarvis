namespace Jarvis.WorkQueues

open System
open System.Security.Cryptography
open System.Text
open Jarvis.WorkQueues.Contracts

// WQ08 INERT ONLY. These are pure simulation inputs, not evidence that a Git
// source, producer high-water, role version, or account project set is verified.
// A future host adapter must establish those facts independently before reuse.
type InertRoleSnapshot =
    { Id: string
      Version: int
      Queues: Set<string>
      InstructionsDigest: string }

type InertProjectSnapshot =
    { Id: string
      State: Project
      Roles: InertRoleSnapshot list }

type InertSchedulingSnapshot =
    { Account: Account
      Projects: InertProjectSnapshot list
      CompleteProjectSet: bool
      CompleteRoleVersions: bool }

type InertLaunchPlan =
    { Launch: Launch
      WorkId: string
      WorkGeneration: int
      RequestFingerprint: string
      AccountVersion: int
      ProjectVersion: int
      ReserveOperationId: string
      AdmitOperationId: string
      DispatchOperationId: string
      AbortOperationId: string
      ReleaseOperationId: string }

// This returns an instruction *reference*, never a speculative instruction body.
type InertBootstrap =
    { LaunchId: string
      AgentUuid: string
      SessionKey: string
      ProjectId: string
      RoleId: string
      RoleVersion: int
      Queues: Set<string>
      InstructionsDigest: string
      LeaseGeneration: int }

[<RequireQualifiedAccess>]
type InertDispatchAdvice =
    | SubmitOriginal of operationId: string
    | ReconcileOriginal of operationId: string
    | AlreadySubmitted
    | DoNotSubmit

type InertUnregisteredDeadline =
    { LaunchId: string
      DeadlineAt: int64
      OriginalDispatchOperationId: string
      OriginalJournalSettled: bool }

module InertScheduler =
    let private bad reason = Error(Rejection.InvalidState reason)

    // Only the guarded entry point is usable by runtime consumers. The other
    // case exists solely for the friend test assembly's hypothetical WQ02/WQ07
    // model; it is NOT a producer/high-water/role provenance witness.
    type private PlannerScope =
        | RuntimeGuarded
        | TestOnlyHypothetical

    // Length-prefixed UTF-8 removes ambiguity from colons, slashes and unicode.
    // The adapter must persist this fingerprint with the original one-shot intent.
    let private fingerprint fields =
        let canonical =
            fields
            |> List.map (fun (field: string) ->
                sprintf "%d:%s" (Encoding.UTF8.GetByteCount field) field)
            |> String.concat ""
        SHA256.HashData(Encoding.UTF8.GetBytes canonical)
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    let private blocksOther (project: Project) (work: WorkStage) =
        project.Works
        |> Map.exists (fun _ candidate ->
            isUnfinished candidate
            && candidate.Dependencies.Contains work.Id
            && not (candidate.WaivedDependencies.ContainsKey work.Id)
            && not (isSatisfied project work.Id))

    let private roleCompatible (project: Project) (role: InertRoleSnapshot) (work: WorkStage) =
        role.Queues.Contains work.QueueId
        && (match work.Kind with
            | WorkKind.RoleMessage messageId ->
                match project.Messages.TryFind messageId with
                | Some message ->
                    message.Subject = Subject.Role role.Id
                    && message.WorkId = Some work.Id
                    && message.State = MessageState.Pending
                    && work.QueueId = "role:" + role.Id
                    && work.SubjectId = role.Id
                | None -> false
            | _ -> true)

    let private proposedLaunch launchId projectId (role: InertRoleSnapshot) : Launch =
        { Id = launchId
          ProjectId = projectId
          RoleId = role.Id
          RoleVersion = role.Version
          AccountTokenId = "token:" + launchId
          State = LaunchState.ReservedUnregistered
          Lease = None
          Version = 0
          Credit = None
          PrimaryWorkId = None
          EverClaimed = false
          AncillaryMessages = Set.empty
          DispatchCount = 0 }

    let private validateInput scope (snapshot: InertSchedulingSnapshot) launchId =
        if String.IsNullOrWhiteSpace launchId then
            bad "launchId must be supplied once by the external journal"
        elif not snapshot.CompleteProjectSet || not snapshot.CompleteRoleVersions then
            Error(Rejection.IncompleteCoverage "account project set or canonical role versions")
        elif (match scope with
              | RuntimeGuarded ->
                  not (canDispatch snapshot.Account (snapshot.Projects |> List.map (fun item -> item.State)))
              | TestOnlyHypothetical ->
                  not (canDispatchPrototype snapshot.Account (snapshot.Projects |> List.map (fun item -> item.State)))) then
            Error(Rejection.IncompleteCoverage "migration/high-water prerequisites")
        elif snapshot.Projects.IsEmpty
             || (snapshot.Projects |> List.map (fun item -> item.Id) |> Set.ofList |> Set.count) <> snapshot.Projects.Length then
            Error(Rejection.IncompleteCoverage "missing or duplicate project authority")
        elif snapshot.Account.Tokens.ContainsKey launchId
             || snapshot.Projects |> List.exists (fun item -> item.State.Launches.ContainsKey launchId) then
            bad "launchId already has an account or project binding"
        elif snapshot.Projects |> List.exists (fun item ->
            String.IsNullOrWhiteSpace item.Id
            || item.Roles |> List.exists (fun role ->
                String.IsNullOrWhiteSpace role.Id
                || role.Version <= 0
                || String.IsNullOrWhiteSpace role.InstructionsDigest
                || item.State.Subscriptions.TryFind role.Id <> Some role.Queues)
            || (item.Roles |> List.map (fun role -> role.Id) |> Set.ofList |> Set.count) <> item.Roles.Length
            || item.State.Works |> Map.exists (fun _ work -> work.ProjectId <> item.Id)) then
            Error(Rejection.IncompleteCoverage "role subscription, version or stage provenance")
        else Ok ()

    // Non-agent, no I/O, no UUID allocation, no token claim, no dispatch.
    // A caller-stable launchId is persisted before invoking this planner.
    // A different project head or role witness produces a different fingerprint.
    let private planInScope scope (snapshot: InertSchedulingSnapshot) launchId : Result<InertLaunchPlan option, Rejection> =
        validateInput scope snapshot launchId
        |> Result.bind (fun () ->
            let occupied =
                snapshot.Account.Tokens
                |> Map.toSeq
                |> Seq.filter (fun (_, released) -> not released)
                |> Seq.length
            if occupied >= snapshot.Account.MaxConcurrentAgents then Ok None
            else
                let candidates =
                    snapshot.Projects
                    |> List.map (fun item ->
                        orderAvailable item.State
                        |> Result.map (List.map (fun work -> item, work)))
                match candidates |> List.tryPick (function Error error -> Some error | _ -> None) with
                | Some error -> Error error
                | None ->
                    let ranked =
                        candidates
                        |> List.collect (function Ok works -> works | Error _ -> [])
                        |> List.sortBy (fun (item, work) ->
                            (if blocksOther item.State work then 0 else 1),
                            work.AvailableAt, item.Id, work.AvailableSequence, work.Id)
                    ranked
                    |> List.tryPick (fun (item, work) ->
                        item.Roles
                        |> List.sortBy (fun role -> role.Id, role.Version)
                        |> List.tryPick (fun role ->
                            if not (roleCompatible item.State role work) then None
                            else
                                let launch = proposedLaunch launchId item.Id role
                                // The existing WQ07 project credit matcher is the
                                // admission oracle, not an invented parallel matcher.
                                match admitProject item.State.Version ("wq08:preview:" + launchId) launch item.State with
                                | Ok projected when projected.Launches.[launchId].Credit = Some work.Id ->
                                    let fields =
                                        [ "wq08-plan-v1"; launchId; item.Id; role.Id
                                          string role.Version; role.InstructionsDigest
                                          string role.Queues.Count ]
                                        @ Set.toList role.Queues
                                        @ [ work.Id; string work.StageGeneration
                                            string work.AvailableAt; string work.AvailableSequence
                                            string item.State.Version; string item.State.GraphVersion
                                            string snapshot.Account.Version ]
                                    let hash = fingerprint fields
                                    let op kind = "wq08:" + kind + ":" + hash
                                    Some
                                        { Launch = launch
                                          WorkId = work.Id
                                          WorkGeneration = work.StageGeneration
                                          RequestFingerprint = hash
                                          AccountVersion = snapshot.Account.Version
                                          ProjectVersion = item.State.Version
                                          ReserveOperationId = op "reserve"
                                          AdmitOperationId = op "admit"
                                          DispatchOperationId = op "dispatch"
                                          AbortOperationId = op "abort-c01"
                                          ReleaseOperationId = op "release" }
                                | _ -> None))
                    |> Ok)

    // No caller-supplied WQ02 flags can authorize the normal planner. The
    // WQ04 semantic/source guard stays fail-closed until verified adapters land.
    let plan (snapshot: InertSchedulingSnapshot) launchId : Result<InertLaunchPlan option, Rejection> =
        planInScope RuntimeGuarded snapshot launchId

    // INTERNAL + friend test assembly only. This produces a hypothetical
    // plan for pure disposable CAS tests, NOT an executable launch permission.
    // No live Scheduler is bound to or able to call this test seam.
    let internal planHypotheticalFixtureOnly (snapshot: InertSchedulingSnapshot) launchId =
        planInScope TestOnlyHypothetical snapshot launchId

    // A public record is not permission to substitute a pre-registered agent,
    // change a token binding or invent new operation IDs after an uncertain send.
    let private validPlan (plan: InertLaunchPlan) =
        let launch = plan.Launch
        let hasText (value: string) = not (String.IsNullOrWhiteSpace value)
        let operation kind = "wq08:" + kind + ":" + plan.RequestFingerprint
        hasText launch.Id && hasText launch.ProjectId && hasText launch.RoleId
        && hasText plan.WorkId && hasText plan.RequestFingerprint
        && plan.RequestFingerprint.Length = 64
        && (plan.RequestFingerprint |> Seq.forall (fun c ->
            (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
        && launch.RoleVersion > 0 && plan.WorkGeneration > 0
        && plan.AccountVersion >= 0 && plan.ProjectVersion >= 0
        && plan.ReserveOperationId = operation "reserve"
        && plan.AdmitOperationId = operation "admit"
        && plan.DispatchOperationId = operation "dispatch"
        && plan.AbortOperationId = operation "abort-c01"
        && plan.ReleaseOperationId = operation "release"
        && launch.AccountTokenId = "token:" + launch.Id
        && launch.State = LaunchState.ReservedUnregistered
        && launch.Version = 0 && launch.Lease.IsNone && launch.Credit.IsNone
        && launch.PrimaryWorkId.IsNone && not launch.EverClaimed
        && launch.AncillaryMessages.IsEmpty && launch.DispatchCount = 0

    // Global-first: the returned account snapshot must win the *account Git CAS*
    // before project admission is even attempted by an eventual real adapter.
    let reserveAccount (plan: InertLaunchPlan) (account: Account) =
        if not (validPlan plan) then
            bad "malformed or pre-registered launch plan"
        elif account.Migration <> MigrationEpoch.Activated then
            Error(Rejection.IncompleteCoverage "account migration not activated")
        else reserveGlobal plan.AccountVersion plan.ReserveOperationId plan.Launch.Id account

    // Separate project CAS. Matching token + original reserve receipt is necessary,
    // not proof by itself that the future external account Git head is authoritative.
    let admitAfterReserve (plan: InertLaunchPlan) (account: Account) (project: Project) =
        if not (validPlan plan) then
            bad "malformed or pre-registered launch plan"
        elif account.Tokens.TryFind plan.Launch.Id <> Some false
           || account.Receipts.TryFind plan.ReserveOperationId <> Some("reserve|" + plan.Launch.Id) then
            Error Rejection.MissingProof
        elif project.Version <> plan.ProjectVersion then Error Rejection.CasConflict
        elif project.Migration <> MigrationEpoch.Activated then
            Error(Rejection.IncompleteCoverage "project migration not activated")
        else
            match project.Works.TryFind plan.WorkId with
            | Some stage when stage.StageGeneration = plan.WorkGeneration ->
                admitProject plan.ProjectVersion plan.AdmitOperationId plan.Launch project
                |> Result.bind (fun updated ->
                    if updated.Launches.[plan.Launch.Id].Credit = Some plan.WorkId then Ok updated
                    else Error Rejection.NoCompatibleMatching)
            | _ -> Error Rejection.NoEligibleWork

    // C01 abort is the only way to release a reserved but unbound launch.
    // If an admission already exists, the abort must fail and the token stays held.
    let abortUnboundC01 (plan: InertLaunchPlan) (project: Project) =
        if not (validPlan plan) then
            bad "malformed or pre-registered launch plan"
        else
            abortUnboundAdmission project.Version plan.AbortOperationId plan.Launch project

    let releaseAfterTerminal (plan: InertLaunchPlan) (project: Project) (account: Account) =
        match validPlan plan, project.Launches.TryFind plan.Launch.Id with
        | false, _ -> bad "malformed or pre-registered launch plan"
        | true, Some launch when launch.ProjectId = plan.Launch.ProjectId
                           && launch.AccountTokenId = plan.Launch.AccountTokenId
                           && launch.RoleId = plan.Launch.RoleId
                           && launch.RoleVersion = plan.Launch.RoleVersion ->
            match launch.State with
            | LaunchState.TerminalPendingRelease receipt when not (String.IsNullOrWhiteSpace receipt) ->
                releaseGlobal account.Version plan.ReleaseOperationId plan.Launch.Id project account
            | _ -> Error Rejection.MissingProof
        | _ -> Error Rejection.MissingProof

    // Advisory only: caller owns durable pre-send journal, one-shot browser intent
    // and reconciliation. An uncertain first submission NEVER authorizes a resend.
    let dispatchAdvice (plan: InertLaunchPlan) (project: Project) =
        match validPlan plan, project.Launches.TryFind plan.Launch.Id with
        | false, _ -> InertDispatchAdvice.DoNotSubmit
        | true, Some launch when launch.ProjectId = plan.Launch.ProjectId
                           && launch.RoleVersion = plan.Launch.RoleVersion
                           && launch.Credit.IsSome ->
            match launch.State with
            | LaunchState.ReservedUnregistered -> InertDispatchAdvice.SubmitOriginal plan.DispatchOperationId
            | LaunchState.SubmittedUnregistered DispatchState.OutcomeUnknown ->
                InertDispatchAdvice.ReconcileOriginal plan.DispatchOperationId
            | LaunchState.SubmittedUnregistered DispatchState.Submitted ->
                InertDispatchAdvice.AlreadySubmitted
            | _ -> InertDispatchAdvice.DoNotSubmit
        | _ -> InertDispatchAdvice.DoNotSubmit

    let recordDispatchOutcome (plan: InertLaunchPlan) outcome (project: Project) =
        if not (validPlan plan) then
            bad "malformed or pre-registered launch plan"
        else
            dispatchOnce project.Version plan.DispatchOperationId plan.Launch.Id outcome project

    let private bootstrap (launch: Launch) (role: InertRoleSnapshot) =
        match launch.State, launch.Lease with
        | LaunchState.Registered(uuid, sessionKey), Some lease ->
            Ok
                { LaunchId = launch.Id
                  AgentUuid = uuid
                  SessionKey = sessionKey
                  ProjectId = launch.ProjectId
                  RoleId = launch.RoleId
                  RoleVersion = launch.RoleVersion
                  Queues = role.Queues
                  InstructionsDigest = role.InstructionsDigest
                  LeaseGeneration = lease.Generation }
        | _ -> Error(Rejection.InvalidState "registration has no witnessed lease")

    // Pure project-CAS adapter. newUuid is evaluated only for an eligible first
    // registration on a matching project snapshot. A losing real Git push may
    // discard a candidate UUID, but only the committed winner is an agent UUID.
    let initializeFirstConnect expectedVersion operationId launchId sessionKey now
                               (newUuid: unit -> string) (role: InertRoleSnapshot) (project: Project) =
        if String.IsNullOrWhiteSpace operationId || String.IsNullOrWhiteSpace sessionKey then
            bad "missing operationId/sessionKey"
        else
            match project.Launches.TryFind launchId with
            | None -> bad "unknown launch"
            | Some launch ->
                if launch.RoleId <> role.Id || launch.RoleVersion <> role.Version
                   || project.Subscriptions.TryFind role.Id <> Some role.Queues
                   || String.IsNullOrWhiteSpace role.InstructionsDigest then
                    Error(Rejection.IncompleteCoverage "pinned role and instructions are unverified")
                elif project.Receipts.TryFind operationId
                     |> Option.exists (fun existing ->
                         existing <> ProjectOperationPayload.OtherMutation(
                             "initialize|" + launchId + "|" + sessionKey)) then
                    Error Rejection.OperationIdReused
                else
                    match launch.State with
                    | LaunchState.TerminalPendingRelease _
                    | LaunchState.DemandInvalidatedPendingTerminal _ -> Error Rejection.Terminal
                    | LaunchState.Registered(_, oldKey) when oldKey <> sessionKey ->
                        Error Rejection.AlreadyRegistered
                    | LaunchState.Registered _ ->
                        bootstrap launch role |> Result.map (fun receipt -> project, receipt)
                    | LaunchState.SubmittedUnregistered _ ->
                        if project.Version <> expectedVersion then Error Rejection.CasConflict
                        elif project.Launches
                             |> Map.exists (fun otherId other ->
                                 otherId <> launchId
                                 && match other.State with
                                    | LaunchState.Registered(_, key) -> key = sessionKey
                                    | _ -> false) then
                            bad "provider session key already bound to another launch"
                        else
                            let candidate = newUuid ()
                            let mutable guid = Guid.Empty
                            if not (Guid.TryParse(candidate, &guid)) || guid = Guid.Empty then
                                bad "UUID allocator must produce a nonempty UUID"
                            elif project.Launches
                                 |> Map.exists (fun _ other ->
                                     match other.State with
                                     | LaunchState.Registered(uuid, _) ->
                                         let mutable existingGuid = Guid.Empty
                                         Guid.TryParse(uuid, &existingGuid) && existingGuid = guid
                                     | _ -> false) then
                                bad "UUID collision with registered agent"
                            else
                                initializeAgent expectedVersion operationId launchId sessionKey (guid.ToString("D")) now project
                                |> Result.bind (fun updated ->
                                    bootstrap updated.Launches.[launchId] role
                                    |> Result.map (fun receipt -> updated, receipt))
                    | _ -> bad "unsubmitted launch cannot register"

    // Deadline is a pinned external journal input. Original-dispatch settlement
    // is an asserted fixture value, NOT a boolean accepted as production proof.
    let expireUnregistered expectedVersion operationId observedAt
                           (deadline: InertUnregisteredDeadline) (project: Project) =
        let originalDispatchRecorded =
            match project.Receipts.TryFind deadline.OriginalDispatchOperationId with
            | Some(ProjectOperationPayload.OtherMutation content) ->
                content = ("dispatch|" + deadline.LaunchId + "|" + string DispatchState.Submitted)
                || content = ("dispatch|" + deadline.LaunchId + "|" + string DispatchState.OutcomeUnknown)
            | _ -> false
        if String.IsNullOrWhiteSpace deadline.OriginalDispatchOperationId
           || observedAt < deadline.DeadlineAt
           || not deadline.OriginalJournalSettled
           || not originalDispatchRecorded then
            Error Rejection.UnsafeExternalEffect
        else
            match project.Launches.TryFind deadline.LaunchId with
            | Some launch ->
                match launch.State with
                | LaunchState.SubmittedUnregistered _ ->
                    finishUnregistered expectedVersion operationId deadline.LaunchId true true project
                | LaunchState.Registered _ -> Error Rejection.AlreadyRegistered
                | LaunchState.TerminalPendingRelease _
                | LaunchState.DemandInvalidatedPendingTerminal _ -> Error Rejection.Terminal
                | _ -> Error Rejection.MissingProof
            | None -> Error Rejection.MissingProof

    // The non-model service may coalesce many release wakes into one check. No
    // service loop or real-time timer is installed by this pure function.
    type WakeProjection = { Pending: bool; NextFallbackAt: int64 }

    let coalesceWake now boundedFallbackInterval capacityReleased (current: WakeProjection) =
        if boundedFallbackInterval <= 0L then bad "fallback interval must be positive"
        else
            Ok
                { Pending = current.Pending || capacityReleased || now >= current.NextFallbackAt
                  NextFallbackAt =
                    if now >= current.NextFallbackAt then now + boundedFallbackInterval
                    else current.NextFallbackAt }
