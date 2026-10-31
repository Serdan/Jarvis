module Client.TerminalLayout

/// Pure cell geometry shared by terminal rendering and mouse hit-testing.
type Rect = { X: int; Y: int; Width: int; Height: int }

let contains x y rect =
    rect.Width > 0 && rect.Height > 0
    && x >= rect.X && x < rect.X + rect.Width
    && y >= rect.Y && y < rect.Y + rect.Height

/// Wide terminals use a persistent details pane; narrow ones use inline details.
let agentPane terminalWidth =
    if terminalWidth < 105 then None
    else
        let divider = terminalWidth * 55 / 100
        Some (divider - 2, divider, max 15 (terminalWidth - divider - 2))

let agentViewportRows terminalHeight terminalWidth detailsExpanded =
    let reserved = 15 + (if detailsExpanded && terminalWidth < 105 then 10 else 0)
    max 3 (min 12 (terminalHeight - reserved))
