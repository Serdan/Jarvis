namespace Server.Services

open System
open System.Collections.Concurrent
open System.IO
open System.Security.Cryptography
open System.Threading

type ExportedFile =
    { FileName: string
      MimeType: string
      Data: byte[]
      OwnerId: string
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

type ExportStore(?lifetime: TimeSpan, ?maxBytes: int64, ?maxEntries: int, ?maxUserBytes: int64, ?maxUserEntries: int) =
    let entries = ConcurrentDictionary<string, ExportedFile>()
    let lifetime = defaultArg lifetime (TimeSpan.FromMinutes 10.0)
    let maxBytes = defaultArg maxBytes (64L * 1024L * 1024L)
    let maxEntries = defaultArg maxEntries 32
    let gate = obj ()
    let maxUserBytes = defaultArg maxUserBytes (16L * 1024L * 1024L)
    let maxUserEntries = defaultArg maxUserEntries 8
    do if maxUserBytes <= 0L || maxUserEntries <= 0 then invalidArg "maxUserBytes/maxUserEntries" "Must be positive"
    do if maxBytes <= 0L then invalidArg "maxBytes" "Must be positive"
    do if maxEntries <= 0 then invalidArg "maxEntries" "Must be positive"

    let cleanup now =
        for KeyValue(token, entry) in entries do
            if entry.ExpiresAt <= now then
                entries.TryRemove token |> ignore

    let expiryTimer =
        new Timer(TimerCallback(fun _ -> lock gate (fun () -> cleanup DateTimeOffset.UtcNow)),
                  null, TimeSpan.FromMinutes 1.0, TimeSpan.FromMinutes 1.0)

    let createToken () =
        let bytes = RandomNumberGenerator.GetBytes 32
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    member _.Add(fileName: string, data: byte[], ?ownerId: string) = lock gate (fun () ->
        let now = DateTimeOffset.UtcNow
        cleanup now
        if int64 data.LongLength > maxBytes || entries.Count >= maxEntries ||
           (entries.Values |> Seq.sumBy (fun e -> int64 e.Data.LongLength)) + int64 data.LongLength > maxBytes then
            invalidOp "Export storage capacity exceeded; retry after existing downloads expire."
        let owner = defaultArg ownerId ""
        let owned = entries.Values |> Seq.filter (fun e -> e.OwnerId = owner) |> Seq.toArray
        if owned.Length >= maxUserEntries ||
           (owned |> Array.sumBy (fun e -> int64 e.Data.LongLength)) + int64 data.LongLength > maxUserBytes then
            invalidOp "Per-user export storage capacity exceeded; retry after existing downloads expire."
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
                  OwnerId = owner
                  ExpiresAt = expiresAt }
            if entries.TryAdd(token, entry) then token else add ()

        let token = add ()
        { Token = token
          FileName = safeName
          MimeType = mimeType
          Bytes = int64 data.LongLength
          ExpiresAt = expiresAt })

    member _.TryGet(token: string) =
        let now = DateTimeOffset.UtcNow
        cleanup now
        match entries.TryGetValue token with
        | true, entry when entry.ExpiresAt > now -> ValueSome entry
        | true, _ ->
            entries.TryRemove token |> ignore
            ValueNone
        | _ -> ValueNone

    interface IDisposable with
        member _.Dispose() = expiryTimer.Dispose()
