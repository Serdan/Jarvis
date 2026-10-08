module ExportStoreTests

open System
open NUnit.Framework
open FsUnitTyped
open Server.Services

[<Test>]
let ``export quotas are isolated per user`` () =
    use store = new ExportStore(maxUserBytes = 2L, maxUserEntries = 1)
    store.Add("a.png", [| 1uy; 2uy |], ownerId = "alice") |> ignore
    Assert.Throws<InvalidOperationException>(fun () -> store.Add("b.png", [| 3uy |], ownerId = "alice") |> ignore) |> ignore
    store.Add("c.png", [| 4uy |], ownerId = "bob") |> ignore

[<Test>]
let ``parallel export admission respects entry limit`` () =
    use store = new ExportStore(maxEntries = 3, maxUserEntries = 3)
    let accepted =
        [| 1 .. 50 |]
        |> Array.Parallel.map (fun i ->
            try
                store.Add($"{i}.png", [| 1uy |], ownerId = "alice") |> ignore
                true
            with :? InvalidOperationException -> false)
        |> Array.filter id
    accepted.Length |> shouldEqual 3

[<Test>]
let ``export store rejects excess bytes and entries`` () =
    let bytes = ExportStore(maxBytes = 3L)
    bytes.Add("a.png", [| 1uy; 2uy |]) |> ignore
    Assert.Throws<InvalidOperationException>(fun () -> bytes.Add("b.png", [| 1uy; 2uy |]) |> ignore) |> ignore

    let entries = ExportStore(maxEntries = 1)
    entries.Add("a.png", [| 1uy |]) |> ignore
    Assert.Throws<InvalidOperationException>(fun () -> entries.Add("b.png", [| 1uy |]) |> ignore) |> ignore

[<Test>]
let ``expired exports release capacity`` () =
    let store = ExportStore(TimeSpan.Zero, maxEntries = 1)
    store.Add("a.png", [| 1uy |]) |> ignore
    store.Add("b.png", [| 2uy |]) |> ignore

[<Test>]
let ``export store creates unguessable distinct tokens`` () =
    let store = ExportStore()
    let first = store.Add("render.png", [| 1uy; 2uy |])
    let second = store.Add("render.png", [| 1uy; 2uy |])

    first.Token |> shouldNotEqual second.Token
    first.Token.Length |> shouldBeGreaterThan 32
    first.MimeType |> shouldEqual "image/png"
    first.Bytes |> shouldEqual 2L

[<Test>]
let ``export store returns bytes before expiry`` () =
    let store = ExportStore(TimeSpan.FromMinutes 1.0)
    let created = store.Add("render.webp", [| 4uy; 5uy; 6uy |])

    match store.TryGet created.Token with
    | ValueSome exported ->
        exported.FileName |> shouldEqual "render.webp"
        exported.MimeType |> shouldEqual "image/webp"
        exported.Data |> shouldEqual [| 4uy; 5uy; 6uy |]
    | ValueNone -> Assert.Fail("Expected export to be available before expiry.")

[<Test>]
let ``export store expires links`` () =
    let store = ExportStore(TimeSpan.Zero)
    let created = store.Add("render.png", [| 1uy |])

    store.TryGet created.Token |> shouldEqual ValueNone

[<Test>]
let ``active content is not served as html`` () =
    ExportMimeType.detect "page.html" |> shouldEqual "text/plain; charset=utf-8"
    ExportMimeType.isInlineSafe "image/png" |> shouldEqual true
    ExportMimeType.isInlineSafe "image/svg+xml" |> shouldEqual false

[<Test>]
let ``export filename is reduced to basename`` () =
    let store = ExportStore()
    let created = store.Add("../nested/render.png", [| 1uy |])

    created.FileName |> shouldEqual "render.png"
