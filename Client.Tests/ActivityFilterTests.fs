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
