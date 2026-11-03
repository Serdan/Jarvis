module Client.TerminalLayout

/// Pure cell geometry shared by terminal rendering and mouse hit-testing.
type Rect = { X: int; Y: int; Width: int; Height: int }

let contains x y rect =
    rect.Width > 0 && rect.Height > 0
    && x >= rect.X && x < rect.X + rect.Width
    && y >= rect.Y && y < rect.Y + rect.Height

/// Agents always use the full available width for list rows. Selected
/// details are rendered beneath the list, independent of terminal width.
let agentListWidth terminalWidth = max 1 terminalWidth

/// Remaining single-line items after the header and reserved lower panels.
/// At least one row is retained for navigation even in very short terminals;
/// the bounded renderer clips that row if the terminal cannot display it.
let availableRows terminalHeight firstRow reservedRows =
    max 1 (terminalHeight - firstRow - max 0 reservedRows)
