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
let agentRowsUseFullWidthAcrossFormerPaneBreakpoint () =
    for width in [ 80; 103; 104; 105; 106; 107; 120; 160; 220 ] do
        Assert.That(agentListWidth width, Is.EqualTo(width),
                    $"Agent row width at terminal width {width} must not be reduced.")
    Assert.That(agentListWidth 0, Is.EqualTo(1))

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

[<Test>]
let collapsedPhysicalTerminalsHaveNoDrawableFrame () =
    for columns, rows in [ 0, 0; 0, 20; 1, 1; 1, 20; 2, 0; 80, 0 ] do
        Assert.That(tryFrameSize columns rows, Is.EqualTo(None),
                    $"A terminal of {columns}x{rows} must not manufacture geometry.")

[<Test>]
let frameGeometryReservesPhysicalLastColumn () =
    for columns, rows in [ 2, 1; 3, 2; 81, 24; 160, 48 ] do
        Assert.That(tryFrameSize columns rows, Is.EqualTo(Some(columns - 1, rows)))
        Assert.That(matchesFrame (columns - 1) rows columns rows, Is.True)
        Assert.That(matchesFrame columns rows columns rows, Is.False)
        Assert.That(matchesFrame (columns - 1) rows (columns + 1) rows, Is.False)
