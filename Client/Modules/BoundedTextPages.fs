module Client.BoundedTextPages

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Client
open Common

[<Literal>]
let defaultPageBytes = 48 * 1024
[<Literal>]
let maxPageBytes = 64 * 1024

[<CLIMutable>]
type Cursor =
    { PathDigest: string
      TotalBytes: int64
      WriteTicks: int64
      Offset: int64
      Line: int64
      PreviousCarriageReturn: bool }

let private pathDigest (path: string) =
    let canonical = Path.GetFullPath path
    let normalized =
        if OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() then canonical.ToUpperInvariant()
        else canonical
    SHA256.HashData(Encoding.UTF8.GetBytes normalized)
    |> Convert.ToHexString

let private encodeCursor cursor =
    let text = JsonSerializer.Serialize(cursor)
    Convert.ToBase64String(Encoding.UTF8.GetBytes(text))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_')

let private decodeCursor (token: string) =
    try
        if String.IsNullOrWhiteSpace token || token.Length > 1024 then
            Error(ValidationError "Invalid read-page cursor.")
        else
            let data = token.Replace('-', '+').Replace('_', '/')
            let padded = data.PadRight((data.Length + 3) / 4 * 4, '=')
            let result = JsonSerializer.Deserialize<Cursor>(Encoding.UTF8.GetString(Convert.FromBase64String padded))
            if isNull (box result) then Error(ValidationError "Invalid read-page cursor.")
            else Ok result
    with _ -> Error(ValidationError "Invalid read-page cursor.")

let private decodePage (buffer: byte[]) count : Result<string * int> =
    let strictUtf8 = UTF8Encoding(false, true)
    let mutable parsed = None
    let mutable tryBytes = count
    let lower = max 1 (count - 3)
    while tryBytes >= lower && parsed.IsNone do
        try
            parsed <- Some(strictUtf8.GetString(buffer, 0, tryBytes), tryBytes)
        with :? DecoderFallbackException ->
            tryBytes <- tryBytes - 1
    match parsed with
    | Some value -> Ok value
    | None -> Error(ValidationError "File contains invalid UTF-8 text; use export_file for binary data.")

let private advanceLines (text: string) firstLine previousCr =
    let mutable line = firstLine
    let mutable wasCr = previousCr
    for ch in text do
        if ch = '\n' then
            if not wasCr then line <- line + 1L
            wasCr <- false
        elif ch = '\r' then
            line <- line + 1L
            wasCr <- true
        else
            wasCr <- false
    line, wasCr

let readFilePage (FilePath path) relativePath cursorToken pageBytes : Client.Result<ReadFilePageResult> =
    try
        let pageBytes = defaultArg pageBytes defaultPageBytes
        if pageBytes < 16 || pageBytes > maxPageBytes then
            Error(ValidationError $"PageBytes must be between 16 and {maxPageBytes}.")
        else
            use source = new FileStream(path, FileMode.Open, FileAccess.Read,
                                        FileShare.ReadWrite ||| FileShare.Delete)
            let firstLength = source.Length
            let firstTicks = File.GetLastWriteTimeUtc(path).Ticks
            let thisDigest = pathDigest path
            let cursorResult =
                match cursorToken with
                | None -> Ok { PathDigest = thisDigest
                               TotalBytes = firstLength
                               WriteTicks = firstTicks
                               Offset = 0L
                               Line = 1L
                               PreviousCarriageReturn = false }
                | Some token -> decodeCursor token
            match cursorResult with
            | Error failure -> Error failure
            | Ok cursor when cursor.PathDigest <> thisDigest ->
                Error(ValidationError "Read-page cursor belongs to a different file.")
            | Ok cursor when cursor.TotalBytes <> firstLength || cursor.WriteTicks <> firstTicks ->
                Error(ValidationError "File changed since the previous read page; restart at the first page.")
            | Ok cursor when cursor.Offset < 0L || cursor.Offset > firstLength || cursor.Line < 1L ->
                Error(ValidationError "Invalid read-page cursor.")
            | Ok cursor ->
                source.Position <- cursor.Offset
                let buffer = Array.zeroCreate<byte> pageBytes
                let count = source.Read(buffer, 0, buffer.Length)
                let decoded = if count = 0 then Ok("", 0) else decodePage buffer count
                match decoded with
                | Error failure -> Error failure
                | Ok (raw, usedBytes) ->
                    let content =
                        if cursor.Offset = 0L && raw.StartsWith('\uFEFF') then raw.Substring(1)
                        else raw
                    let nextLine, previousCr =
                        advanceLines content cursor.Line cursor.PreviousCarriageReturn
                    let nextOffset = cursor.Offset + int64 usedBytes
                    let currentTicks = File.GetLastWriteTimeUtc(path).Ticks
                    let currentLength = FileInfo(path).Length
                    if source.Length <> firstLength || currentLength <> firstLength || currentTicks <> firstTicks then
                        Error(ValidationError "File changed while reading the page; restart at the first page.")
                    else
                        let continuation =
                            if nextOffset = firstLength then None
                            else
                                Some(encodeCursor
                                    { cursor with Offset = nextOffset
                                                  Line = nextLine
                                                  PreviousCarriageReturn = previousCr })
                        Ok { FilePath = relativePath
                             Content = content
                             StartByte = cursor.Offset
                             NextByte = nextOffset
                             TotalBytes = firstLength
                             FirstLine = cursor.Line
                             NextLine = nextLine
                             NextCursor = continuation }
    with
    | :? IOException as ex -> Error(ExceptionError ex)
    | :? UnauthorizedAccessException as ex -> Error(ExceptionError ex)
    | _ -> Error(ValidationError "Unable to read file page.")
