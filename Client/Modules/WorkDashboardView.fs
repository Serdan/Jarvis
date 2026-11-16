module Client.WorkDashboardView

open System
open System.Globalization

// STAGED, NOT PRODUCTION-BOUND. The WorkQueueToolFacade module is not on
// integrated main at this source baseline. The host binding to install after
// that facade is accepted is:
//   1. Enumerate actual trusted LedgerBinding projects in the client host.
//   2. For each binding, call WorkQueueToolFacade.readWithBinding
//      binding "List" "All" "" (a pinned Git-backed read).
//   3. Copy every WorkToolSnapshot field and WorkToolRow/WorkDependencyReason
//      field to the DTOs below, setting SubjectTitle=None unless a title was
//      independently verified at that *same* canonical Git head.
//   4. Preserve one ProjectSource per bound project, using Error reason on
//      incomplete/unbound reads; no stale cache or legacy Agent substitutions.
//   5. Inject that per-project Load function through ConsoleTui.SetWorkAccess.
// There is no command to claim/complete Work or start Scheduler in this view.
// Until that explicit host adapter is installed, the tab displays NotReady.
type Dependency =
    { WorkId: string
      StageGeneration: int option
      ClaimGeneration: int option
      State: string
      Satisfied: bool
      WaiverReceipt: string option
      Reason: string }

type WorkRow =
    { WorkId: string
      SubjectId: string
      SubjectTitle: string option
      QueueId: string
      Kind: string
      State: string
      Category: string
      StageGeneration: int
      ClaimGeneration: int
      GraphVersion: int
      Gate: string
      Domain: string
      ClaimLaunchId: string option
      SessionUuid: string option
      Liveness: string
      Dependencies: Dependency list
      Reasons: string list
      ClaimEnabled: bool }

type Snapshot =
    { ProjectId: string
      VerifiedGitHead: string
      ProjectVersion: int
      GraphVersion: int
      Coverage: string
      RoleAuthority: string
      ClaimBinding: string
      Work: WorkRow list
      ReadOnly: bool }

type ProjectSource =
    { ProjectName: string
      Snapshot: Result<Snapshot, string> }

type DisplayRow =
    { Key: string
      Project: string
      WorkId: string
      Subject: string
      QueueAndKind: string
      Status: string
      Category: string
      Details: string list }

let private safe (value: string) =
    if isNull value then "(null)"
    else
        value
        |> Seq.map (fun ch ->
            match Char.GetUnicodeCategory ch with
            | UnicodeCategory.Format
            | UnicodeCategory.LineSeparator
            | UnicodeCategory.ParagraphSeparator -> ' '
            | _ when Char.IsControl ch -> ' '
            | _ -> ch)
        |> Seq.toArray |> String

let fit width value =
    if width <= 0 then ""
    else
        let value = safe value
        if value.Length <= width then value
        elif width = 1 then "…"
        else value.Substring(0, width - 1) + "…"

let private validHead (head: string) =
    not (String.IsNullOrWhiteSpace head)
    // A verified project Git head can be a lowercase SHA-1 or SHA-256 OID.
    && (head.Length = 40 || head.Length = 64)
    && (head |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))

let private rank = function
    | "Available" -> 0
    | "Blocked" -> 1
    | "Unknown" -> 2
    | "Active" -> 3
    | "Terminal" -> 4
    | _ -> 5

let private workStatus (snapshot: Snapshot) (row: WorkRow) =
    let coverageVerified = snapshot.Coverage = "Verified" && validHead snapshot.VerifiedGitHead
    if not coverageVerified || not snapshot.ReadOnly then
        "UNKNOWN / NotReady"
    else
        match row.Category with
        | "Available" ->
            if not row.ClaimEnabled then "NotReady (queue available)"
            elif snapshot.RoleAuthority.StartsWith("UNKNOWN", StringComparison.OrdinalIgnoreCase) then
                "UNKNOWN role"
            else "Available (observation)"
        | "Blocked" -> "Blocked"
        | "Unknown" -> "UNKNOWN"
        | "Active" ->
            // Category=Active records the Work claim, not proof that its
            // associated execution is live. Require an explicitly recognized
            // positive observation and both bound identities; all other
            // strings (including stale/stopped/unbound) fail closed.
            let identified =
                (row.ClaimLaunchId |> Option.exists (not << String.IsNullOrWhiteSpace))
                && (row.SessionUuid |> Option.exists (not << String.IsNullOrWhiteSpace))
            let observedLive =
                match row.Liveness with
                | "Running" | "Live (verified)" -> true
                | _ -> false
            if identified && observedLive then "Active (observed)"
            else "Claimed; session UNKNOWN"
        | "Terminal" -> "Terminal"
        | _ -> "UNKNOWN / NotReady"

