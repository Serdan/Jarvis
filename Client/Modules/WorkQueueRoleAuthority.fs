module Client.WorkQueueRoleAuthority

open System
open System.Collections.Generic
open System.Text.Json
open FsToolkit.ErrorHandling
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
      ProvisionOperationId: string
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
      CatalogRevision: int
      ProvisionOperationId: string }

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
      RequiresCanonicalSourceAndAccountProof: bool
      // Candidate feasibility only. Never a permit to mutate or claim.
      RequiresSameCasChosenWorkAndCreditProof: bool }

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

    let private encode projectId provisionOperationId (catalog: RoleCatalog) =
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
            { SchemaVersion = 2; ProjectId = projectId
              Revision = catalog.Revision; ProvisionOperationId = provisionOperationId
              Current = current
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

    let private decode projectId (raw: string) : Result<RoleCatalog * string, LedgerFailure> =
        try
            let persisted = JsonSerializer.Deserialize<StoredRoleCatalog>(raw)
            if isNull (box persisted)
               || persisted.SchemaVersion <> 2 || persisted.ProjectId <> projectId
               || not (nonBlank persisted.ProvisionOperationId)
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
                   || encode projectId persisted.ProvisionOperationId catalog <> raw then
                    Error(LedgerIntegrityError "WQ04 role-catalog history, receipt, ordering or canonical bytes invalid.")
                else Ok(catalog, persisted.ProvisionOperationId)
        with ex ->
            Error(LedgerIntegrityError ("WQ04 invalid historical role catalog: " + ex.Message))

    let private readCatalogAt binding snapshot =
        LedgerStore.readAt binding snapshot CatalogPath
        |> Result.bind (function
            | None, None -> Error(InvalidBinding "WQ04 role-catalog not provisioned; unversioned WQ02 subscriptions are not authority.")
            | Some raw, Some oid ->
                decode ((binding: LedgerBinding).ProjectId.ToString("D")) raw
                |> Result.map (fun (catalog, provisionId) -> catalog, oid, provisionId)
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

    // The role-specific admission witness must be emitted alongside the
    // original project admission and its operation receipt by the future
    // combined SAME-CAS writer. WQ02's legacy admission alone is insufficient.
    [<CLIMutable>]
    type StoredRoleAdmission =
        { SchemaVersion: int
          ProjectId: string
          LaunchId: string
          AccountTokenId: string
          RoleId: string
          RoleVersion: int
          CatalogBlobOid: string
          RoleDefinitionOid: string
          AdmissionOperationId: string }

    let roleAdmissionPath launchId =
        (WorkQueueGitLedger.admissionPath launchId)
            .Replace("/admissions/", "/role-admissions/", StringComparison.Ordinal)

    let private integrity reason =
        LedgerIntegrityError ("WQ04 historical role authority: " + reason)

    let private requestMatches (operation: LedgerOperation) (predicate: JsonElement -> bool) =
        try
            use outer = JsonDocument.Parse(operation.CanonicalRequestJson)
            let root = outer.RootElement
            let body = root.GetProperty("request").GetString()
            use inner = JsonDocument.Parse(body)
            root.GetProperty("kind").GetString() = operation.Type
            && predicate inner.RootElement
        with _ -> false

    let private operationOrigin (binding: LedgerBinding) (snapshot: LedgerSnapshot)
                                (operationId: string) (kind: string) (paths: string list) =
        match Guid.TryParse operationId with
        | true, uuid when uuid <> Guid.Empty ->
            LedgerStore.getOperationAt binding snapshot uuid
            |> Result.bind (function
                | Some receipt when receipt.Type = kind
                                    && receipt.OperationId = uuid.ToString("D")
                                    && (paths |> List.forall (fun path ->
                                        receipt.Paths |> Array.contains path)) ->
                    LedgerStore.getOperationOriginAt binding snapshot receipt
                    |> Result.map (fun origin -> receipt, { snapshot with Commit = origin })
                | _ -> Error(integrity ("missing immutable " + kind + " operation receipt or witness paths")))
        | _ -> Error(integrity "missing or malformed original operation UUID")

    let private originalRoleRequest (operation: LedgerOperation)
                                    (revision: int) (role: RoleConfiguration) =
        requestMatches operation (fun body ->
            body.GetProperty("schemaVersion").GetInt32() = 1
            && body.GetProperty("command").GetString() = "ConfigureWQ04Role"
            && body.GetProperty("expectedCatalogRevision").GetInt32() = revision - 1
            && body.GetProperty("expectedPreviousVersion").GetInt32() = role.Version - 1
            && JsonSerializer.Deserialize<StoredRole>(body.GetProperty("role").GetRawText())
               = storedRole role)

    // The current JSON is only a projection. Trace *every* catalog mutation
    // along the verified Git head's first-parent history, including issued
    // revisions that a later CAS might omit by restoring an authentic v1 blob.
    let private catalogChangeHistory (binding: LedgerBinding) (snapshot: LedgerSnapshot) =
        BoundedProcess.run
            { Executable = "git"
              Args = [ "log"; "--first-parent"; "--format=%H"; snapshot.Commit; "--"; CatalogPath ]
              WorkingDirectory = binding.RepositoryPath
              Timeout = TimeSpan.FromSeconds 30.
              MaxOutputBytes = Some(1024 * 1024)
              StandardInput = BoundedProcess.ClosedInput
              ExtraEnvironment = [ "GIT_TERMINAL_PROMPT", "0" ]
              ConfigureStart = None }
        |> function
            | Error _ -> Error(LedgerHistoryUnavailable "WQ04 cannot inspect complete role-catalog Git history.")
            | Ok result when result.TimedOut || result.ExitCode <> 0 ->
                Error(LedgerHistoryUnavailable "WQ04 role-catalog Git history unavailable or timed out.")
            | Ok result ->
                let commits =
                    result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    |> Array.map (fun value -> value.Trim())
                if commits.Length = 0
                   || (commits |> Array.exists (fun oid ->
                       not (System.Text.RegularExpressions.Regex.IsMatch(
                           oid, "^[0-9a-f]{40}([0-9a-f]{24})?$")))) then
                    Error(integrity "catalog lacks a complete, valid first-parent issuance history")
                else Ok(commits |> Array.rev |> Array.toList)

    // Each catalog mutation must originate from its own immutable WQ04
    // operation: an empty provision followed by exactly one role revision per
    // transition, preserving all earlier definitions and receipts byte-for-byte.
    // This makes an unauthorized writer, tail deletion or epoch reset fail
    // closed, even if the latest JSON is internally valid and was once issued.
    let private verifyRoleHistory (binding: LedgerBinding) (snapshot: LedgerSnapshot)
                                  (catalog: RoleCatalog) (provisionId: string) =
        catalogChangeHistory binding snapshot
        |> Result.bind (function
            | [] -> Error(integrity "no authenticated provision transition")
            | provisionCommit :: subsequentCommits ->
                operationOrigin binding snapshot provisionId "InitializeWQ04RoleCatalog" [CatalogPath]
                |> Result.bind (fun (receipt, origin) ->
                    if origin.Commit <> provisionCommit
                       || not (requestMatches receipt (fun body ->
                           body.GetProperty("schemaVersion").GetInt32() = 1
                           && body.GetProperty("command").GetString() = "InitializeWQ04RoleCatalog")) then
                        Error(integrity "original provision is not the first authenticated catalog change")
                    else
                        readCatalogAt binding origin
                        |> Result.bind (fun (initial, _, originalProvision) ->
                            WorkQueueGitLedger.loadAt binding origin
                            |> Result.bind (fun project ->
                                if originalProvision <> provisionId
                                   || initial.Revision <> 0
                                   || not initial.Current.IsEmpty
                                   || not initial.Versions.IsEmpty
                                   || not initial.Receipts.IsEmpty
                                   || not project.Launches.IsEmpty then
                                    Error(integrity "catalog was not originally provisioned empty before any launch")
                                else Ok initial)))
                |> Result.bind (fun initial ->
                    subsequentCommits
                    |> List.fold (fun (state: Result<RoleCatalog, LedgerFailure>) commit ->
                        state |> Result.bind (fun (before: RoleCatalog) ->
                            readCatalogAt binding { snapshot with Commit = commit }
                            |> Result.bind (fun ((after: RoleCatalog), _, originalProvision) ->
                                let additions =
                                    after.Receipts
                                    |> Map.toList
                                    |> List.filter (fun (id, _) -> not (before.Receipts.ContainsKey id))
                                match additions with
                                | [ (id, role) ] when
                                    originalProvision = provisionId
                                    && after.Revision = before.Revision + 1 ->
                                    operationOrigin binding snapshot id "ConfigureWQ04Role" [CatalogPath]
                                    |> Result.bind (fun (receipt, issued) ->
                                        let priorVersion =
                                            before.Current.TryFind role.RoleId |> Option.defaultValue 0
                                        match Producer.configureRole before.Revision priorVersion id role before with
                                        | Ok expected when
                                            issued.Commit = commit
                                            && originalRoleRequest receipt after.Revision role
                                            && after = expected -> Ok after
                                        | _ -> Error(integrity "catalog revision or prior history was replaced at issuance"))
                                | _ -> Error(integrity "catalog transition omitted, reset, or rewrote issued role history"))))
                        (Ok initial)
                    |> Result.bind (fun latest ->
                        if latest <> catalog || latest.Revision <> subsequentCommits.Length then
                            Error(integrity "current role catalog differs from complete issued high-water")
                        else Ok ())))

    let private originalAdmissionRequest (operation: LedgerOperation) (launch: Launch)
                                         (credential: WorkCredentialVerifier option) =
        requestMatches operation (fun body ->
            if body.GetProperty("schemaVersion").GetInt32() <> 1 then false
            else
                let mutation = body.GetProperty("mutation")
                let fields = mutation.GetProperty("fields").EnumerateArray() |> Seq.toArray
                let correctCase =
                    match credential with
                    | None -> mutation.GetProperty("case").GetString() = "AdmitProject"
                              && fields.Length = 1
                    | Some verifier ->
                        mutation.GetProperty("case").GetString() = "AdmitCredentialProject"
                        && fields.Length = 2
                        && fields.[1].GetString() = verifier.SecretSha256
                        && verifier.SchemaVersion = 1
                        && verifier.ProjectId = launch.ProjectId
                        && verifier.LaunchId = launch.Id
                        && verifier.AccountTokenId = launch.AccountTokenId
                let admitted = fields.[0]
                correctCase
                && admitted.GetProperty("Id").GetString() = launch.Id
                && admitted.GetProperty("ProjectId").GetString() = launch.ProjectId
                && admitted.GetProperty("AccountTokenId").GetString() = launch.AccountTokenId
                && admitted.GetProperty("RoleId").GetString() = launch.RoleId
                && admitted.GetProperty("RoleVersion").GetInt32() = launch.RoleVersion)

    let private readRoleAdmission binding snapshot launchId =
        LedgerStore.readAt binding snapshot (roleAdmissionPath launchId)
        |> Result.bind (function
            | Some raw, Some oid ->
                try
                    let record = JsonSerializer.Deserialize<StoredRoleAdmission>(raw)
                    if isNull (box record)
                       || record.SchemaVersion <> 1
                       || JsonSerializer.Serialize(record) <> raw then
                        Error(integrity "noncanonical or malformed role-specific admission witness")
                    else Ok(record, oid)
                with _ -> Error(integrity "invalid role-specific admission witness")
            | _ -> Error(integrity ("missing same-CAS role-specific admission for " + launchId)))

    // Legacy WQ02 admissions, even if made after a role catalog was created,
    // never become WQ04-authorized by backfilling a matching numeric version.
    // Admission's verifier, role witness and launch must all be traceable
    // to the same original Git commit. Legacy admissions have no verifier.
    let private authenticatedAdmissionOrigin binding snapshot (launch: Launch)
                                             (decision: ProjectAdmissionRecord) = result {
        let! current = WorkQueueGitLedger.readCredentialAt binding snapshot launch.Id
        let admissionPath = WorkQueueGitLedger.admissionPath launch.Id
        let rolePath = roleAdmissionPath launch.Id
        let paths =
            match current with
            | Some _ ->
                [ WorkQueueGitLedger.RecordPath; admissionPath
                  WorkQueueGitLedger.credentialPath launch.Id; rolePath ]
            | None -> [ WorkQueueGitLedger.RecordPath; admissionPath; rolePath ]
        let! receipt, issued =
            operationOrigin binding snapshot decision.AdmissionOperationId "WQ02.Mutate" paths
        let! original = WorkQueueGitLedger.readCredentialAt binding issued launch.Id
        if current <> original then
            return! Error(integrity "Work credential verifier differs from immutable original admission")
        if not (originalAdmissionRequest receipt launch (current |> Option.map fst)) then
            return! Error(integrity "original admission semantic request mismatches pinned launch")
        return issued
    }

    let private verifyAdmittedLaunch (binding: LedgerBinding) (snapshot: LedgerSnapshot)
                                      (catalog: RoleCatalog) (launch: Launch) =
        match historical catalog launch.RoleId launch.RoleVersion with
        | Error error -> Error error
        | Ok role when not role.Enabled ->
            Error(integrity ("original pinned role was disabled: " + launch.Id))
        | Ok role ->
            WorkQueueGitLedger.readAdmissionAt binding snapshot launch.Id
            |> Result.bind (function
                | Some (decision, decisionOid) when
                    decision.SchemaVersion = 1
                    && decision.ProjectId = binding.ProjectId.ToString("D")
                    && decision.LaunchId = launch.Id
                    && decision.AccountTokenId = launch.AccountTokenId
                    && decision.Kind = "AdmissionWon"
                    && decision.AbortOperationId = "" ->
                    authenticatedAdmissionOrigin binding snapshot launch decision
                    |> Result.bind (fun issued ->
                        WorkQueueGitLedger.readAdmissionAt binding issued launch.Id
                        |> Result.bind (function
                                | Some (originalDecision, originalOid)
                                    when originalOid = decisionOid && originalDecision = decision ->
                                    readRoleAdmission binding snapshot launch.Id
                                    |> Result.bind (fun (roleWitness, witnessOid) ->
                                        readRoleAdmission binding issued launch.Id
                                        |> Result.bind (fun (originalWitness, originalWitnessOid) ->
                                            WorkQueueGitLedger.loadAt binding issued
                                            |> Result.bind (fun issuedProject ->
                                                readCatalogAt binding issued
                                                |> Result.bind (fun (issuedCatalog, issuedCatalogOid, _) ->
                                                    LedgerStore.computeBlobOid binding
                                                        (JsonSerializer.Serialize(storedRole role))
                                                    |> Result.bind (fun roleOid ->
                                                        let issuedRole =
                                                            issuedCatalog.Versions.TryFind launch.RoleId
                                                            |> Option.bind (fun versions ->
                                                                versions.TryFind launch.RoleVersion)
                                                        let admittedLaunch =
                                                            issuedProject.Launches.TryFind launch.Id
                                                        if originalWitness <> roleWitness
                                                           || originalWitnessOid <> witnessOid
                                                           || roleWitness.SchemaVersion <> 1
                                                           || roleWitness.ProjectId <> launch.ProjectId
                                                           || roleWitness.LaunchId <> launch.Id
                                                           || roleWitness.AccountTokenId <> launch.AccountTokenId
                                                           || roleWitness.RoleId <> launch.RoleId
                                                           || roleWitness.RoleVersion <> launch.RoleVersion
                                                           || roleWitness.CatalogBlobOid <> issuedCatalogOid
                                                           || roleWitness.RoleDefinitionOid <> roleOid
                                                           || roleWitness.AdmissionOperationId <> decision.AdmissionOperationId
                                                           || issuedRole <> Some role
                                                           || (admittedLaunch |> Option.exists (fun admitted ->
                                                               admitted.RoleId <> launch.RoleId
                                                               || admitted.RoleVersion <> launch.RoleVersion
                                                               || admitted.AccountTokenId <> launch.AccountTokenId))
                                                           || admittedLaunch.IsNone then
                                                            Error(integrity ("unproven role issuance at original admission: " + launch.Id))
                                                        else Ok())))))
                                | _ -> Error(integrity "original admission decision was rewritten")))
                | _ -> Error(integrity ("missing authenticated original project admission: " + launch.Id)))

    // C01's exclusive unbound tombstone is NOT a role admission. It has no
    // runnable credit and therefore cannot have a role-admission witness.
    // Authenticate its original WQ02 abort CAS instead, preserving the ability
    // to inspect/recover the project after a verified global-token release.
    let private verifyAbortedLaunch (binding: LedgerBinding) (snapshot: LedgerSnapshot)
                                   (launch: Launch) = result {
        let! abortId =
            match launch.State with
            | LaunchState.TerminalPendingRelease marker
                when marker.StartsWith("abort:", StringComparison.Ordinal) ->
                match Guid.TryParse(marker.Substring("abort:".Length)) with
                | true, value when value <> Guid.Empty -> Ok(value.ToString("D"))
                | _ -> Error(integrity "unbound abort has malformed terminal UUID")
            | _ -> Error(integrity "C01 verification requires a terminal abort tombstone")
        let projectId = binding.ProjectId.ToString("D")
        if launch.ProjectId <> projectId || launch.Version <> 0 || launch.RoleVersion < 1
           || String.IsNullOrWhiteSpace launch.RoleId
           || launch.Credit.IsSome || launch.PrimaryWorkId.IsSome || launch.Lease.IsSome
           || launch.EverClaimed || launch.DispatchCount <> 0 then
            return! Error(integrity "unbound abort is not an inert no-session tombstone")
        let! decision, decisionOid =
            WorkQueueGitLedger.readAdmissionAt binding snapshot launch.Id
            |> Result.bind (function
                | Some (record, oid) when record.SchemaVersion = 1
                                          && record.ProjectId = projectId
                                          && record.LaunchId = launch.Id
                                          && record.AccountTokenId = launch.AccountTokenId
                                          && record.AdmissionOperationId = launch.AccountTokenId
                                          && record.AbortOperationId = abortId
                                          && record.Kind = "AbortOnlyUnbound" -> Ok(record, oid)
                | _ -> Error(integrity "missing immutable original C01 abort-only decision"))
        let witnessPath = WorkQueueGitLedger.admissionPath launch.Id
        let! operation, issued =
            operationOrigin binding snapshot abortId "WQ02.Mutate"
                [WorkQueueGitLedger.RecordPath; witnessPath]
        if operation.Paths <> [|WorkQueueGitLedger.RecordPath; witnessPath|] then
            return! Error(integrity "C01 abort operation wrote unexpected paths")
        if not (requestMatches operation (fun body ->
            let mutation = body.GetProperty("mutation")
            let fields = mutation.GetProperty("fields").EnumerateArray() |> Seq.toArray
            mutation.GetProperty("case").GetString() = "AbortUnboundProjectAdmission"
            && body.GetProperty("schemaVersion").GetInt32() = 1
            && fields.Length = 2
            && fields.[1].GetString() = decision.AdmissionOperationId
            && fields.[0].GetProperty("Id").GetString() = launch.Id
            && fields.[0].GetProperty("ProjectId").GetString() = projectId
            && fields.[0].GetProperty("AccountTokenId").GetString() = launch.AccountTokenId
            && fields.[0].GetProperty("RoleId").GetString() = launch.RoleId
            && fields.[0].GetProperty("RoleVersion").GetInt32() = launch.RoleVersion
            && fields.[0].GetProperty("Version").GetInt32() = 0
            && fields.[0].GetProperty("State").GetProperty("case").GetString() = "ReservedUnregistered")) then
            return! Error(integrity "original C01 command does not match the unbound launch identity")
        let! originalDecision, originalOid =
            WorkQueueGitLedger.readAdmissionAt binding issued launch.Id
            |> Result.bind (function
                | Some evidence -> Ok evidence
                | _ -> Error(integrity "original C01 commit omitted abort-only decision"))
        if originalOid <> decisionOid || originalDecision <> decision then
            return! Error(integrity "C01 abort-only decision changed after its original CAS")
        let! originalState = WorkQueueGitLedger.loadAt binding issued
        match originalState.Launches.TryFind launch.Id,
              originalState.Receipts.TryFind abortId with
        | Some original, Some(ProjectOperationPayload.OtherMutation text)
            when original = launch
                 && text = "abort-unbound|" + launch.Id
                 && not (originalState.Receipts.ContainsKey decision.AdmissionOperationId) ->
            let! roleWitness = LedgerStore.readAt binding snapshot (roleAdmissionPath launch.Id)
            if roleWitness <> (None, None) then
                return! Error(integrity "C01 no-session tombstone contains a forged role admission")
            return ()
        | _ ->
            return! Error(integrity "original C01 project state lacks exclusive abort-only tombstone")
    }

    let private verifyLaunchAdmission (binding: LedgerBinding) (snapshot: LedgerSnapshot)
                                      (catalog: RoleCatalog) (launch: Launch) =
        match launch.State with
        | LaunchState.TerminalPendingRelease marker
            when marker.StartsWith("abort:", StringComparison.Ordinal) ->
            verifyAbortedLaunch binding snapshot launch
        | _ -> verifyAdmittedLaunch binding snapshot catalog launch

    let loadAt (binding: LedgerBinding) (snapshot: LedgerSnapshot) =
        WorkQueueGitLedger.loadAt binding snapshot
        |> Result.bind (fun project ->
            LedgerStore.readAt binding snapshot WorkQueueGitLedger.RecordPath
            |> Result.bind (function
                | Some _, Some projectOid ->
                    readCatalogAt binding snapshot
                    |> Result.bind (fun (catalog, catalogOid, provisionId) ->
                        verifyRoleHistory binding snapshot catalog provisionId
                        |> Result.bind (fun () ->
                            // Even a numerically matching legacy launch has no WQ04
                            // authority without a role-specific admission at
                            // the ORIGINAL project CAS commit.
                            project.Launches
                            |> Map.toList
                            |> List.map snd
                            |> List.fold (fun state launch ->
                                state |> Result.bind (fun () ->
                                    verifyLaunchAdmission binding snapshot catalog launch)) (Ok ())
                            |> Result.map (fun () ->
                                { Project = project; Catalog = catalog
                                  Proof =
                                    { ProjectId = binding.ProjectId.ToString("D")
                                      LedgerId = binding.LedgerId.ToString("D")
                                      Epoch = binding.Epoch
                                      Head = snapshot.Commit
                                      ProjectBlobOid = projectOid
                                      ProjectVersion = project.Version
                                      CatalogBlobOid = catalogOid
                                      CatalogRevision = catalog.Revision
                                      ProvisionOperationId = provisionId } })))
                | _ -> Error(LedgerIntegrityError "WQ04 project head/blob provenance incomplete.")))

    let load binding =
        LedgerStore.fetch binding |> Result.bind (loadAt binding)

    let verifyAt binding snapshot (proof: RoleRevisionProof) =
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
                                // Candidate eligibility is weaker than the first
                                // feasible deterministic outcome. Reject a caller
                                // choosing an alternate eligible stage, even during
                                // read-only preflight. This still does NOT prove
                                // the actual Contracts.claimNext CAS outcome.
                                let other = live |> List.filter (fun l -> l.Id <> launchId)
                                let rec first = function
                                    | [] -> Error(roleFailure "no feasible claim for pinned role")
                                    | candidate :: rest when eligible project role candidate ->
                                        match rematch view
                                            (candidates |> List.filter (fun w -> w.Id <> candidate.Id))
                                            other with
                                        | Ok () -> Ok candidate.Id
                                        | Error _ -> first rest
                                    | _ :: rest -> first rest
                                first candidates
                                |> Result.bind (fun selected ->
                                    if selected = workId && eligible project role work then Ok ()
                                    else Error(roleFailure
                                        "caller-selected work is not the first feasible pinned-role candidate")))
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
              RequiresCanonicalSourceAndAccountProof = true
              RequiresSameCasChosenWorkAndCreditProof = true })

    // This is the reusable joint-CAS guard. A later work-queue writer MUST call
    // it INSIDE LedgerStore.transact's planner and produce its project changes
    // from the same snapshot. It re-reads everything; no caller-provided booleans
    // or historical views can assert authorization.
    let inspectAt binding snapshot proof useKind =
        verifyAt binding snapshot proof |> Result.bind (fun view -> inspect view useKind)

    // NO executable claim authority is available from this isolated role
    // adapter. The eventual combined writer must run inside one project Git
    // CAS planner, re-evaluate exact role history, account/source provenance,
    // Contracts.claimNext's ACTUAL chosen work, claimed stage generation and
    // all resulting credit assignments before publishing anything.
    //
    // In particular, even a successful inspectAt(ClaimWork ...) is NOT a
    // permission, and WQ02's unversioned Project.Subscriptions cannot stand in
    // for the pinned versioned role catalog. This method deliberately denies
    // an attempted operational handoff until the joint writer exists.
    let authorizeClaimAt _binding _snapshot _proof _launchId _workId =
        Error(LedgerIntegrityError
            "WQ04 claim denied: no same-CAS canonical selected-work/credit, role, source, account and generation writer.")

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
                |> Result.bind (fun project ->
                    LedgerStore.readAt binding snapshot CatalogPath
                    |> Result.bind (function
                        | Some _, _ -> Error(InvalidBinding "WQ04 catalog already provisioned.")
                        | None, None when not project.Launches.IsEmpty ->
                            Error(LedgerIntegrityError
                                "WQ04 role catalog cannot be backfilled after any legacy launch; independently authenticated migration unavailable.")
                        | None, None ->
                            let catalog = Producer.emptyRoleCatalog
                            Ok [{ Path = CatalogPath; ExpectedBlobOid = None
                                  Content = encode ((binding: LedgerBinding).ProjectId.ToString("D"))
                                      (operationId.ToString("D")) catalog }]
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
                loadAt binding snapshot
                |> Result.bind (fun view ->
                    let before = view.Catalog
                    let oid = view.Proof.CatalogBlobOid
                    let provisionId = view.Proof.ProvisionOperationId
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
                                      Content = encode ((binding: LedgerBinding).ProjectId.ToString("D"))
                                          provisionId after }])))

    /// Versioned read-only role demand, based on one verified canonical Git
    /// snapshot. No caller high-water or admission grant is manufactured.
    let selectPracticalDemand binding roleId =
        load binding
        |> Result.bind (fun pinned ->
            Producer.selectRoleDemandPractical pinned.Catalog pinned.Project roleId
            |> Result.mapError (fun rejected ->
                InvalidBinding (sprintf "WQ04 practical role demand: %A" rejected)))
