module Client.ActivityFilter

open System

type Status =
    | All
    | Running
    | AwaitingPermission
    | Completed
    | Failed
    | Informational

type Terms = FilterSelectors.Terms

let parseTerms query = FilterSelectors.parse query

// Preserve raw command project IDs; normalize only their presentation.
// Worktrees are addressed as "Project::worktree" or ".Project-worktrees/worktree".
let private worktreeParts (projectName: string) =
    match Client.ProjectPaths.ProjectTarget.parse projectName with
    | Ok target ->
        match Client.ProjectPaths.ProjectTarget.worktreeName (Client.ProjectPaths.ProjectTarget.checkout target) with
        | Some worktree -> Some(Client.ProjectPaths.ProjectTarget.projectName target, worktree)
        | None -> None
    | Error _ ->
        // Legacy display-only worktree paths are not protocol selectors.
        let marker = "-worktrees/"
        let index = projectName.IndexOf(marker, StringComparison.Ordinal)
        if projectName.StartsWith(".", StringComparison.Ordinal)
           && index > 1
           && index + marker.Length < projectName.Length then
            let project = projectName.Substring(1, index - 1)
            let worktree = projectName.Substring(index + marker.Length)
            if not (project.Contains('/')) && not (worktree.Contains('/')) then
                Some(project, worktree)
            else None
        else None

let projectRootName (projectName: string) =
    match worktreeParts projectName with
    | Some(project, _) -> project
    | None -> projectName

let projectLabel (projectName: string) =
    match worktreeParts projectName with
    | Some(project, worktree) -> project + "/" + worktree
    | None -> projectName

let projectToken (projectName: string) =
    "#" + projectLabel projectName

let private containsIgnoreCase (needle: string) (haystack: string) =
    haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0

let matchesTerms (terms: Terms) (projectName: string option) (searchableText: string) =
    let projectMatches =
        terms.Projects
        |> List.forall (fun term ->
            projectName
            |> Option.exists (fun name ->
                containsIgnoreCase term (projectLabel name)
                || containsIgnoreCase term name))

    let textMatches =
        terms.Text
        |> List.forall (fun term -> containsIgnoreCase term searchableText)

    projectMatches && textMatches

let nextStatus = function
    | All -> Running
    | Running -> AwaitingPermission
    | AwaitingPermission -> Completed
    | Completed -> Failed
    | Failed -> Informational
    | Informational -> All

let statusLabel = function
    | All -> "All"
    | Running -> "Running"
    | AwaitingPermission -> "Awaiting"
    | Completed -> "Completed"
    | Failed -> "Failed"
    | Informational -> "Info"
