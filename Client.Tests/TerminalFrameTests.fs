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


/// In-memory fake terminal. Invalid cursor positions fail exactly as the real
/// Windows console can, without mutating the process-wide terminal state.
type private ResizingTerminal(initialColumns: int, initialRows: int) =
    let mutable columns = initialColumns
    let mutable rows = initialRows
    let mutable writes = 0
    let mutable fg = System.ConsoleColor.Gray
    let mutable bg = System.ConsoleColor.Black
    let mutable resizeAfterWrite: (int * int * int) option = None
    let mutable failWrite: exn option = None
    member _.Resize(nextColumns, nextRows) =
        columns <- nextColumns
        rows <- nextRows
    member _.ResizeAfterWrite(writeNumber, nextColumns, nextRows) =
        resizeAfterWrite <- Some(writeNumber, nextColumns, nextRows)
    member _.FailNextWrite(error: exn) = failWrite <- Some error
    member _.Writes = writes
    interface IFrameOutput with
        member _.IsOutputRedirected = false
        member _.Size = columns, rows
        member _.ForegroundColor
            with get() = fg
            and set value = fg <- value
        member _.BackgroundColor
            with get() = bg
            and set value = bg <- value
        member _.SetCursorPosition(left, top) =
            if left < 0 || top < 0 || left >= columns || top >= rows then
                raise (System.IndexOutOfRangeException("Terminal cursor used stale dimensions."))
        member _.Write(_value) =
            writes <- writes + 1
            match failWrite with
            | Some failure ->
                failWrite <- None
                raise failure
            | None -> ()
            match resizeAfterWrite with
            | Some(writeNumber, nextColumns, nextRows) when writes = writeNumber ->
                resizeAfterWrite <- None
                columns <- nextColumns
                rows <- nextRows
            | _ -> ()

[<Test>]
let fullRedrawAfterGrowthNeverReadsOutsideTheSmallerPreviousFrame () =
    // The old edge comparison must not read the new terminal's column
    // from the previous, narrower frame: that was a real index failure.
    let terminal = ResizingTerminal(3, 2)
    let oldFrame = Frame(2, 2)
    oldFrame.WriteLine("a")
    Assert.That(oldFrame.PresentOn(None, terminal), Is.True)
    terminal.Resize(81, 24)
    let grown = Frame(80, 24)
    grown.WriteLine("new header")
    Assert.That(grown.PresentOn(Some oldFrame, terminal), Is.True)
    Assert.That(grown.GetCell(0, 0).Text, Is.EqualTo("n"))

[<Test>]
let interruptedRenderInvalidatesAndRecoversWithFullRepaint () =
    let terminal = ResizingTerminal(81, 24)
    let initial = Frame(80, 24)
    initial.WriteLine("Jarvis")
    Assert.That(initial.PresentOn(None, terminal), Is.True)
    let current = Frame(80, 24)
    current.WriteLine("Jarvis updated")
    let mutable failure: exn option = None
    terminal.ResizeAfterWrite(terminal.Writes + 2, 2, 1)
    Assert.That(current.PresentOn(Some initial, terminal, onFailure = (fun ex -> failure <- Some ex)), Is.False)
    Assert.That(failure.IsSome, Is.True, "A mid-presentation resize is retained in diagnostics.")
    // An interrupted frame must never become the new previous frame.
    Assert.That(terminal.Writes, Is.GreaterThan(0))
    terminal.Resize(81, 24)
    let recovered = Frame(80, 24)
    recovered.WriteLine("Jarvis restored")
    Assert.That(recovered.PresentOn(None, terminal), Is.True)
    Assert.That(recovered.GetCell(0, 0).Text, Is.EqualTo("J"))

[<Test>]
let consoleIndexErrorsDuringResizeAreCaughtAndReportedOffScreen () =
    let terminal = ResizingTerminal(81, 24)
    let frame = Frame(80, 24)
    frame.WriteLine("Jarvis")
    terminal.FailNextWrite(System.IndexOutOfRangeException("Resizing between terminal writes"))
    let mutable reported: string option = None
    let result =
        frame.PresentOn(None, terminal, onFailure = (fun ex -> reported <- Some(ex.ToString())))
    Assert.That(result, Is.False)
    Assert.That(reported |> Option.defaultValue "", Does.Contain("Resizing between terminal writes"))
    // On a stable terminal, a fully rebuilt frame succeeds.
    let retry = Frame(80, 24)
    retry.WriteLine("recovered")
    Assert.That(retry.PresentOn(None, terminal), Is.True)

[<Test>]
let repeatedMinimalNormalResizesAndChangedContentRemainIndexSafe () =
    let sizes = [| 0, 0; 1, 1; 2, 1; 3, 2; 81, 24; 160, 45; 40, 5 |]
    let terminal = ResizingTerminal(81, 24)
    let mutable previous: Frame option = None
    for iteration in 0 .. 240 do
        let columns, rows = sizes[iteration % sizes.Length]
        terminal.Resize(columns, rows)
        match Client.TerminalLayout.tryFrameSize columns rows with
        | None -> previous <- None
        | Some(width, height) ->
            let frame = Frame(width, height)
            frame.WriteLine($"Update #{iteration}")
            frame.SetCursorPosition(width - 1, height - 1)
            frame.Write("x")
            Assert.That(frame.PresentOn(previous, terminal), Is.True,
                        $"A stable frame must present at {columns}x{rows}.")
            previous <- Some frame

[<Test>]
let backgroundContentUpdatesPreserveFrameIsolationThroughRepeatedResizes () =
    // Match the TUI's single rendering owner: another thread produces immutable
    // completed frames while this thread presents them through changing sizes.
    use pending = new System.Collections.Concurrent.BlockingCollection<Frame>(4)
    let producer =
        System.Threading.Tasks.Task.Run(System.Action(fun () ->
            try
                for update in 0 .. 95 do
                    let frame = Frame(80, 24)
                    frame.WriteLine($"background content #{update}")
                    pending.Add(frame)
            finally pending.CompleteAdding()))
    let terminal = ResizingTerminal(81, 24)
    let mutable previous: Frame option = None
    let mutable received = 0
    for frame in pending.GetConsumingEnumerable() do
        if received % 7 = 0 then
            terminal.Resize(2, 1)
            Assert.That(frame.PresentOn(previous, terminal), Is.False)
            terminal.Resize(81, 24)
            previous <- None
        Assert.That(frame.PresentOn(previous, terminal), Is.True)
        previous <- Some frame
        received <- received + 1
    producer.GetAwaiter().GetResult()
    Assert.That(received, Is.EqualTo(96))
