namespace Server.Services

open System
open System.Collections.Concurrent
open System.IO
open System.Security.Cryptography

type ExportedFile =
    { FileName: string
      MimeType: string
      Data: byte[]
      ExpiresAt: DateTimeOffset }

type ExportCreated =
    { Token: string
      FileName: string
      MimeType: string
      Bytes: int64
      ExpiresAt: DateTimeOffset }

module ExportMimeType =
    let detect (fileName: string) =
        match Path.GetExtension(fileName).ToLowerInvariant() with
        | ".png" -> "image/png"
        | ".jpg"
        | ".jpeg" -> "image/jpeg"
        | ".webp" -> "image/webp"
        | ".gif" -> "image/gif"
        | ".svg" -> "image/svg+xml"
        | ".pdf" -> "application/pdf"
        | ".json" -> "application/json"
        | ".txt"
        | ".md"
        | ".log"
        | ".csv"
        | ".tsv" -> "text/plain; charset=utf-8"
        | ".html"
        | ".htm" -> "text/plain; charset=utf-8"
        | _ -> "application/octet-stream"

    let isInlineSafe (mimeType: string) =
        mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
        && not (String.Equals(mimeType, "image/svg+xml", StringComparison.OrdinalIgnoreCase))

type ExportStore(?lifetime: TimeSpan) =
    let entries = ConcurrentDictionary<string, ExportedFile>()
    let lifetime = defaultArg lifetime (TimeSpan.FromMinutes 10.0)

    let cleanup now =
        for KeyValue(token, entry) in entries do
            if entry.ExpiresAt <= now then
                entries.TryRemove token |> ignore

    let createToken () =
        let bytes = RandomNumberGenerator.GetBytes 32
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    member _.Add(fileName: string, data: byte[]) =
        let now = DateTimeOffset.UtcNow
        cleanup now
        let safeName =
            let name = Path.GetFileName fileName
            if String.IsNullOrWhiteSpace name then "download" else name
        let mimeType = ExportMimeType.detect safeName
        let expiresAt = now + lifetime

        let rec add () =
            let token = createToken ()
            let entry =
                { FileName = safeName
                  MimeType = mimeType
                  Data = data
                  ExpiresAt = expiresAt }
            if entries.TryAdd(token, entry) then token else add ()

        let token = add ()
        { Token = token
          FileName = safeName
          MimeType = mimeType
          Bytes = int64 data.LongLength
          ExpiresAt = expiresAt }

    member _.TryGet(token: string) =
        let now = DateTimeOffset.UtcNow
        cleanup now
        match entries.TryGetValue token with
        | true, entry when entry.ExpiresAt > now -> ValueSome entry
        | true, _ ->
            entries.TryRemove token |> ignore
            ValueNone
        | _ -> ValueNone
