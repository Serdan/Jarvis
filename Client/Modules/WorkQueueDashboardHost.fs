module Client.WorkQueueDashboardHost

open System
open Client
open Client.LedgerStore
open Client.ConsoleTui
open Client.WorkDashboardView
open Client.WorkQueueDashboardReadBinding
open Client.WorkQueueToolFacade

/// Local, read-only ports. Production ports inspect explicitly approved
/// checkout bindings and read one trust-verified Git snapshot per project.
/// Tests replace only the ports, never the production approval boundary.
type HostPorts =
    { ListGitProjects: unit -> Result<string list, string>
      InspectLocal: string -> Result<LedgerBinding * string, string>
      ReadPinned: string -> Result<LedgerBinding * WorkToolSnapshot, string> }

let private validHead (head: string) =
    not (String.IsNullOrWhiteSpace head)
    && (head.Length = 40 || head.Length = 64)
    && (head |> Seq.forall (fun c ->
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))

let private guarded call =
    try call () with _ -> Error "UNKNOWN: local Work authority/read operation failed."

let private unavailable project reason : ProjectSource =
    { ProjectName = project; Snapshot = Error reason }

/// A fresh host read always rechecks local trust both before and after the
/// verified Git load. No older successful result is retained. The local Git
/// project inventory is NOT a complete operator account/project registry.
let loadWithPorts (ports: HostPorts) : Result<ProjectSource list, string> =
    match guarded ports.ListGitProjects with
    | Error _ -> Error "UNKNOWN: authorized local Git project inventory unavailable."
    | Ok [] -> Error "UNKNOWN: no local Git projects to inspect for approved Work bindings."
    | Ok projects when
        projects |> List.exists String.IsNullOrWhiteSpace
        || (projects |> List.distinctBy (fun name -> name.ToUpperInvariant()) |> List.length) <> projects.Length ->
        Error "UNKNOWN: local Git project inventory has missing or duplicate identities."
    | Ok projects ->
        let inspected : (string * Result<LedgerBinding * string, string>) list =
            projects
            |> List.sortBy (fun name -> name.ToUpperInvariant(), name)
            |> List.map (fun name -> name, guarded (fun () -> ports.InspectLocal name))
        // Two physical checkouts claiming the same canonical project ID are
        // ambiguous until a separately verified alias policy is supplied.
        let duplicates : Set<Guid> =
            inspected
            |> List.choose (fun (_, inspection) ->
                inspection |> Result.toOption |> Option.map (fun ((binding: LedgerBinding), _) -> binding.ProjectId))
            |> List.countBy id
            |> List.choose (fun (id, count) -> if count > 1 then Some id else None)
            |> Set.ofList
        let sourceFor (name: string, inspection: Result<LedgerBinding * string, string>) =
            match inspection with
            | Error _ ->
                unavailable name "UNKNOWN: no valid, locally approved Git ledger binding for this project."
            | Ok (binding, trustedHead) when not (validHead trustedHead) ->
                unavailable name "UNKNOWN: local Git trust anchor is absent or malformed."
            | Ok (binding, _) when duplicates.Contains binding.ProjectId ->
                unavailable name "UNKNOWN: multiple local Git projects claim the same canonical project identity."
            | Ok (binding, _) ->
                match guarded (fun () -> ports.ReadPinned name) with
                | Error _ ->
                    unavailable name "UNKNOWN: trust-verified Git Work snapshot cannot be loaded."
                | Ok (readBinding, view) when readBinding <> binding ->
                    unavailable name "UNKNOWN: Git Work source identity changed during refresh."
                | Ok (_, view) ->
                    match guarded (fun () -> ports.InspectLocal name) with
                    | Error _ ->
                        unavailable name "UNKNOWN: local Git binding disappeared during refresh."
                    | Ok (afterBinding, afterHead)
                        when afterBinding <> binding || afterHead <> view.VerifiedGitHead ->
                        unavailable name "UNKNOWN: Git trust head or source identity changed during refresh."
                    | Ok _ ->
                        match WorkQueueDashboardReadBinding.toSnapshot binding view with
                        | Error _ ->
                            unavailable name "UNKNOWN: canonical Work coverage or pinned WQ04 role authority is incomplete."
                        | Ok snapshot ->
                            { ProjectName = name; Snapshot = Ok snapshot }
        // There is no verified complete account membership/capacity ledger in
        // this local host inventory. Never imply account-wide readiness.
        let missingAccount =
            unavailable "[Account membership UNKNOWN]"
                "UNKNOWN: local approved Git checkouts do not prove complete account project membership or capacity."
        Ok (missingAccount :: (inspected |> List.map sourceFor))

let createAccessWithPorts (ports: HostPorts) : WorkUiAccess =
    { Load = fun () ->
        try loadWithPorts ports
        with _ -> Error "UNKNOWN: Work host read failed; prior snapshots are unavailable." }

/// Production only: a project is read from the same Git snapshot verified
/// against its local, persistent highest-trusted-head binding.
let createAccess rt : WorkUiAccess =
    let ports =
        { ListGitProjects = (fun () ->
              Client.ProjectBrowser.listGitProjects rt
              |> Result.map (List.map (fun (ProjectName name) -> name))
              |> Result.mapError (fun _ -> "Git project inventory unavailable."))
          InspectLocal = (fun name ->
              Client.OrchestrationApi.LedgerBindingConfig.inspectLocal name rt
              |> Result.mapError (fun _ -> "Local trust binding unavailable."))
          ReadPinned = (fun name ->
              Client.OrchestrationApi.LedgerBindingConfig.loadWithSnapshot name rt
              |> Result.mapError (fun _ -> "Verified Git trust anchor unavailable.")
              |> Result.bind (fun (binding, gitSnapshot) ->
                  Client.WorkQueueGitLedger.WorkQueueGitLedger.loadAt binding gitSnapshot
                  |> Result.mapError (fun _ -> "Pinned WQ02 state unavailable.")
                  |> Result.map (fun project ->
                      let role =
                          Client.WorkQueueRoleAuthority.WorkQueueRoleAuthority.loadAt binding gitSnapshot
                          |> Result.mapError (fun _ -> "Pinned WQ04 authority unavailable.")
                      binding,
                      WorkQueueToolFacade.inspectAt
                          (binding.ProjectId.ToString("D")) gitSnapshot.Commit project role))) }
    createAccessWithPorts ports

/// Explicit opt-in. The default path does not even evaluate the provider.
let installIfEnabled enabled (tui: ConsoleTui) (provider: unit -> WorkUiAccess) =
    if enabled then tui.SetWorkAccess(provider())
