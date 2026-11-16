module Client.WorkQueueDashboardReadBinding

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open Client.LedgerStore
open Client.WorkQueueToolFacade
open Client.WorkDashboardView
open Client.ConsoleTui

// The host, not the dashboard or this module, supplies the complete
// operator-approved account/project registry. No filesystem discovery, legacy
// Agent roster, cached snapshot or server telemetry can authorize a project.
type ApprovedProjectIdentity =
    { ProjectId: Guid
      DisplayName: string }

type ApprovedAccountProjects =
    { AccountId: string
      MembershipComplete: bool
      Projects: ApprovedProjectIdentity list
      Bindings: LedgerBinding list }

type AuthorityPort = unit -> Result<ApprovedAccountProjects, string>

let private uniqueBy (key: 'a -> string) (items: 'a list) =
    let seen = HashSet<string>(StringComparer.OrdinalIgnoreCase)
    items |> List.forall (fun item -> seen.Add(key item))

let private validate (source: ApprovedAccountProjects) =
    if String.IsNullOrWhiteSpace source.AccountId
       || List.isEmpty source.Projects then
        Error "The approved account and its full project membership must be explicit."
    elif source.Projects |> List.exists (fun p ->
        p.ProjectId = Guid.Empty || String.IsNullOrWhiteSpace p.DisplayName
        || p.DisplayName <> p.DisplayName.Trim()) then
        Error "Approved projects require stable nonempty IDs and display names."
    elif not (uniqueBy (fun (p: ApprovedProjectIdentity) -> p.ProjectId.ToString("D")) source.Projects)
         || not (uniqueBy (fun (p: ApprovedProjectIdentity) -> p.DisplayName) source.Projects) then
        Error "Duplicate approved project ID or display identity."
    elif source.Bindings |> List.exists (fun b ->
        b.ProjectId = Guid.Empty || b.LedgerId = Guid.Empty || b.Epoch < 1
        || String.IsNullOrWhiteSpace b.RepositoryPath
        || String.IsNullOrWhiteSpace b.RemoteName
        || String.IsNullOrWhiteSpace b.ApprovedFetchUrl
        || String.IsNullOrWhiteSpace b.ApprovedPushUrl) then
        Error "A project binding has incomplete pinned source/ledger authority."
    elif not (uniqueBy (fun (b: LedgerBinding) -> b.ProjectId.ToString("D")) source.Bindings)
         || not (uniqueBy (fun (b: LedgerBinding) -> b.LedgerId.ToString("D")) source.Bindings)
         || not (uniqueBy (fun (b: LedgerBinding) -> Path.GetFullPath b.RepositoryPath) source.Bindings)
         || not (uniqueBy (fun (b: LedgerBinding) -> b.ApprovedFetchUrl + "\n" + b.ApprovedPushUrl) source.Bindings) then
        Error "Duplicate project, ledger, checkout or approved remote authority."
    else
        let approvedIds = source.Projects |> List.map _.ProjectId |> Set.ofList
        if source.Bindings |> List.exists (fun b -> not (approvedIds.Contains b.ProjectId)) then
            Error "A project binding is not in the explicitly approved account membership."
        else Ok ()

let private toDependency (item: WorkDependencyReason) : Dependency =
    { WorkId = item.WorkId
      StageGeneration = item.StageGeneration
      ClaimGeneration = item.ClaimGeneration
      State = item.State
      Satisfied = item.Satisfied
      WaiverReceipt = item.WaiverReceipt
      Reason = item.Reason }

let private toRow (item: WorkToolRow) : WorkRow =
    { WorkId = item.WorkId
      SubjectId = item.SubjectId
      SubjectTitle = None // No same-head subject-title authority is supplied.
      QueueId = item.QueueId
      Kind = item.Kind
      State = item.State
      Category = item.Category
      StageGeneration = item.StageGeneration
      ClaimGeneration = item.ClaimGeneration
      GraphVersion = item.GraphVersion
      Gate = item.Gate
      Domain = item.Domain
      ClaimLaunchId = item.ClaimLaunchId
      SessionUuid = item.SessionUuid
      Liveness = item.Liveness
      Dependencies = item.Dependencies |> List.map toDependency
      Reasons = item.Reasons
      ClaimEnabled = item.ClaimEnabled }

let private validHead (head: string) =
    not (String.IsNullOrWhiteSpace head)
    // Match LedgerStore's Git object-ID grammar: lowercase SHA-1 or SHA-256.
    && (head.Length = 40 || head.Length = 64)
    && (head |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))

