module PromptContextAccountingTests

open System
open System.Text
open System.Text.Json
open NUnit.Framework
open Client.PromptContextAccounting

let fragment kind ordinal version body : Fragment =
    { Kind = kind; LayerOrdinal = ordinal; Version = version; Text = body }

let framing body = fragment SectionKind.Framing None None body
let role ordinal version body = fragment SectionKind.Role (Some ordinal) (Some version) body
let utf8 = UTF8Encoding(false, true)
let measure fragments = account fragments None None noBudgets

[<Test>]
let ``exact ordered section sizes include framing and supplementary characters`` () =
    let fragments = [ framing "⟦"; role 0 1 "Aé😀"; framing "\r\n" ]
    let result = measure fragments
    Assert.That(result.Prompt.Utf8Bytes, Is.EqualTo(12L))
    Assert.That(result.Prompt.Utf16Units, Is.EqualTo(7L))
    Assert.That(result.Sections |> Array.map (fun x -> x.Size.Utf8Bytes), Is.EqualTo([|3L;7L;2L|]))
    Assert.That(result.Sections |> Array.map (fun x -> x.Kind), Is.EqualTo([|SectionKind.Framing;SectionKind.Role;SectionKind.Framing|]))
    Assert.That(serialize result, Is.EqualTo(serialize (measure fragments)))

[<Test>]
let ``property sizes equal independently encoded concatenation across deterministic Unicode corpus`` () =
    let rng = Random(107)
    let chars = [|"a"; "é"; "中"; "😀"; "\r\n"; "\u0000"; "e\u0301"; ""; "\\"; "\""|]
    for _ in 1..300 do
        let fragments =
            [ for i in 0..rng.Next(0,20) ->
                let body = String.Concat(Array.init (rng.Next(0,25)) (fun _ -> chars.[rng.Next(chars.Length)]))
                role i 2 body ]
        let prompt = fragments |> List.map (fun x -> x.Text) |> String.concat ""
        let actual = measure fragments
        Assert.That(actual.Prompt.Utf8Bytes, Is.EqualTo(int64 (utf8.GetByteCount prompt)))
        Assert.That(actual.Prompt.Utf16Units, Is.EqualTo(int64 prompt.Length))
        Assert.That(actual.Sections |> Array.sumBy (fun x -> x.Size.Utf8Bytes), Is.EqualTo(actual.Prompt.Utf8Bytes))
        Assert.That(actual.Sections |> Array.sumBy (fun x -> x.Size.Utf16Units), Is.EqualTo(actual.Prompt.Utf16Units))

[<Test>]
let ``prefix is exact in bytes across section boundaries including partial Unicode scalar`` () =
    let actual = account [framing "ab"; framing "é"; framing "😀z"] (Some(ActualPrompt "abê😀z")) None noBudgets
    Assert.That(actual.CommonPrefixUtf8Bytes.Value, Is.EqualTo(3L))
    let shorter = account [framing "a"; framing ""; framing "b"] (Some(ActualPrompt "abc")) None noBudgets
    Assert.That(shorter.CommonPrefixUtf8Bytes.Value, Is.EqualTo(2L))
    let identical = account [framing "😀"] (Some(ActualPrompt "😀")) None noBudgets
    Assert.That(identical.CommonPrefixUtf8Bytes.Value, Is.EqualTo(4L))
    let unknown = account [framing "ab"] (Some(SizeOnly { Utf8Bytes = 2L; Utf16Units = 2L })) None noBudgets
    Assert.That(unknown.CommonPrefixUtf8Bytes.HasValue, Is.False)
    Assert.That((measure [framing "ab"]).CommonPrefixUtf8Bytes.HasValue, Is.False)
    let empty = account [] (Some(ActualPrompt "a")) None noBudgets
    Assert.That(empty.CommonPrefixUtf8Bytes.Value, Is.EqualTo(0L))

[<Test>]
let ``duplicate identity is explicit and independent of version or equal content`` () =
    let fragments =
        [ role 0 1 "x"; role 1 1 "x"; role 0 2 "y"
          fragment SectionKind.Role None (Some 1) "x"
          fragment SectionKind.Role None (Some 1) "x"
          fragment SectionKind.Agent (Some 0) (Some 1) "x" ]
    let result = measure fragments
    Assert.That(result.Sections |> Array.map (fun x -> x.DuplicateLayer), Is.EqualTo([|false;false;true;false;false;false|]))

