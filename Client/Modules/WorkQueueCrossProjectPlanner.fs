module Client.WorkQueueCrossProjectPlanner

open System
open Client.AccountCapacityLedger

// WQ08: a pure read-only cross-project planner with fake-only launch intents.
// The real default-off host may inject pinned *observations*, but this module
// has no browser transport, CAS writer, timer or launch allocator. Neither a
// simulated projection nor a WouldLaunch is execution authority.
type PlannerProject =
    { ProjectId: string
      LedgerId: string
      LedgerEpoch: int
      AuthoritySha256: string
      Head: string
      Version: int
      CatalogRevision: int
      CatalogBlobOid: string
      CoverageComplete: bool }

type PlannerProjectSet =
    { CompleteProjectSet: bool
      Projects: PlannerProject list }

type PlannerRole =
    { ProjectId: string
      ProjectHead: string
      CatalogBlobOid: string
      RoleId: string
      Version: int
      Enabled: bool
      Queues: Set<string> }

type PlannerSource =
    { Head: string
      OperationId: string
      RequestHash: string
      SourceRef: string option
      SourceCommit: string option }

type PlannerCandidate =
    { ProjectId: string
      ProjectHead: string
      ProjectVersion: int
      WorkId: string
      WorkGeneration: int
      QueueId: string
      AvailableAt: int64
      AvailableSequence: int64
      Priority: int
      BlocksOtherWork: bool
      RoleId: string
      RoleVersion: int
      CatalogBlobOid: string
      Source: PlannerSource
      // These IDs must already exist in the fixture's durable pre-send intent.
      // The planner neither generates nor registers them.
      LaunchId: string
      OriginalBrowserOperationId: string }

[<RequireQualifiedAccess>]
type PlannerLaunchPhase =
    | ReservedUnregistered
    | SubmittedUnregistered
    | Registered
    | DemandInvalidatedPendingTerminal
    | TerminalPendingRelease

[<RequireQualifiedAccess>]
type PlannerBrowserEvidence =
    | NotRecorded
    | Accepted of receipt: string
    | DefinitiveNoEffect of reason: string
    | Unknown of reason: string

type PlannerExistingLaunch =
    { LaunchId: string
      ProjectId: string
      RoleId: string
      RoleVersion: int
      AccountAdmissionOperationId: string
      Phase: PlannerLaunchPhase
      OriginalBrowserOperationId: string option
      OriginalBrowser: PlannerBrowserEvidence }

type PlannerIssue =
    { Code: string
      Record: string
      NextAction: string }

[<RequireQualifiedAccess>]
type PlannerDecision =
    | WouldLaunch of PlannerCandidate
    | NoCapacity
    | NeedsAttention
    | Unknown

type PlannerReport =
    { Mode: string
      CanDispatch: bool
      AccountHead: string option
      MaxConcurrentAgents: int option
      OccupiedAgents: int option
      Decision: PlannerDecision
      VisibleLaunches: PlannerExistingLaunch list
      Issues: PlannerIssue list }

// Injected ports carry observations, not authorization. The host adapter may
// use trusted read-only Git/project/WQ03 evidence, but executable WQ04/WQ07
// pinned admission remains a separate, independently reviewed cutover.
type FakeOnlyCrossProjectPorts =
    { ReadAccount: unit -> Result<AccountSnapshot, string>
      ReadProjects: unit -> Result<PlannerProjectSet, string>
      ReadRoles: unit -> Result<PlannerRole list, string>
      ReadCandidates: unit -> Result<PlannerCandidate list, string>
      ReadExistingLaunches: unit -> Result<PlannerExistingLaunch list, string> }