// WQ02 queue coverage and WQ04 role provenance are independent evidence.
// Only the facade's exact positive, same-head WQ04 observation is accepted;
// "UNKNOWN" or arbitrary non-mismatch text is not role authority.
let private hasPinnedRoleProof (view: WorkToolSnapshot) =
    let prefix = "Pinned WQ04 catalog revision "
    let suffix = " at " + view.VerifiedGitHead + " (observation only)"
    if String.IsNullOrWhiteSpace view.RoleAuthority
       || not (view.RoleAuthority.StartsWith(prefix, StringComparison.Ordinal))
       || not (view.RoleAuthority.EndsWith(suffix, StringComparison.Ordinal))
       || view.RoleAuthority.Length <= prefix.Length + suffix.Length then
        false
    else
        let revision =
            view.RoleAuthority.Substring(prefix.Length,
                view.RoleAuthority.Length - prefix.Length - suffix.Length)
        match Int32.TryParse(revision, NumberStyles.None, CultureInfo.InvariantCulture) with
        | true, value -> value >= 0
        | false, _ -> false

let private toSnapshot (binding: LedgerBinding) (view: WorkToolSnapshot) : Result<Snapshot, string> =
    if view.ProjectId <> binding.ProjectId.ToString("D") then
        Error "UNKNOWN: Work snapshot project identity differs from approved Git binding."
    elif not (validHead view.VerifiedGitHead) then
        Error "UNKNOWN: no valid verified project Git head."
    elif not view.ReadOnly || view.Work |> List.exists _.ClaimEnabled then
        Error "UNKNOWN: Work read provider is not strictly read-only."
    elif view.Coverage <> "Verified" then
        Error ("UNKNOWN: incomplete pinned Work coverage: " + view.Coverage)
    elif not (hasPinnedRoleProof view) then
        Error ("UNKNOWN: missing or inconsistent pinned WQ04 role authority: " + view.RoleAuthority)
    else
        Ok { ProjectId = view.ProjectId
             VerifiedGitHead = view.VerifiedGitHead
             ProjectVersion = view.ProjectVersion
             GraphVersion = view.GraphVersion
             Coverage = view.Coverage
             RoleAuthority = view.RoleAuthority
             ClaimBinding = view.ClaimBinding
             Work = view.Work |> List.map toRow
             ReadOnly = view.ReadOnly }

let private readProject (binding: LedgerBinding) : Result<Snapshot, string> =
    try
        match WorkQueueToolFacade.readWithBinding binding "List" "All" "" with
        | Error issue -> Error (sprintf "UNKNOWN: verified project Git read failed: %A" issue)
        | Ok view -> toSnapshot binding view
    with ex -> Error ("UNKNOWN: verified project Git read unavailable: " + ex.Message)

let private unavailable name reason : ProjectSource =
    { ProjectName = name; Snapshot = Error reason }

/// One synchronous, fresh, read-only load. An unavailable bound project retains
/// its own UNKNOWN row; no last-good cache or legacy Agent data is consulted.
/// The terminal independently rejects late refresh/provider-replacement replies.
let load (approved: ApprovedAccountProjects) : Result<ProjectSource list, string> =
    try
        match validate approved with
        | Error reason -> Error ("UNKNOWN: invalid approved project registry: " + reason)
        | Ok () ->
            let byProject = approved.Bindings |> List.map (fun b -> b.ProjectId, b) |> Map.ofList
            let rows =
                approved.Projects
                |> List.sortBy (fun p -> p.DisplayName.ToUpperInvariant(), p.ProjectId.ToString("D"))
                |> List.map (fun p ->
                    let snapshot =
                        match byProject.TryFind p.ProjectId with
                        | None -> Error "UNKNOWN: approved project has no verified Git binding."
                        | Some binding -> readProject binding
                    { ProjectName = p.DisplayName; Snapshot = snapshot })
            if approved.MembershipComplete then Ok rows
            else
                Ok ((unavailable ("[Account membership UNKNOWN: " + approved.AccountId + "]")
                    "UNKNOWN: operator-approved account project membership is incomplete; no global readiness conclusion.")
                    :: rows)
    with ex ->
        Error ("UNKNOWN: project registry identity or pinned path validation failed: " + ex.Message)

/// Explicit host opt-in only: the host may pass the resulting access to
/// ConsoleTui.SetWorkAccess. This function does not install it or change modes.
let createAccess (authority: AuthorityPort) : WorkUiAccess =
    { Load = fun () ->
        try
            if isNull (box authority) then
                Error "UNKNOWN: no operator-approved project authority port supplied."
            else
                authority ()
                |> Result.mapError (fun error -> "UNKNOWN: approved account registry unavailable: " + error)
                |> Result.bind load
        with ex -> Error ("UNKNOWN: approved account registry failed: " + ex.Message) }
