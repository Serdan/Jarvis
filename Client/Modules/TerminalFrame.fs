module Client.TerminalFrame

open System
open System.Globalization
open System.IO
open System.Text

/// Narrow terminal output seam: production uses System.Console, while tests can
/// resize between writes and detect cursor coordinates outside the viewport.
type IFrameOutput =
    abstract IsOutputRedirected: bool
    abstract Size: int * int
    abstract ForegroundColor: ConsoleColor with get, set
    abstract BackgroundColor: ConsoleColor with get, set
    abstract SetCursorPosition: int * int -> unit
    abstract Write: string -> unit

type private SystemFrameOutput() =
    interface IFrameOutput with
        member _.IsOutputRedirected = Console.IsOutputRedirected
        member _.Size = Console.WindowWidth, Console.WindowHeight
        member _.ForegroundColor
            with get() = Console.ForegroundColor
            and set value = Console.ForegroundColor <- value
        member _.BackgroundColor
            with get() = Console.BackgroundColor
            and set value = Console.BackgroundColor <- value
        member _.SetCursorPosition(left, top) = Console.SetCursorPosition(left, top)
        member _.Write(value) = Console.Write(value)

[<Struct; StructuralEquality; StructuralComparison>]
type Cell = { Text: string; Color: ConsoleColor; Background: ConsoleColor; BackgroundRgb: (byte * byte * byte) option }

