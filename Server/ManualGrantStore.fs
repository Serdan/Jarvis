namespace Server

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open Common

// Manual, operator-supervised authority is deliberately NOT a Work reservation.
// Only a verifier and the replaceable approval/scope state are kept in RAM.
type ManualGrantScope = { Purpose: string; ProjectName: string; ResourceKind: string; Capabilities: Set<string> }
type ManualGrantTicket = { Credential: string; Scope: ManualGrantScope }
type ManualGrantStatus = Pending | Approved | Denied | Creating | Created | CreationBlocked
type private ManualGrantEntry =
    { Owner: string
      Scope: ManualGrantScope
      mutable Status: ManualGrantStatus
      mutable CanonicalProjectId: string option }

module ManualGrantPolicy =
    let families = set [ "ContextRead"; "Implementation"; "Prerequisite"; "Review"; "Integration"; "SchedulingAdmin" ]

    let family = AgentCommandInfo.manualGrantFamily

    let target = function
        | CreateProjectCommand cmd -> Some cmd.Name
        | command ->
            AgentCommandInfo.projectName command
            |> Option.bind (fun selector ->
                match ProjectSelector.parse selector with
                | Some (projectName, _) -> Some projectName
                | None -> None)

    let private validProjectName (name: string) =
        not (String.IsNullOrWhiteSpace name) && name.Length <= 128 && name = name.Trim()
        && name <> "." && name <> ".." && not (name.StartsWith(".", StringComparison.Ordinal))
        && not (name.Contains("..", StringComparison.Ordinal))
        && not (name.Contains("::", StringComparison.Ordinal))
        && not (name |> Seq.exists (fun ch -> Char.IsControl ch || ch = '/' || ch = '\\'))
        && (match ProjectSelector.parse name with Some(project, None) -> project = name | _ -> false)

    let validate (owner: string) (kind: string) (project: string) (purpose: string) (requested: string list) =
        if String.IsNullOrWhiteSpace owner then Error "An authenticated account owner is required."
        elif kind <> "existing" && kind <> "prospective" then Error "resourceKind must be existing or prospective."
        elif not (validProjectName project) then Error "projectName must be one canonical project identifier."
        elif String.IsNullOrWhiteSpace purpose || purpose.Length > 512 || purpose <> purpose.Trim()
             || (purpose |> Seq.exists Char.IsControl) then Error "purpose must be 1-512 printable trimmed characters."
        elif isNull (box requested) || List.isEmpty requested || requested.Length > families.Count
             || requested |> List.exists (fun name -> not (families.Contains name)) then
            Error "capabilities must contain only the six documented Work capability categories."
        else Ok { Purpose = purpose; ProjectName = project; ResourceKind = kind; Capabilities = Set.ofList requested }

    let canonicalUuidV4 (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && (match Guid.TryParseExact(value, "D") with
            | true, parsed ->
                parsed <> Guid.Empty && parsed.ToString("D") = value
                && value.[14] = '4' && "89ab".Contains(value.[19])
            | _ -> false)

type ManualGrantStore() =
    let syncRoot = obj()
    // SHA-256 of 122-bit CSPRNG UUIDv4: never retain bearer preimages.
    let entries = Dictionary<string, ManualGrantEntry>(StringComparer.Ordinal)
    let digest (token: string) =
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes token))
    let find owner token =
        if not (ManualGrantPolicy.canonicalUuidV4 token) then None
        else
            match entries.TryGetValue(digest token) with
            | true, entry when String.Equals(entry.Owner, owner, StringComparison.Ordinal) -> Some entry
            | _ -> None

    member _.Request(owner, kind, projectName, purpose, capabilities: string list) =
        lock syncRoot (fun () ->
            match ManualGrantPolicy.validate owner kind projectName purpose capabilities with
            | Error message -> Error message
            | Ok scope when entries.Count >= 4096 ->
                Error "Ephemeral manual-grant verifier capacity is exhausted until server restart."
            | Ok scope ->
                let credential = Guid.NewGuid().ToString("D")
                let entry = { Owner = owner; Scope = scope; Status = Pending; CanonicalProjectId = None }
                entries.Add(digest credential, entry)
                Ok { Credential = credential; Scope = scope })

    member _.Decide(owner, credential, approve: bool) =
        lock syncRoot (fun () ->
            match find owner credential with
            | Some entry when entry.Status = Pending ->
                entry.Status <- if approve then Approved else Denied
                true
            | _ -> false)

    member _.Status(owner, credential) =
        lock syncRoot (fun () -> find owner credential |> Option.map (fun entry -> entry.Status))

    // Only approved, in-scope operation *families* reach the host.
    // Prospective creation is an atomic one-use transition: all subsequent
    // operations are forbidden until the returned canonical project name binds.
    member _.ExpectedCanonicalProjectId(owner, credential) =
        lock syncRoot (fun () ->
            match find owner credential with
            | Some entry when entry.Status = Created -> entry.CanonicalProjectId
            | _ -> None)

    member _.Authorize(owner, credential, command: AgentCommand, observedCanonicalProjectId: string option) =
        lock syncRoot (fun () ->
            match find owner credential with
            | None -> Error "Unknown manual grant credential or account."
            | Some entry when entry.Status = Pending -> Error "Manual grant is awaiting operator approval."
            | Some entry when entry.Status = Denied -> Error "Manual grant was denied."
            | Some entry when entry.Status = Creating -> Error "Project creation is awaiting a verified result."
            | Some entry when entry.Status = CreationBlocked -> Error "Project creation outcome is unknown; request a new scoped grant."
            | Some entry ->
                match ManualGrantPolicy.family command, ManualGrantPolicy.target command with
                | Some family, Some name when entry.Scope.Capabilities.Contains family
                                              && name = entry.Scope.ProjectName ->
                    match entry.Scope.ResourceKind, entry.Status, command with
                    | "prospective", Approved, CreateProjectCommand setup when setup.InitializeLedger ->
                        entry.Status <- Creating
                        Ok true
                    | "prospective", Approved, CreateProjectCommand _ ->
                        Error "Prospective project creation must initialize an approved canonical Git ledger."
                    | "prospective", Created, CreateProjectCommand _ ->
                        Error "Project creation grant has already been consumed."
                    | "prospective", Created, _ when entry.CanonicalProjectId.IsSome
                                                  && observedCanonicalProjectId = entry.CanonicalProjectId ->
                        Ok false
                    | "prospective", Created, _ ->
                        Error "Canonical project identity was not independently verified for this operation."
                    | "existing", Approved, CreateProjectCommand _ ->
                        Error "Existing-project grants cannot create projects."
                    | "existing", Approved, _ -> Ok false
                    | _ -> Error "Prospective project must be created and canonically bound first."
                | None, _ -> Error "This operation is not delegable through a manual grant."
                | Some _, None -> Error "Operation has no project scope."
                | Some family, _ when not (entry.Scope.Capabilities.Contains family) ->
                    Error "Operation family is outside the approved manual grant capabilities."
                | _ -> Error "Operation targets an unrelated project.")

    member _.FinishCreation(owner, credential, createdProject: (string * string) option) =
        lock syncRoot (fun () ->
            match find owner credential with
            | Some entry when entry.Status = Creating ->
                let verifiedId =
                    match createdProject with
                    | Some(name, projectId) when name = entry.Scope.ProjectName ->
                        match Guid.TryParseExact(projectId, "D") with
                        | true, guid when guid <> Guid.Empty
                                          && guid.ToString("D") = projectId -> Some projectId
                        | _ -> None
                    | _ -> None
                entry.CanonicalProjectId <- verifiedId
                entry.Status <- if verifiedId.IsSome then Created else CreationBlocked
                entry.Status = Created
            | _ -> false)

    member _.VerifierCount = lock syncRoot (fun () -> entries.Count)
