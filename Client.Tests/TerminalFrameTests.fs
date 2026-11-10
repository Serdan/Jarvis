module Client.Tests.TerminalFrameTests

open Client.TerminalFrame
open NUnit.Framework

[<Test>]
let rowsAreClippedRatherThanWrappedIntoAnotherHitRegion () =
    let frame = Frame(8, 3)
    frame.WriteLine("123456789123456")
    frame.WriteLine("target")
    Assert.That(frame.GetCell(0, 7).Text, Is.EqualTo("8"))
    Assert.That(frame.GetCell(1, 0).Text, Is.EqualTo("t"))
    Assert.That(frame.GetCell(1, 6).Text, Is.EqualTo(" "))
    Assert.That(frame.CursorTop, Is.EqualTo(2))

[<Test>]
let drawingBelowTheViewportCannotScrollOrReplaceVisibleRows () =
    let frame = Frame(6, 2)
    frame.WriteLine("first")
    frame.WriteLine("last")
    frame.WriteLine("offscreen")
    frame.SetCursorPosition(2, 50)
    frame.Write("overflow")
    Assert.That(frame.GetCell(0, 0).Text, Is.EqualTo("f"))
    Assert.That(frame.GetCell(1, 0).Text, Is.EqualTo("l"))

[<Test>]
let sidePanePositionsAreIndependentOfWriteLinePosition () =
    let frame = Frame(24, 6)
    frame.WriteLine("Rows")
    frame.WriteLine("left")
    frame.WriteLine("selection")
    frame.SetCursorPosition(12, 1)
    frame.ForegroundColor <- System.ConsoleColor.Cyan
    frame.Write("details")
    Assert.That(frame.GetCell(1, 0).Text, Is.EqualTo("l"))
    Assert.That(frame.GetCell(1, 12).Text, Is.EqualTo("d"))
    Assert.That(frame.GetCell(1, 12).Color, Is.EqualTo(System.ConsoleColor.Cyan))

[<Test>]
let unicodeCellsAreNotSplitOrWrappedAtTheRightEdge () =
    let frame = Frame(4, 2)
    frame.Write("abc界") // Width two; it must not partially occupy the last cell.
    Assert.That(frame.GetCell(0, 3).Text, Is.EqualTo(" "))
    frame.SetCursorPosition(0, 1)
    frame.Write("界x")
    Assert.That(frame.GetCell(1, 0).Text, Is.EqualTo("界"))
    Assert.That(frame.GetCell(1, 1).Text, Is.EqualTo(""))
    Assert.That(frame.GetCell(1, 2).Text, Is.EqualTo("x"))


[<Test>]
let untrustedTextCannotInjectTerminalEscapeSequences () =
    let frame = Frame(30, 2)
    frame.Write("task\u001b[2J\u0007title")
    let line = [ for column in 0 .. 29 -> frame.GetCell(0, column).Text ] |> String.concat ""
    Assert.That(line, Does.Contain("task[2Jtitle"))
    Assert.That(line |> Seq.exists (fun c -> int c = 27), Is.False)
    Assert.That(line |> Seq.exists (fun c -> int c = 7), Is.False)

[<Test>]
let backgroundColorIsStoredWithCellsAndRestoredForFollowingRows () =
    let frame = Frame(8, 3)
    frame.BackgroundColor <- System.ConsoleColor.DarkBlue
    frame.Write("colored")
    frame.BackgroundColor <- System.ConsoleColor.Black
    frame.WriteLine()
    frame.Write("normal")
    Assert.That(frame.GetCell(0, 0).Background, Is.EqualTo(System.ConsoleColor.DarkBlue))
    Assert.That(frame.GetCell(0, 6).Background, Is.EqualTo(System.ConsoleColor.DarkBlue))
    Assert.That(frame.GetCell(1, 0).Background, Is.EqualTo(System.ConsoleColor.Black))
    Assert.That(frame.GetCell(0, 7).Background, Is.EqualTo(System.ConsoleColor.Black))


[<Test>]
let trueColorBackgroundCoversBlankCellsAndCanReturnToPalette () =
    let frame = Frame(8, 3)
    let dark = Some(16uy, 16uy, 16uy)
    let selected = Some(38uy, 38uy, 38uy)
    Assert.That(frame.GetCell(0, 7).BackgroundRgb, Is.EqualTo(dark))
    frame.BackgroundRgb <- selected
    frame.WriteLine("selected")
    Assert.That(frame.GetCell(0, 0).BackgroundRgb, Is.EqualTo(selected))
    frame.BackgroundRgb <- dark
    frame.Write("normal")
    Assert.That(frame.GetCell(1, 0).BackgroundRgb, Is.EqualTo(dark))
    frame.BackgroundColor <- System.ConsoleColor.Black
    frame.Write("x")
    Assert.That(frame.GetCell(1, 6).BackgroundRgb, Is.EqualTo(None))
