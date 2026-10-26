module ActivityFilterTests

open Client
open FsUnitTyped
open NUnit.Framework

[<Test>]
let ``plain terms are text filters`` () =
    let terms = ActivityFilter.parseTerms "build client"
    terms.Text |> shouldEqual [ "build"; "client" ]
    terms.Projects |> shouldEqual []

[<Test>]
let ``at terms are project filters`` () =
    let terms = ActivityFilter.parseTerms "@Wayfold sprite"
    terms.Text |> shouldEqual [ "sprite" ]
    terms.Projects |> shouldEqual [ "Wayfold" ]

[<Test>]
let ``project filters use case insensitive substring matching`` () =
    let terms = ActivityFilter.parseTerms "@loke"
    ActivityFilter.matchesTerms terms (Some "Projekt Loke") "SearchText foo"
    |> shouldEqual true

[<Test>]
let ``text and project terms are combined`` () =
    let terms = ActivityFilter.parseTerms "@way render sprite"
    ActivityFilter.matchesTerms terms (Some "Wayfold") "Render modern sprite system"
    |> shouldEqual true
    ActivityFilter.matchesTerms terms (Some "Wayfold") "Render inventory"
    |> shouldEqual false

[<Test>]
let ``status cycle covers predefined list`` () =
    ActivityFilter.nextStatus ActivityFilter.All |> shouldEqual ActivityFilter.Running
    ActivityFilter.nextStatus ActivityFilter.Running |> shouldEqual ActivityFilter.AwaitingPermission
    ActivityFilter.nextStatus ActivityFilter.AwaitingPermission |> shouldEqual ActivityFilter.Completed
    ActivityFilter.nextStatus ActivityFilter.Completed |> shouldEqual ActivityFilter.Failed
    ActivityFilter.nextStatus ActivityFilter.Failed |> shouldEqual ActivityFilter.Informational
    ActivityFilter.nextStatus ActivityFilter.Informational |> shouldEqual ActivityFilter.All

[<Test>]
let ``worktree activity labels use project slash worktree`` () =
    ActivityFilter.projectToken "Wayfold::silver-ring-trade"
    |> shouldEqual "@Wayfold/silver-ring-trade"
    ActivityFilter.projectToken "Wayfold"
    |> shouldEqual "@Wayfold"
    ActivityFilter.projectRootName "Wayfold::silver-ring-trade"
    |> shouldEqual "Wayfold"
    ActivityFilter.projectLabel "Wayfold::silver-ring-trade"
    |> shouldEqual "Wayfold/silver-ring-trade"

[<Test>]
let ``project filter accepts displayed worktree path and parent project`` () =
    for query in [ "@Wayfold/silver-ring-trade"; "@wayfold/silver"; "@silver-ring-trade"; "@Wayfold"; "@Wayfold::silver-ring-trade" ] do
        ActivityFilter.matchesTerms (ActivityFilter.parseTerms query)
            (Some "Wayfold::silver-ring-trade") "RunCommand dotnet test"
        |> shouldEqual true
    ActivityFilter.matchesTerms (ActivityFilter.parseTerms "@Wayfold/other")
        (Some "Wayfold::silver-ring-trade") "RunCommand dotnet test"
    |> shouldEqual false
    ActivityFilter.matchesTerms (ActivityFilter.parseTerms "@Wayfold/silver-ring-trade")
        (Some "Wayfold") "RunCommand dotnet test"
    |> shouldEqual false

[<Test>]
let ``non-worktree project labels remain unchanged`` () =
    ActivityFilter.projectToken "Jarvis" |> shouldEqual "@Jarvis"
    ActivityFilter.projectToken "Jarvis::" |> shouldEqual "@Jarvis::"
    ActivityFilter.projectToken "::invalid" |> shouldEqual "@::invalid"

[<Test>]
let ``hidden sibling worktree paths use readable project and worktree tokens`` () =
    ActivityFilter.projectToken ".Wayfold-worktrees/silver-ring-trade"
    |> shouldEqual "@Wayfold/silver-ring-trade"
    ActivityFilter.projectToken ".Wayfold-worktrees/tooltip-fix"
    |> shouldEqual "@Wayfold/tooltip-fix"
    ActivityFilter.projectRootName ".Wayfold-worktrees/silver-ring-trade"
    |> shouldEqual "Wayfold"
    ActivityFilter.projectToken "Wayfold::silver-ring-trade"
    |> shouldEqual "@Wayfold/silver-ring-trade"
    ActivityFilter.projectToken "Wayfold"
    |> shouldEqual "@Wayfold"

[<Test>]
let ``filters match hidden sibling worktrees using rendered tokens`` () =
    let raw = Some ".Wayfold-worktrees/silver-ring-trade"
    for query in [ "@Wayfold/silver-ring-trade"; "@wayfold/silver"; "@Wayfold"; "@.Wayfold-worktrees/silver-ring-trade" ] do
        ActivityFilter.matchesTerms (ActivityFilter.parseTerms query) raw "Trade Silver Ring"
        |> shouldEqual true
    ActivityFilter.matchesTerms (ActivityFilter.parseTerms "@Wayfold/tooltip-fix") raw "Trade Silver Ring"
    |> shouldEqual false

[<Test>]
let ``other path-like project names remain unchanged`` () =
    ActivityFilter.projectToken "Wayfold-nav-alloc" |> shouldEqual "@Wayfold-nav-alloc"
    ActivityFilter.projectToken ".Wayfold-worktrees" |> shouldEqual "@.Wayfold-worktrees"
    ActivityFilter.projectToken ".Wayfold-worktrees/" |> shouldEqual "@.Wayfold-worktrees/"
    ActivityFilter.projectToken ".Wayfold-worktrees/silver-ring-trade/nested"
    |> shouldEqual "@.Wayfold-worktrees/silver-ring-trade/nested"