[<Test>]
let ``advisory budgets exact boundary growth and absent baselines`` () =
    let budgets = { noBudgets with PromptUtf8Bytes = Some 4L; PromptUtf16Units = Some 2L; GrowthUtf8Bytes = Some 2L; EnvelopeUtf8Bytes = Some 3L }
    let exact = account [framing "😀"] (Some(ActualPrompt "ab")) (Some "123") budgets
    Assert.That(exact.Warnings, Is.Empty)
    Assert.That(exact.Delta.Value.Utf8Bytes, Is.EqualTo(2L))
    let over = account [framing "😀a"] (Some(ActualPrompt "ab")) (Some "1234") budgets
    Assert.That(over.Warnings |> Array.map (fun x -> x.Reason), Is.EqualTo([|WarningReason.PromptUtf8;WarningReason.PromptUtf16;WarningReason.EnvelopeUtf8;WarningReason.GrowthUtf8|]))
    Assert.That(over.Warnings |> Array.map (fun x -> x.Observed - x.Threshold), Is.EqualTo([|1L;1L;1L;1L|]))
    let missing = account [framing "😀a"] None None budgets
    Assert.That(missing.Delta.HasValue, Is.False)
    Assert.That(missing.EnvelopeUtf8Bytes.HasValue, Is.False)
    Assert.That(missing.Warnings |> Array.map (fun x -> x.Reason), Is.EqualTo([|WarningReason.PromptUtf8;WarningReason.PromptUtf16|]))
    let shrink = account [framing "a"] (Some(ActualPrompt "abcd")) None budgets
    Assert.That(shrink.Delta.Value.Utf8Bytes, Is.EqualTo(-3L))
    Assert.That(shrink.Warnings, Is.Empty)

[<Test>]
let ``actual BrowserArguments serialization measured separately and diagnostics omit sensitive material`` () =
    let sentinel = "PRIVATE-CREDENTIAL-9fbe1"
    let prompt = "Project: Jarvis\nAgent: Worker 30\n" + sentinel + "\"😀"
    let envelope = Client.BrowserArguments.serializeMinimalLaunchPrompt prompt
    let result = account [framing prompt] (Some(ActualPrompt (sentinel + " previous"))) (Some envelope) noBudgets
    Assert.That(result.EnvelopeUtf8Bytes.Value, Is.EqualTo(int64 (Client.BrowserArguments.byteCount envelope)))
    Assert.That(result.Prompt.Utf8Bytes, Is.EqualTo(int64 (utf8.GetByteCount prompt)))
    Assert.That(result.EnvelopeUtf8Bytes.Value, Is.GreaterThan(result.Prompt.Utf8Bytes))
    let json = serialize result
    Assert.That(json, Does.Not.Contain(sentinel))
    Assert.That(json, Does.Not.Contain("Worker 30"))
    Assert.That(json, Does.Not.Contain("previous"))
    Assert.That(json, Does.Not.Contain("Sha256"))
    use parsed = JsonDocument.Parse json
    Assert.That(parsed.RootElement.GetProperty("Prompt").GetProperty("Utf8Bytes").GetInt64(), Is.EqualTo(result.Prompt.Utf8Bytes))

[<Test>]
let ``invalid data rejected without replacement or caller text`` () =
    let invalid = String([|char 0xD800|])
    let reject f = Assert.Throws<ArgumentException>(TestDelegate(fun () -> f () |> ignore)) |> ignore
    reject (fun () -> measure [framing invalid])
    reject (fun () -> measure [framing invalid; framing (String([|char 0xDC00|]))])
    reject (fun () -> measure [framing null])
    reject (fun () -> measure [fragment (enum<SectionKind> 999) None None "x"])
    reject (fun () -> measure [role -1 1 "x"])
    reject (fun () -> measure [role 0 -1 "x"])
    reject (fun () -> measure [fragment SectionKind.Framing (Some 0) None "x"])
    reject (fun () -> account [] (Some(ActualPrompt invalid)) None noBudgets)
    reject (fun () -> account [] None (Some invalid) noBudgets)
    reject (fun () -> account [] (Some(SizeOnly { Utf8Bytes = 1L; Utf16Units = 2L })) None noBudgets)
    reject (fun () -> account [] None None { noBudgets with PromptUtf8Bytes = Some -1L })
