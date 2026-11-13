namespace Common

open System

/// The registered checkout selects a workspace, not another project identity.
/// This parser is shared by routing and recipient notification policy.
module ProjectSelector =
    let private validComponent (part: string) =
        not (String.IsNullOrWhiteSpace part)
        && part <> "." && part <> ".."
        && not (part.Contains('/')) && not (part.Contains('\\'))
        && not (part.Contains("..", StringComparison.Ordinal))
        && not (part.Contains(':'))

    /// Return canonical project name and optional registered worktree selector.
    /// Invalid selectors never acquire a different identity by string stripping.
    let parse (input: string) =
        if String.IsNullOrWhiteSpace input then None
        else
            match input.Split([| "::" |], StringSplitOptions.None) with
            | [| project |] when validComponent project -> Some(project, None)
            | [| project; checkout |] when validComponent project && validComponent checkout ->
                Some(project, Some checkout)
            | _ -> None

    let identity input = parse input |> Option.map fst

    /// The empty source means local delivery. Otherwise carry the verified
    /// canonical source project, never its checkout-qualified selector.
    let notificationSource sourceSelector destinationSelector =
        match identity sourceSelector, identity destinationSelector with
        | Some source, Some destination ->
            Some(if source = destination then "" else source)
        | _ -> None
