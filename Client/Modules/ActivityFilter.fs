module Client.ActivityFilter

open System

type Status =
    | All
    | Running
    | AwaitingPermission
    | Completed
    | Failed
    | Informational

type Terms =
    { Text: string list
      Projects: string list }

let parseTerms (query: string) =
    let tokens =
        (if isNull query then "" else query).Split(
            [| ' '; '\t' |],
            StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
        |> Array.toList

    let projects, text =
        tokens
        |> List.fold (fun (projects, text) token ->
            if token.StartsWith("@", StringComparison.Ordinal) && token.Length > 1 then
                token.Substring(1) :: projects, text
            elif token = "@" then
                projects, text
            else
                projects, token :: text) ([], [])

    { Text = List.rev text
      Projects = List.rev projects }

// Worktree selectors stay in their command-routing form (Project::worktree).
// Translate only at the presentation boundary so history and routing remain stable.
let private separatorIndex (projectName: string) =
    let index = projectName.IndexOf("::", StringComparison.Ordinal)
    if index > 0 && index + 2 < projectName.Length then Some index else None

let projectRootName (projectName: string) =
    match separatorIndex projectName with
    | Some index -> projectName.Substring(0, index)
    | None -> projectName

let projectLabel (projectName: string) =
    match separatorIndex projectName with
    | Some index ->
        projectName.Substring(0, index) + "/" + projectName.Substring(index + 2)
    | None -> projectName

let projectToken (projectName: string) =
    "@" + projectLabel projectName

let private containsIgnoreCase (needle: string) (haystack: string) =
    haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0

let matchesTerms terms projectName searchableText =
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
