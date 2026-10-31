module Client.Tests.TerminalLayoutTests

open Client.TerminalLayout
open NUnit.Framework

[<Test>]
let mouseRegionsHaveHalfOpenBounds () =
    let area = { X = 10; Y = 3; Width = 12; Height = 1 }
    Assert.That(contains 10 3 area, Is.True)
    Assert.That(contains 21 3 area, Is.True)
    Assert.That(contains 22 3 area, Is.False)
    Assert.That(contains 10 4 area, Is.False)
    Assert.That(contains -1 3 area, Is.False)

[<Test>]
let wideTerminalsProvideASidePaneAndNarrowOnesDoNot () =
    Assert.That(agentPane 104, Is.EqualTo(None))
    match agentPane 120 with
    | Some (listWidth, detailsX, detailsWidth) ->
        Assert.That(listWidth, Is.GreaterThan(0))
        Assert.That(detailsX, Is.GreaterThan(listWidth))
        Assert.That(detailsX + detailsWidth, Is.LessThanOrEqualTo(120))
    | None -> Assert.Fail("Wide terminal lost agent details pane.")

[<Test>]
let narrowExpandedDetailsReserveViewportHeight () =
    let normal = agentViewportRows 30 80 false
    let expanded = agentViewportRows 30 80 true
    Assert.That(expanded, Is.LessThan(normal))
    Assert.That(agentViewportRows 10 80 true, Is.EqualTo(3))
    Assert.That(agentViewportRows 50 140 true, Is.EqualTo(12))
