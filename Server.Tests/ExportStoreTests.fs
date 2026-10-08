module ExportStoreTests

open System
open NUnit.Framework
open FsUnitTyped
open Server.Services

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