/// A fixed-size terminal frame. Drawing past the right or bottom edge is clipped,
/// never wrapped into a different hit-test row or scrolled on the real terminal.
type Frame(width: int, height: int) =
    let width = max 1 width
    let height = max 1 height
    let defaultRgb = Some(16uy, 16uy, 16uy)
    let blank = { Text = " "; Color = ConsoleColor.Gray; Background = ConsoleColor.Black; BackgroundRgb = defaultRgb }
    let cells = Array2D.create height width blank
    let mutable x = 0
    let mutable y = 0
    let mutable color = ConsoleColor.Gray
    let mutable background = ConsoleColor.Black
    let mutable backgroundRgb = defaultRgb

    member _.Width = width
    member _.Height = height
    member _.CursorLeft = max 0 (min width x)
    member _.CursorTop = y

    member _.ForegroundColor
        with get() = color
        and set value = color <- value

    member _.BackgroundColor
        with get() = background
        and set value =
            background <- value
            backgroundRgb <- None

    /// True-color backgrounds allow genuinely subtle dark-theme shades.
    member _.BackgroundRgb
        with get() = backgroundRgb
        and set value = backgroundRgb <- value

    member _.GetCell(row, column) = cells[row, column]

    member _.SetCursorPosition(left: int, top: int) =
        x <- max 0 left
        y <- max 0 top

    member _.Write(value: string) =
        if not (String.IsNullOrEmpty value) then
            for rune in value.EnumerateRunes() do
                let v = rune.Value
                // Dynamic task titles and log output must never emit VT escape
                // sequences (or BEL / terminal control codes) during presentation.
                if (v >= 32 && v <> 127 && (v < 128 || v >= 160)) then
                    // Combining marks append to their preceding grapheme; East
                    // Asian/full-width symbols occupy a trailing continuation cell.
                    let category = Rune.GetUnicodeCategory rune
                    let combining =
                        category = UnicodeCategory.NonSpacingMark
                        || category = UnicodeCategory.SpacingCombiningMark
                        || category = UnicodeCategory.EnclosingMark
                    let wide =
                        (v >= 0x1100 && v <= 0x115f)
                        || (v >= 0x2329 && v <= 0x232a)
                        || (v >= 0x2e80 && v <= 0xa4cf)
                        || (v >= 0xac00 && v <= 0xd7a3)
                        || (v >= 0xf900 && v <= 0xfaff)
                        || (v >= 0xfe10 && v <= 0xfe6f)
                        || (v >= 0xff01 && v <= 0xff60)
                        || (v >= 0xffe0 && v <= 0xffe6)
                        || (v >= 0x1f300 && v <= 0x1faff)
                    if combining then
                        if y < height && x > 0 && x - 1 < width then
                            let original = cells[y, x - 1]
                            cells[y, x - 1] <- { original with Text = original.Text + rune.ToString() }
                    else
                        let cellWidth = if wide then 2 else 1
                        if y < height && x + cellWidth <= width then
                            cells[y, x] <- { Text = rune.ToString(); Color = color; Background = background; BackgroundRgb = backgroundRgb }
                            if cellWidth = 2 then
                                cells[y, x + 1] <- { Text = ""; Color = color; Background = background; BackgroundRgb = backgroundRgb }
                        x <- x + cellWidth

    member this.WriteLine(value: string) =
        this.Write value
        x <- 0
        y <- y + 1

    member _.WriteLine() =
        x <- 0
        y <- y + 1

    /// Draws only changed rows, retaining unchanged cells and their colors.
    /// Width reserves one physical terminal column to avoid bottom-right wrapping.
    /// Paint that final column with ECH, which does not advance the cursor.
    member this.PresentOn(previous: Frame option, terminal: IFrameOutput, ?onFailure: exn -> unit) =
        let sizeMatches () =
            try
                let columns, rows = terminal.Size
                TerminalLayout.matchesFrame width height columns rows
            with
            | :? ArgumentOutOfRangeException
            | :? IOException
            | :? InvalidOperationException
            | :? IndexOutOfRangeException -> false
        let reportFailure error =
            match onFailure with
            | None -> ()
            | Some report ->
                try report error
                with _ -> ()
        if terminal.IsOutputRedirected then false
        elif not (sizeMatches()) then false
        else
            let full =
                previous |> Option.forall (fun old -> old.Width <> width || old.Height <> height)
            let mutable initialColor = ConsoleColor.Gray
            let mutable initialBackground = ConsoleColor.Black
            let mutable activeRgb = None
            try
                // Clear using the terminal's default background (OSC 11), not
                // Console.Clear's palette black. This also covers untouched cells
                // and the rightmost column reserved against terminal wrapping.
                if full then terminal.Write("\u001b[49m\u001b[2J")
                initialColor <- terminal.ForegroundColor
                initialBackground <- terminal.BackgroundColor
                for row in 0 .. height - 1 do
                    if not (sizeMatches()) then
                        raise (InvalidOperationException("Terminal resized during presentation."))
                    let mutable col = 0
                    while col < width do
                        let current = cells[row, col]
                        let changed = full || (previous |> Option.exists (fun old -> old.GetCell(row, col) <> current))
                        if not changed then col <- col + 1
                        else
                            let runStart = col
                            let runColor = current.Color
                            let runBackground = current.Background
                            let runRgb = current.BackgroundRgb
                            let text = StringBuilder()
                            while col < width
                                  && cells[row, col].Color = runColor
                                  && cells[row, col].Background = runBackground
                                  && cells[row, col].BackgroundRgb = runRgb
                                  && (full || (previous |> Option.exists (fun old -> old.GetCell(row, col) <> cells[row, col]))) do
                                let part = cells[row, col].Text
                                if part <> "" then text.Append(part) |> ignore
                                col <- col + 1
                            if text.Length > 0 then
                                if not (sizeMatches()) then
                                    raise (InvalidOperationException("Terminal resized during presentation."))
                                terminal.SetCursorPosition(runStart, row)
                                if terminal.ForegroundColor <> runColor then terminal.ForegroundColor <- runColor
                                // terminal.ForegroundColor may reset terminal attributes,
                                // including a true-color background. Reapply RGB for every
                                // changed run rather than trusting a stale cached setting.
                                match runRgb with
                                | Some(r, g, b) ->
                                    terminal.Write($"\u001b[48;2;{r};{g};{b}m")
                                    activeRgb <- runRgb
                                | None ->
                                    if activeRgb.IsSome then
                                        terminal.Write("\u001b[49m")
                                        activeRgb <- None
                                    if terminal.BackgroundColor <> runBackground then
                                        terminal.BackgroundColor <- runBackground
                                terminal.Write(text.ToString())
                // The frame reserves the terminal's rightmost column to prevent
                // a write into the bottom-right corner from scrolling. Still paint
                // its background, using ECH (erase character) at that position.
                // ECH fills with the current rendition but does not move the cursor.
                for row in 0 .. height - 1 do
                    if not (sizeMatches()) then
                        raise (InvalidOperationException("Terminal resized during presentation."))
                    let edge = cells[row, width - 1]
                    // A full redraw may follow growth from a narrower old frame.
                    // Never index old cells using coordinates from new geometry.
                    let oldEdge =
                        if full then None
                        else previous |> Option.map (fun frame -> frame.GetCell(row, width - 1))
                    if full
                       || (oldEdge |> Option.exists (fun old -> old.BackgroundRgb <> edge.BackgroundRgb || old.Background <> edge.Background)) then
                        terminal.SetCursorPosition(width, row)
                        match edge.BackgroundRgb with
                        | Some(r, g, b) ->
                            terminal.Write($"\u001b[48;2;{r};{g};{b}m")
                            activeRgb <- edge.BackgroundRgb
                        | None ->
                            if activeRgb.IsSome then
                                terminal.Write("\u001b[49m")
                                activeRgb <- None
                            terminal.BackgroundColor <- edge.Background
                        terminal.Write("\u001b[1X")
                if activeRgb.IsSome then terminal.Write("\u001b[49m")
                terminal.ForegroundColor <- initialColor
                terminal.BackgroundColor <- initialBackground
                if not (sizeMatches()) then
                    raise (InvalidOperationException("Terminal resized during presentation."))
                terminal.SetCursorPosition(0, 0)
                sizeMatches()
            with ex when (ex :? ArgumentOutOfRangeException
                          || ex :? IOException
                          || ex :? InvalidOperationException
                          || ex :? IndexOutOfRangeException) ->
                // An interrupted write invalidates the whole frame. Surface the
                // original stack trace to the caller's durable diagnostic log.
                reportFailure ex
                try
                    if activeRgb.IsSome then terminal.Write("\u001b[49m")
                    terminal.ForegroundColor <- initialColor
                    terminal.BackgroundColor <- initialBackground
                with _ -> ()
                false

    member this.Present(previous: Frame option, ?onFailure: exn -> unit) =
        this.PresentOn(previous, SystemFrameOutput(), ?onFailure = onFailure)
