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
let availableRowsFillTheTerminalWithoutAnArbitraryPageCap () =
    Assert.That(availableRows 24 9 3, Is.EqualTo(12))
    Assert.That(availableRows 52 9 3, Is.EqualTo(40))
    Assert.That(availableRows 80 11 7, Is.EqualTo(62))

[<Test>]
let availableRowsAccountForPanelsAndTinyTerminals () =
    Assert.That(availableRows 35 10 4, Is.EqualTo(21))
    Assert.That(availableRows 35 10 13, Is.EqualTo(12))
    Assert.That(availableRows 8 12 5, Is.EqualTo(1))
    Assert.That(availableRows 30 7 -4, Is.EqualTo(23))
