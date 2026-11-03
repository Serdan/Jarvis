module Client.TerminalMouse

open System
open System.Text.RegularExpressions

/// Terminal cell coordinates are zero-based. SGR reports are one-based.
type MouseEvent =
    | Click of x: int * y: int
    | Scroll of direction: int * x: int * y: int

type InputEvent =
    | Key of ConsoleKeyInfo
    | Mouse of MouseEvent

module private Parsing =
    let report = Regex("^\\x1b\\[<(\\d+);(\\d+);(\\d+)([Mm])$", RegexOptions.Compiled)

    let decode (text: string) =
        let matchResult = report.Match(text)
        if not matchResult.Success then None
        else
            match Int32.TryParse(matchResult.Groups.[1].Value),
                  Int32.TryParse(matchResult.Groups.[2].Value),
                  Int32.TryParse(matchResult.Groups.[3].Value) with
            | (true, button), (true, column), (true, row)
                when column >= 1 && row >= 1 && column <= 10000 && row <= 10000 ->
                let x, y = column - 1, row - 1
                if button &&& 64 <> 0 then
                    match button &&& 3 with
                    | 0 -> Some(Scroll(-1, x, y))
                    | 1 -> Some(Scroll(1, x, y))
                    | _ -> None
                elif matchResult.Groups.[4].Value = "M"
                     && button &&& 32 = 0
                     && button &&& 3 = 0 then Some(Click(x, y))
                else None // Ignore releases, motion and non-left buttons.
            | _ -> None

/// Separates xterm SGR mouse reports from Console.ReadKey's ordinary keyboard events.
/// ESC waits briefly: a real Escape key is still delivered after the timeout.
type Decoder() =
    let pending = ResizeArray<ConsoleKeyInfo>()
    let mutable started = DateTimeOffset.MinValue

    let replay () =
        let items = pending |> Seq.map Key |> Seq.toList
        pending.Clear()
        items

    member _.Feed(now: DateTimeOffset, key: ConsoleKeyInfo) : InputEvent list =
        if pending.Count = 0 then
            if key.Key = ConsoleKey.Escape && key.KeyChar = '\u001b' then
                started <- now
                pending.Add key
                []
            else [ Key key ]
        else
            pending.Add key
            let text = new String(pending |> Seq.map _.KeyChar |> Seq.toArray)
            if pending.Count = 2 && key.KeyChar <> '[' then replay()
            elif pending.Count >= 3 && text.[2] <> '<' then
                // Console normally decodes arrows itself. Preserve raw escape
                // sequences if the runtime instead presents them as characters.
                let arrow =
                    match text with
                    | "\u001b[A" -> Some ConsoleKey.UpArrow
                    | "\u001b[B" -> Some ConsoleKey.DownArrow
                    | "\u001b[C" -> Some ConsoleKey.RightArrow
                    | "\u001b[D" -> Some ConsoleKey.LeftArrow
                    | "\u001b[H" -> Some ConsoleKey.Home
                    | "\u001b[F" -> Some ConsoleKey.End
                    | "\u001b[5~" -> Some ConsoleKey.PageUp
                    | "\u001b[6~" -> Some ConsoleKey.PageDown
                    | _ -> None
                match arrow with
                | Some keyCode ->
                    pending.Clear()
                    [ Key(ConsoleKeyInfo('\u0000', keyCode, false, false, false)) ]
                | None when pending.Count >= 5 || (pending.Count >= 3 && text.[2] <> '5' && text.[2] <> '6') ->
                    replay()
                | None -> []
            elif pending.Count >= 3 && text.[2] = '<' then
                if pending.Count > 40 then replay()
                elif key.KeyChar = 'M' || key.KeyChar = 'm' then
                    pending.Clear()
                    match Parsing.decode text with
                    | Some mouse -> [ Mouse mouse ]
                    | None -> [] // A syntactically complete, ignored mouse event.
                elif not (Char.IsAsciiDigit(key.KeyChar) || key.KeyChar = ';' || key.KeyChar = '<') then replay()
                else []
            else []

    member _.Flush(now: DateTimeOffset) : InputEvent list =
        if pending.Count > 0 && now - started >= TimeSpan.FromMilliseconds(65.) then replay()
        else []

    member _.HasPending = pending.Count > 0

/// Merge adjacent wheel reports before the costly whole-screen redraw.
/// Key presses and clicks are barriers: their relative order must remain intact.
let coalesceWheelEvents (events: InputEvent list) =
    let flush pending acc =
        match pending with
        | Some (delta, x, y) when delta <> 0 -> Mouse(Scroll(delta, x, y)) :: acc
        | _ -> acc
    let rec loop pending acc remaining =
        match remaining with
        | Mouse(Scroll(direction, x, y)) :: tail ->
            let delta =
                match pending with
                | Some (previous, _, _) -> previous + direction
                | None -> direction
            loop (Some(delta, x, y)) acc tail
        | event :: tail ->
            loop None (event :: flush pending acc) tail
        | [] -> List.rev (flush pending acc)
    loop None [] events

/// Request normal-button + wheel reports with SGR coordinates and hide the
/// cursor while the TUI runs. Restore both modes on cancellation/exception.
module Tracking =
    let enable () : IDisposable =
        if Console.IsInputRedirected || Console.IsOutputRedirected
           || Environment.GetEnvironmentVariable("TERM") = "dumb" then
            { new IDisposable with member _.Dispose() = () }
        else
            try
                Console.Write("\u001b[?25l\u001b[?1000h\u001b[?1006h")
                Console.Out.Flush()
                { new IDisposable with
                    member _.Dispose() =
                        try
                            Console.Write("\u001b[?1006l\u001b[?1000l\u001b[?25h")
                            Console.Out.Flush()
                        with _ -> () }
            with _ ->
                { new IDisposable with member _.Dispose() = () }