module WorkQueueCrossProjectPlanner =
    let private nonBlank (value: string) = not (String.IsNullOrWhiteSpace value)

    // Compare UUID identities, not their caller-provided textual spellings.
    // Keep the originals untouched in returned candidates, launches and issues.
    let private uuidIdentity (value: string) =
        match Guid.TryParse value with
        | true, id when id <> Guid.Empty -> Some id
        | _ -> None

    let private validUuid value = uuidIdentity value |> Option.isSome

    let private issue code record action =
        { Code = code; Record = record; NextAction = action }

    let private compareOrdinal a b = StringComparer.Ordinal.Compare(a, b)

    let private duplicateUuidSpellings (values: string list) =
        values
        // Unissued WQ08 read-only demand has no original launch/browser UUID.
        // It is independently rejected as CandidateIdentityInvalid, but two
        // absent IDs are not one duplicated *issued* identity. Keep canonical
        // UUID alias collisions strict, never invent a placeholder credential.
        |> List.choose (fun value ->
            uuidIdentity value |> Option.map (fun uuid -> uuid, value))
        |> List.groupBy fst
        |> List.choose (fun (_, group) ->
            if List.length group > 1 then
                group |> List.map snd |> List.sortWith compareOrdinal
                |> String.concat ", " |> Some
            else None)
        |> List.sortWith compareOrdinal

    let private report account decision launches issues =
        { Mode = "FakeOnlyReadOnly"
          CanDispatch = false
          AccountHead = account |> Option.map (fun (snapshot: AccountSnapshot) -> snapshot.Head)
          MaxConcurrentAgents = account |> Option.bind (fun snapshot ->
              if isNull (box snapshot.State) then None
              else Some snapshot.State.MaxConcurrentAgents)
          OccupiedAgents = account |> Option.bind (fun snapshot ->
              if isNull (box snapshot.State) || isNull snapshot.State.Tokens then None
              else Some(snapshot.State.Tokens |> Array.filter (fun token -> token.Status <> "Released") |> Array.length))
          Decision = decision
          VisibleLaunches = launches |> List.sortWith (fun left right ->
              let p = compareOrdinal left.ProjectId right.ProjectId
              if p <> 0 then p else compareOrdinal left.LaunchId right.LaunchId)
          Issues = issues |> List.sortWith (fun left right ->
              let code = compareOrdinal left.Code right.Code
              if code <> 0 then code
              else
                  let record = compareOrdinal left.Record right.Record
                  if record <> 0 then record
                  else compareOrdinal left.NextAction right.NextAction) }

    // StringComparer.Ordinal gives stable, culture-independent tie-breaking
    // across client processes and input enumeration orders.

    let private compareCandidates left right =
        let compareInOrder =
            [ compare (if left.BlocksOtherWork then 0 else 1)
                      (if right.BlocksOtherWork then 0 else 1)
              compare right.Priority left.Priority
              compare left.AvailableAt right.AvailableAt
              compareOrdinal left.ProjectId right.ProjectId
              compare left.AvailableSequence right.AvailableSequence
              compareOrdinal left.WorkId right.WorkId
              compareOrdinal left.RoleId right.RoleId
              compare left.RoleVersion right.RoleVersion
              compareOrdinal left.LaunchId right.LaunchId ]
        compareInOrder |> List.tryFind ((<>) 0) |> Option.defaultValue 0

    // Shared deterministic ranking for a real host's inert demand projection.
    // Ranking neither allocates launch IDs nor authorizes dispatch.
    let rankCandidates (candidates: PlannerCandidate list) =
        candidates |> List.sortWith compareCandidates

    let private plan (account: AccountSnapshot) (set: PlannerProjectSet)
                     (roles: PlannerRole list) (candidates: PlannerCandidate list)
                     (launches: PlannerExistingLaunch list) =
        let problems = ResizeArray<PlannerIssue>()
        let add code record action = problems.Add(issue code record action)
        let unknown = ref false
        let problematic = ref false
        let markUnknown code record action =
            unknown.Value <- true
            add code record action
        let markProblem code record action =
            problematic.Value <- true
            add code record action

        if not set.CompleteProjectSet then
            markUnknown "ProjectSetIncomplete" "Project authorities"
                "Read the entire trusted account/project set before cross-project selection."
        if not (nonBlank account.Head) || isNull (box account.State)
           || isNull (box account.State.Tokens) then
            markUnknown "AccountUnknown" "Account capacity"
                "Inspect the original verified account Git head and complete token ledger."
        else
            if account.State.MigrationPhase <> "Activated" then
                markProblem "AccountNotActivated" account.Head
                    "Obtain separate operator approval for account activation; this planner remains default-off."
            if account.State.MaxConcurrentAgents < 1 then
                markProblem "CapacityConfigurationInvalid" account.Head
                    "Repair the account-global concurrency configuration at its trusted Git head."

        let projects = set.Projects |> List.map (fun p -> p.ProjectId, p)
        if List.length projects <> (projects |> List.map fst |> Set.ofList |> Set.count) then
            markProblem "DuplicateProject" "Project authorities"
                "Remove conflicting project snapshots and pin one verified head per project."
        for project in set.Projects do
            if not project.CoverageComplete
               || not (nonBlank project.ProjectId)
               || not (nonBlank project.Head)
               || not (nonBlank project.LedgerId)
               || not (nonBlank project.AuthoritySha256)
               || not (nonBlank project.CatalogBlobOid)
               || project.LedgerEpoch <= 0 || project.Version < 0 || project.CatalogRevision < 0 then
                markUnknown "ProjectProofIncomplete" project.ProjectId
                    "Repair pinned project, role catalog and source coverage at one verified Git head."

        let projectById = projects |> Map.ofList
        if roles |> List.map (fun role -> role.ProjectId, role.RoleId) |> Set.ofList |> Set.count
           <> roles.Length then
            markProblem "DuplicateRole" "Pinned role set"
                "Read exactly one current pinned role version for each project/role identity."
        let roleById = roles |> List.map (fun role -> (role.ProjectId, role.RoleId), role) |> Map.ofList
        for role in roles do
            match projectById.TryFind role.ProjectId with
            | None ->
                markUnknown "RoleProjectUnknown" role.ProjectId
                    "Locate the role's original project authority before selection."
            | Some project when role.ProjectHead <> project.Head
                                || role.CatalogBlobOid <> project.CatalogBlobOid
                                || role.Version <= 0
                                || not (nonBlank role.RoleId)
                                || role.Queues.IsEmpty ->
                markProblem "RoleProofMismatch" (role.ProjectId + "/" + role.RoleId)
                    "Reload the exact role definition, catalog blob and project head."
            | _ -> ()

        for originals in candidates |> List.map (fun candidate -> candidate.LaunchId) |> duplicateUuidSpellings do
            markProblem "DuplicateLaunchIntent" originals
                "Resolve duplicate durable launch intents without minting replacement IDs."
        for originals in candidates |> List.map (fun candidate -> candidate.OriginalBrowserOperationId) |> duplicateUuidSpellings do
            markProblem "DuplicateBrowserIntent" originals
                "Recover original one-shot intents; do not substitute a fresh operation UUID."
        for candidate in candidates do
            let key = candidate.ProjectId + "/" + candidate.WorkId
            match projectById.TryFind candidate.ProjectId,
                  roleById.TryFind(candidate.ProjectId, candidate.RoleId) with
            | None, _ ->
                markUnknown "CandidateProjectUnknown" key
                    "Load the original pinned project authority and its work coverage."
            | _, None ->
                markProblem "CandidateRoleMissing" key
                    "Repair the selected role's current versioned WQ04 definition."
            | Some project, Some role ->
                if candidate.ProjectHead <> project.Head
                   || candidate.ProjectVersion <> project.Version
                   || candidate.CatalogBlobOid <> project.CatalogBlobOid then
                    markProblem "CandidateProjectVersionMismatch" key
                        "Re-read the candidate at the same project/catalog head."
                if role.ProjectHead <> candidate.ProjectHead
                   || role.Version <> candidate.RoleVersion then
                    markProblem "RoleVersionMismatch" key
                        "Rebind to the pinned role version; never silently substitute a newer role."
                if not role.Enabled || role.RoleId = "Manager"
                   || not (role.Queues.Contains candidate.QueueId) then
                    markProblem "RoleNotEligible" key
                        "Use an enabled, non-Manager role subscribed to the candidate queue."
            if not (nonBlank candidate.WorkId)
               || not (nonBlank candidate.QueueId)
               || candidate.WorkGeneration <= 0
               || candidate.AvailableSequence < 0L
               || not (validUuid candidate.LaunchId)
               || not (validUuid candidate.OriginalBrowserOperationId) then
                markProblem "CandidateIdentityInvalid" key
                    "Recover original work generation and persisted launch/browser UUIDs."
            if not (nonBlank candidate.Source.Head)
               || not (nonBlank candidate.Source.OperationId)
               || not (nonBlank candidate.Source.RequestHash) then
                markUnknown "SourceReceiptUnknown" key
                    "Inspect the original immutable source operation, request hash and head."

        for originals in launches |> List.map (fun launch -> launch.LaunchId) |> duplicateUuidSpellings do
            markProblem "DuplicateExistingLaunch" originals
                "Resolve contradictory launch records before planning a new one."
        if launches |> List.exists (fun launch ->
            not (validUuid launch.LaunchId)
            || not (nonBlank launch.RoleId)
            || launch.RoleVersion <= 0
            || (launch.OriginalBrowserOperationId |> Option.exists (validUuid >> not))) then
            markProblem "ExistingLaunchIdentityInvalid" "Existing launch records"
                "Read the original project launch identities and pinned role versions."

        let tokens =
            if isNull (box account.State) || isNull account.State.Tokens then [||]
            else account.State.Tokens
        if tokens |> Array.exists (fun token -> not (validUuid token.LaunchId)) then
            markProblem "AccountTokenIdentityInvalid" "Account tokens"
                "Recover the original account launch UUID before planning."
        let tokensByLaunch = tokens |> Array.groupBy (fun t -> uuidIdentity t.LaunchId) |> Map.ofArray
        let launchesById = launches |> List.map (fun launch -> uuidIdentity launch.LaunchId, launch) |> Map.ofList
        for token in tokens do
            match projectById.TryFind token.ProjectId, launchesById.TryFind (uuidIdentity token.LaunchId) with
            | None, _ when token.Status = "Released" ->
                // Settled historical capacity does not require the retired
                // project's current approved snapshot. Preserve the token for
                // identity/UUID collision checks and audit history.
                ()
            | None, _ ->
                markUnknown "AccountTokenProjectUnknown" token.LaunchId
                    "Read the complete pinned project set for the original account token."
            | _, None when token.Status <> "Released" ->
                markUnknown "AccountTokenLaunchUnknown" token.LaunchId
                    "Find the original project launch/one-shot journal; held capacity is not settled."
            | Some project, Some launch ->
                if token.ProjectLedgerId <> project.LedgerId
                   || token.ProjectLedgerEpoch <> project.LedgerEpoch
                   || token.ProjectAuthoritySha256 <> project.AuthoritySha256
                   || launch.ProjectId <> project.ProjectId
                   || token.RoleId <> launch.RoleId
                   || token.RoleConfigVersion <> int64 launch.RoleVersion
                   || token.AdmissionOperationId <> launch.AccountAdmissionOperationId then
                    markProblem "AccountProjectIdentityMismatch" token.LaunchId
                        "Reconcile original project/account authority and admission operation IDs."
                if token.Status <> "Held" && token.Status <> "Released" then
                    markUnknown "AccountTokenStatusUnknown" token.LaunchId
                        "Inspect the account receipt before treating this slot as free."
            | _ -> ()
        for launch in launches do
            let token = tokensByLaunch.TryFind (uuidIdentity launch.LaunchId)
            match projectById.TryFind launch.ProjectId, token with
            | None, _ ->
                markUnknown "LaunchProjectUnknown" launch.LaunchId
                    "Read the original project authority and launch record."
            | _, None ->
                markUnknown "LaunchAccountTokenMissing" launch.LaunchId
                    "Inspect original global account reservation; absence is not evidence of release."
            | _, Some values when values.Length <> 1 ->
                markProblem "DuplicateAccountToken" launch.LaunchId
                    "Resolve conflicting account reservations before launch or release."
            | _, Some values ->
                let token = values.[0]
                if token.Status = "Released"
                   && launch.Phase <> PlannerLaunchPhase.TerminalPendingRelease then
                    markProblem "ActiveLaunchTokenReleased" launch.LaunchId
                        "Reconcile released capacity with still-active project launch at pinned Git heads."
                if token.Status = "Held" &&
                   (launch.Phase = PlannerLaunchPhase.ReservedUnregistered
                    || launch.Phase = PlannerLaunchPhase.SubmittedUnregistered
                    || launch.Phase = PlannerLaunchPhase.DemandInvalidatedPendingTerminal) then
                    match launch.OriginalBrowserOperationId, launch.OriginalBrowser with
                    | Some original, PlannerBrowserEvidence.Accepted _
                        when validUuid original -> ()
                    | Some original, PlannerBrowserEvidence.DefinitiveNoEffect _
                        when validUuid original -> ()
                    | Some original, PlannerBrowserEvidence.Unknown _
                        when validUuid original ->
                        markUnknown "OriginalBrowserOutcomeUnknown" original
                            "Reconcile the original WQ03 one-shot UUID; never resend from a missing acknowledgment."
                    | Some original, _ when not (validUuid original) ->
                        markProblem "OriginalBrowserUuidInvalid" launch.LaunchId
                            "Recover the original immutable browser operation UUID."
                    | _ ->
                        markUnknown "OriginalBrowserJournalMissing" launch.LaunchId
                            "Locate original WQ03 browser intent/receipt; absence does not prove no effect."

        let existingBrowserIds =
            launches
            |> List.choose (fun launch -> launch.OriginalBrowserOperationId |> Option.bind uuidIdentity)
            |> Set.ofList
        for candidate in candidates do
            if launchesById.ContainsKey (uuidIdentity candidate.LaunchId)
               || tokensByLaunch.ContainsKey (uuidIdentity candidate.LaunchId) then
                markProblem "LaunchIdAlreadyUsed" candidate.LaunchId
                    "Reconcile the original launch intent; do not allocate a new UUID to retry it."
            if uuidIdentity candidate.OriginalBrowserOperationId
               |> Option.exists (fun browserId -> existingBrowserIds.Contains browserId) then
                markProblem "BrowserOperationIdAlreadyUsed" candidate.OriginalBrowserOperationId
                    "Recover the original one-shot operation; never reassign it to a different launch."

        let occupied =
            tokens |> Array.filter (fun token -> token.Status <> "Released") |> Array.length
        let decision =
            if unknown.Value then PlannerDecision.Unknown
            elif problematic.Value then PlannerDecision.NeedsAttention
            elif occupied >= account.State.MaxConcurrentAgents then
                let heldIds =
                    tokens
                    |> Array.filter (fun token -> token.Status <> "Released")
                    |> Array.map (fun token -> token.LaunchId)
                    |> Array.sortWith compareOrdinal
                    |> String.concat ", "
                add "CapacityFull" heldIds
                    "Wait for verified terminal release of original launch/account reservations before another attempt."
                PlannerDecision.NoCapacity
            else
                match candidates |> List.sortWith compareCandidates with
                | head :: _ -> PlannerDecision.WouldLaunch head
                | [] ->
                    add "NoEligibleWork" "Candidate queues"
                        "Wait for new canonical work or resolve upstream queue/dependency gates."
                    PlannerDecision.NeedsAttention
        report (Some account) decision launches (List.ofSeq problems)

    let planFakeOnly (ports: FakeOnlyCrossProjectPorts) : PlannerReport =
        // Each source is sampled exactly once. A failed/throwing port is
        // explicitly Unknown, never converted into an empty queue or free cap.
        let safe label read =
            try read ()
            with ex -> Error(label + ": " + ex.Message)
        let account = safe "account" ports.ReadAccount
        let projects = safe "projects" ports.ReadProjects
        let roles = safe "roles" ports.ReadRoles
        let candidates = safe "candidates" ports.ReadCandidates
        let launches = safe "existing launches" ports.ReadExistingLaunches
        match account, projects, roles, candidates, launches with
        | Ok a, Ok p, Ok r, Ok c, Ok l ->
            try plan a p r c l
            with ex ->
                report None PlannerDecision.Unknown l
                    [ issue "MalformedPlannerEvidence" "Fake-only input" ("Inspect pinned fixture evidence: " + ex.Message) ]
        | _ ->
            let errors =
                [ ("AccountReadUnknown", "account", account |> Result.map (fun _ -> ()))
                  ("ProjectReadUnknown", "projects", projects |> Result.map (fun _ -> ()))
                  ("RoleReadUnknown", "roles", roles |> Result.map (fun _ -> ()))
                  ("CandidateReadUnknown", "candidates", candidates |> Result.map (fun _ -> ()))
                  ("LaunchReadUnknown", "launches", launches |> Result.map (fun _ -> ())) ]
                |> List.choose (fun (code, name, result) ->
                    match result with
                    | Error error ->
                        Some(issue code name
                            ("Inspect the original read-only authority and retry the same IDs: " + error))
                    | Ok () -> None)
            report (account |> Result.toOption) PlannerDecision.Unknown
                   (launches |> Result.defaultValue []) errors
