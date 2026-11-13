module Client.FilterSelectors

open System

/// Display-only query syntax; these selectors never become MCP or ledger identities.
type Terms =
    { Text: string list
      Projects: string list
      Agents: string list }

type private SelectorKind =
    | Project
    | Agent

type private Chunk =
    { Kind: SelectorKind option
      Value: string }

let private words (value: string) =
    value.Split(
        [| ' '; '\t'; '\r'; '\n' |],
        StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
    |> Array.toList

/// Markers may touch, e.g. #Wayfold@Worker 1, but ordinary prose containing
/// an embedded @ or # does not start a selector.
let private chunks (query: string) =
    let query = if isNull query then "" else query
    let result = ResizeArray<Chunk>()
    let mutable kind: SelectorKind option = None
    let mutable start = 0
    for index = 0 to query.Length - 1 do
        let marker =
            match query[index] with
            | '#' -> Some Project
            | '@' -> Some Agent
            | _ -> None
        match marker with
        | Some next when index = 0 || Char.IsWhiteSpace query[index - 1] || kind.IsSome ->
            result.Add({ Kind = kind; Value = query.Substring(start, index - start) })
            kind <- Some next
            start <- index + 1
        | _ -> ()
    result.Add({ Kind = kind; Value = query.Substring(start) })
    result |> Seq.toList

let private containsCaseInsensitive (needle: string) (value: string) =
    value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0

/// Agent selectors use case-insensitive substring matching. When the selector
/// ends in a digit, require the numeric token to end at a name boundary:
/// @Worker matches Worker and Worker 1, but @Worker 1 cannot match Worker 10.
/// This is a property of the query syntax, never of which names are present.
let matchesAgentTerm (term: string) (agentName: string) =
    if String.IsNullOrEmpty term then true
    elif isNull agentName then false
    else
        let requiresTokenBoundary = Char.IsDigit term[term.Length - 1]
        let isTokenContinuation character = Char.IsLetterOrDigit character || character = '_'
        let rec search start =
            let index = agentName.IndexOf(term, start, StringComparison.OrdinalIgnoreCase)
            if index < 0 then false
            else
                let after = index + term.Length
                if not requiresTokenBoundary || after = agentName.Length
                   || not (isTokenContinuation agentName[after]) then true
                else search (index + 1)
        search 0

/// A selector with embedded spaces is resolved against the current local
/// snapshot. The longest name-compatible token prefix belongs to the selector;
/// remaining words are ordinary descriptive search terms. Without a known
/// name, only its first word is a selector. The ambiguity cannot be resolved
/// from raw text alone without quotations or local candidates.
let private selectorAndText (names: string list) (value: string) =
    let tokens = words value
    if List.isEmpty tokens then "", []
    else
        let array = tokens |> List.toArray
        let maximum =
            [1 .. array.Length]
            |> List.filter (fun length ->
                let prefix = array[0 .. length - 1] |> String.concat " "
                names |> List.exists (containsCaseInsensitive prefix))
            |> List.tryLast
            |> Option.defaultValue 1
        (array[0 .. maximum - 1] |> String.concat " "),
        (array[maximum ..] |> Array.toList)

/// Text and multiple selectors combine with AND semantics. Empty #/@ markers
/// are deliberately ignored while the user is in live filter-editing mode.
let parse (projects: string list) (agents: string list) (query: string) =
    let mutable text = []
    let mutable projectTerms = []
    let mutable agentTerms = []
    for chunk in chunks query do
        match chunk.Kind with
        | None ->
            for word in words chunk.Value do text <- word :: text
        | Some kind ->
            let name, trailing = selectorAndText (if kind = Project then projects else agents) chunk.Value
            if not (String.IsNullOrWhiteSpace name) then
                match kind with
                | Project -> projectTerms <- name :: projectTerms
                | Agent -> agentTerms <- name :: agentTerms
            for word in trailing do text <- word :: text
    { Text = List.rev text
      Projects = List.rev projectTerms
      Agents = List.rev agentTerms }

let parseWithoutCandidates query = parse [] [] query

let private orderedUniqueNames names =
    names
    |> List.filter (String.IsNullOrWhiteSpace >> not)
    |> List.sortWith (fun left right ->
        let ignoreCase = StringComparer.OrdinalIgnoreCase.Compare(left, right)
        if ignoreCase <> 0 then ignoreCase
        else StringComparer.Ordinal.Compare(left, right))
    |> List.distinctBy (fun name -> name.ToUpperInvariant())

/// Complete only the trailing #/@ fragment. Tab extends the common prefix
/// of *all* currently matching names; it never cycles or invents a suffix.
let completeTrailing (projects: string list) (agents: string list) (query: string) =
    let query = if isNull query then "" else query
    match chunks query |> List.tryLast with
    | Some { Kind = Some kind; Value = typed } ->
        let names = if kind = Project then projects else agents
        let matches =
            orderedUniqueNames names
            |> List.filter (fun name -> name.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
        match matches with
        | [] -> query
        | first :: rest ->
            let mutable common = first.Length
            for name in rest do
                common <- min common name.Length
                let mutable matched = 0
                while matched < common
                      && Char.ToUpperInvariant(first[matched]) = Char.ToUpperInvariant(name[matched]) do
                    matched <- matched + 1
                common <- matched
            if common <= typed.Length then query
            else query + first.Substring(typed.Length, common - typed.Length)
    | _ -> query

let appendSelector marker (query: string) =
    let query = if isNull query then "" else query
    if marker <> '#' && marker <> '@' then query
    else
        let separator =
            if query.Length = 0 || Char.IsWhiteSpace(query[query.Length - 1]) then ""
            else " "
        query + separator + string marker