let private details project snapshot (row: WorkRow) status =
    let strOption item = item |> Option.defaultValue "(none / unverified)"
    let blockers =
        row.Dependencies
        |> List.map (fun dep ->
            sprintf "Dependency: %s | stage v%s / claim v%s | %s | %s%s"
                dep.WorkId
                (dep.StageGeneration |> Option.map string |> Option.defaultValue "?")
                (dep.ClaimGeneration |> Option.map string |> Option.defaultValue "?")
                (if dep.Satisfied then "satisfied" else "UNMET")
                dep.Reason
                (dep.WaiverReceipt |> Option.map (fun receipt -> " | waiver: " + receipt) |> Option.defaultValue ""))
    [ "Work: " + row.WorkId
      "Project: " + project + " | canonical project ID: " + snapshot.ProjectId
      "Subject: " + (row.SubjectTitle |> Option.defaultValue row.SubjectId) + " [" + row.SubjectId + "]"
      "Queue: " + row.QueueId + " | kind: " + row.Kind
      sprintf "Category: %s | readiness: %s | state: %s" row.Category status row.State
      sprintf "Work stage v%d / claim v%d / graph v%d" row.StageGeneration row.ClaimGeneration row.GraphVersion
      sprintf "Snapshot: Git %s / project v%d / graph v%d" snapshot.VerifiedGitHead snapshot.ProjectVersion snapshot.GraphVersion
      "Git coverage: " + snapshot.Coverage
      "Role authority: " + snapshot.RoleAuthority
      "Source/domain: " + row.Domain + " | gate: " + row.Gate
      "Claim authority: " + snapshot.ClaimBinding
      "Account admission: UNKNOWN - no verified cross-ledger capacity/CAS join in this view"
      "Original effect: UNKNOWN - original send/result journal is not joined to this snapshot"
      "Claim launch (not owner): " + strOption row.ClaimLaunchId
      "Session UUID (not owner): " + strOption row.SessionUuid
      "Session liveness: " + row.Liveness ]
    @ blockers
    @ (row.Reasons |> List.map (fun reason -> "Reason: " + reason))
    |> List.map safe

let private unavailable project reason =
    { Key = project + "::(unbound)"
      Project = project; WorkId = "(unbound)"
      Subject = "Pinned Work source unavailable"
      QueueAndKind = "-"
      Status = "NotReady / UNKNOWN"; Category = "Unknown"
      Details = [ "Project: " + project
                  "UNKNOWN / NotReady: " + reason
                  "No legacy Agent or last-good cached data has been substituted." ] |> List.map safe }

let private projectRows (project: ProjectSource) =
    match project.Snapshot with
    | Error reason -> [ unavailable project.ProjectName reason ]
    | Ok snapshot ->
        let rows =
            snapshot.Work
            |> List.mapi (fun index row -> index, row)
            // Preserve the facade's original ordering within a classification.
            |> List.sortBy (fun (index, row) -> rank row.Category, index)
            |> List.map (fun (_, row) ->
                let status = workStatus snapshot row
                { Key = snapshot.ProjectId + "::" + row.WorkId
                  Project = project.ProjectName
                  WorkId = row.WorkId
                  Subject = row.SubjectTitle |> Option.defaultValue row.SubjectId
                  QueueAndKind = row.QueueId + "/" + row.Kind
                  Category = row.Category
                  Status = status
                  Details = details project.ProjectName snapshot row status })
        if not (validHead snapshot.VerifiedGitHead)
           || snapshot.Coverage <> "Verified"
           || not snapshot.ReadOnly then
            unavailable project.ProjectName
                ("Source coverage " + snapshot.Coverage + "; Git head " + snapshot.VerifiedGitHead
                 + "; read-only contract " + string snapshot.ReadOnly) :: rows
        else rows

let allRows projects =
    projects
    |> List.sortBy (fun source -> source.ProjectName.ToUpperInvariant(),
                                  (match source.Snapshot with Ok snapshot -> snapshot.ProjectId | Error _ -> ""))
    |> List.collect projectRows

let visibleRows (filter: string) projects =
    let query = if isNull filter then "" else filter.Trim()
    let rows = allRows projects
    if query = "" then rows
    else
        rows |> List.filter (fun row ->
            [ row.Project; row.WorkId; row.Subject; row.QueueAndKind; row.Status; row.Category ]
            |> List.exists (fun field -> field.Contains(query, StringComparison.OrdinalIgnoreCase)))

let normalizeSelection current rows =
    match current with
    | Some key when rows |> List.exists (fun row -> row.Key = key) -> Some key
    | _ -> rows |> List.tryHead |> Option.map _.Key

let moveSelection current delta rows =
    match rows with
    | [] -> None
    | _ ->
        let items = rows |> List.toArray
        let index = items |> Array.tryFindIndex (fun row -> Some row.Key = current) |> Option.defaultValue 0
        Some items[max 0 (min (items.Length - 1) (index + delta))].Key

let displayLine width selected (row: DisplayRow) =
    let marker = if Some row.Key = selected then ">" else " "
    // Readiness is the safety-critical field: keep it ahead of unbounded
    // project IDs, Work IDs, titles and queue labels during truncation.
    fit width (sprintf "%s %s | %s | %s | %s | %s"
                   marker row.Status row.Project row.WorkId row.Subject row.QueueAndKind)

let detailLines width (row: DisplayRow) =
    row.Details |> List.map (fit width)
