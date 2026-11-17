module CodeQualityHooksTests

open System
open System.IO
open System.Text.Json
open Client
open Client.CodeQualityHooks
open Client.RoslynLsp
open NUnit.Framework
open FsUnitTyped

let private withTempRoot action =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-quality-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(root) |> ignore
    try action root
    finally Directory.Delete(root, true)

let private config (root: string) (text: string) =
    File.WriteAllText(Path.Combine(root, ".jarvis.json"), text)

let private edits (content: string) (json: string) =
    use document = JsonDocument.Parse(json)
    applyTextEdits content document.RootElement

[<Test>]
let ``code quality is disabled in a repository without hooks`` () =
    withTempRoot (fun root ->
        load root |> shouldEqual (Ok [])
        run root AfterWrite [ "Program.cs" ] |> shouldEqual (Ok None))

[<Test>]
let ``declared hooks remain disabled until explicitly enabled`` () =
    withTempRoot (fun root ->
        config root """{"codeQuality":{"hooks":[{"name":"format","adapter":"roslyn-format","trigger":"after-write"}]}}"""
        load root |> shouldEqual (Ok []))

[<Test>]
let ``enabled Roslyn hook preserves scope, policy, and opt in`` () =
    withTempRoot (fun root ->
        config root """{"codeQuality":{"hooks":[{"enabled":true,"name":"format-csharp","adapter":"roslyn-format","trigger":"pre-commit","extensions":[".cs"],"autoFix":false,"failure":"advisory"}]}}"""
        match load root with
        | Ok [ hook ] ->
            hook.Name |> shouldEqual "format-csharp"
            hook.Trigger |> shouldEqual PreCommit
            hook.AutoFix |> shouldEqual false
            hook.Policy |> shouldEqual Advisory
            hook.Extensions |> shouldEqual [ ".cs" ]
        | other -> Assert.Fail(sprintf "Unexpected config %A" other))

[<TestCase("fake-executable")>]
[<TestCase("shell")>]
let ``unsafe adapters cannot be configured by a repository`` adapter =
    withTempRoot (fun root ->
        config root (sprintf """{"codeQuality":{"hooks":[{"enabled":true,"name":"unsafe","adapter":"%s","trigger":"after-write"}]}}""" adapter)
        match load root with
        | Error message -> message.Contains("untrusted") |> shouldEqual true
        | other -> Assert.Fail(sprintf "Expected rejection, got %A" other))

[<Test>]
let ``arbitrary executable fields are rejected rather than interpreted`` () =
    withTempRoot (fun root ->
        config root """{"codeQuality":{"hooks":[{"enabled":true,"name":"unsafe","adapter":"roslyn-format","trigger":"after-write","executable":"sh","args":["-c","echo unsafe"]}]}}"""
        match load root with
        | Error message -> message.Contains("shell") |> shouldEqual true
        | other -> Assert.Fail(sprintf "Expected rejection, got %A" other))

[<Test>]
let ``unsafe extension globs are rejected`` () =
    withTempRoot (fun root ->
        config root """{"codeQuality":{"hooks":[{"enabled":true,"name":"unsafe","adapter":"roslyn-format","trigger":"after-write","extensions":["**/*"]}]}}"""
        match load root with
        | Error message -> message.Contains("extensions") |> shouldEqual true
        | other -> Assert.Fail(sprintf "Expected rejection, got %A" other))

[<Test>]
let ``repository config alone cannot approve launching project analyzers`` () =
    withTempRoot (fun root ->
        match requireApproval root with
        | Error(Client.PermissionDenied message) ->
            message.Contains("JARVIS_APPROVED_CODE_QUALITY_ROOTS") |> shouldEqual true
        | other -> Assert.Fail(sprintf "Expected unapproved checkout, got %A" other))

[<Test; NonParallelizable>]
let ``operator approval is specific to the exact checkout, not siblings`` () =
    withTempRoot (fun root ->
        let previous = Environment.GetEnvironmentVariable("JARVIS_APPROVED_CODE_QUALITY_ROOTS")
        try
            let first = Path.Combine(root, "one")
            let second = Path.Combine(root, "two")
            Directory.CreateDirectory(first) |> ignore
            Directory.CreateDirectory(second) |> ignore
            Environment.SetEnvironmentVariable("JARVIS_APPROVED_CODE_QUALITY_ROOTS", first)
            requireApproval first |> shouldEqual (Ok ())
            match requireApproval second with
            | Error(Client.PermissionDenied _) -> ()
            | other -> Assert.Fail(sprintf "Unexpected sibling approval: %A" other)
        finally
            Environment.SetEnvironmentVariable("JARVIS_APPROVED_CODE_QUALITY_ROOTS", previous))

[<Test>]
let ``UTF16 Roslyn edits preserve non-BMP characters`` () =
    let original = "a\U0001f600b\nz"
    let json =
        """[{"range":{"start":{"line":0,"character":1},"end":{"line":0,"character":3}},"newText":"X"}]"""
    edits original json |> shouldEqual (Ok("aXb\nz", 1))

[<Test>]
let ``Roslyn edits preserve CRLF between lines`` () =
    let original = "first\r\nlast\r\n"
    let json =
        """[{"range":{"start":{"line":1,"character":0},"end":{"line":1,"character":1}},"newText":"L"}]"""
    edits original json |> shouldEqual (Ok("first\r\nLast\r\n", 1))

[<Test>]
let ``multiple non-overlapping Roslyn edits are applied in original document coordinates`` () =
    let json =
        """[{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}},"newText":"A"},{"range":{"start":{"line":1,"character":0},"end":{"line":1,"character":1}},"newText":"B"}]"""
    edits "a\nb" json |> shouldEqual (Ok("A\nB", 2))

[<Test>]
let ``overlapping Roslyn edits fail closed`` () =
    let json =
        """[{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":2}},"newText":"X"},{"range":{"start":{"line":0,"character":1},"end":{"line":0,"character":3}},"newText":"Y"}]"""
    match edits "abcdef" json with
    | Error message -> message.Contains("Overlapping") |> shouldEqual true
    | other -> Assert.Fail(sprintf "Expected conflict, got %A" other)

[<Test>]
let ``two Roslyn insertions at the same offset fail closed`` () =
    let json =
        """[{"range":{"start":{"line":0,"character":1},"end":{"line":0,"character":1}},"newText":"X"},{"range":{"start":{"line":0,"character":1},"end":{"line":0,"character":1}},"newText":"Y"}]"""
    match edits "abc" json with
    | Error message -> message.Contains("Overlapping") |> shouldEqual true
    | other -> Assert.Fail(sprintf "Expected ambiguous insert rejection, got %A" other)

[<Test>]
let ``C-sharp hooks do not require approval for unrelated paths`` () =
    withTempRoot (fun root ->
        config root """{"codeQuality":{"hooks":[{"enabled":true,"name":"format","adapter":"roslyn-format","trigger":"after-write","extensions":[".cs"]}]}}"""
        match load root with
        | Ok hooks ->
            hasMatchingHook hooks AfterWrite [ "notes.md" ] |> shouldEqual false
            run root AfterWrite [ "notes.md" ] |> shouldEqual (Ok None)
        | Error message -> Assert.Fail message)

[<Test>]
let ``out-of-range Roslyn edits fail without partial output`` () =
    let json =
        """[{"range":{"start":{"line":4,"character":0},"end":{"line":4,"character":1}},"newText":"X"}]"""
    match edits "one\ntwo" json with
    | Error message -> message.Contains("outside") |> shouldEqual true
    | other -> Assert.Fail(sprintf "Expected range rejection, got %A" other)


[<Test; NonParallelizable>]
let ``Roslyn session recovers after unavailable server and explicit checkout disposal`` () =
    let installed = Environment.GetEnvironmentVariable("JARVIS_ROSLYN_LSP_DLL")
    if String.IsNullOrWhiteSpace installed then
        Assert.Ignore("Set JARVIS_ROSLYN_LSP_DLL to run the live Roslyn recovery test.")
    withTempRoot (fun root ->
        let file = Path.Combine(root, "Recover.cs")
        let content = "class Recover{int A=1;}"
        File.WriteAllText(file, content)
        let invalid = Path.Combine(root, "roslyn-language-server.dll")
        File.WriteAllText(invalid, "intentionally invalid managed tool")
        try
            Environment.SetEnvironmentVariable("JARVIS_ROSLYN_LSP_DLL", invalid)
            match formatDocument root file content with
            | Error _ -> ()
            | Ok _ -> Assert.Fail("Invalid Roslyn entrypoint should fail closed.")
            Environment.SetEnvironmentVariable("JARVIS_ROSLYN_LSP_DLL", installed)
            prewarm root
            prewarm root
            match formatDocument root file content with
            | Ok result -> result.Content.Contains("class Recover") |> shouldEqual true
            | Error error -> Assert.Fail(sprintf "Failed to restart Roslyn: %s" error)
            disposeCheckout root
            match formatDocument root file content with
            | Ok result -> result.Content.Contains("class Recover") |> shouldEqual true
            | Error error -> Assert.Fail(sprintf "Failed to restore disposed Roslyn session: %s" error)
        finally
            disposeCheckout root
            Environment.SetEnvironmentVariable("JARVIS_ROSLYN_LSP_DLL", installed))


// Model the filesystem Git metadata layout without invoking Git or creating
// a repository: this fast-negative gate must never enumerate worktrees.
let private linkedWorktree mainRoot name =
    let gitAdmin = Path.Combine(mainRoot, ".git", "worktrees", name)
    let checkout = Path.Combine(Path.GetDirectoryName mainRoot, name)
    Directory.CreateDirectory(gitAdmin) |> ignore
    Directory.CreateDirectory(checkout) |> ignore
    File.WriteAllText(Path.Combine(gitAdmin, "commondir"), "../..\n")
    File.WriteAllText(Path.Combine(checkout, ".git"), "gitdir: " + gitAdmin + "\n")
    checkout

[<Test; NonParallelizable>]
let ``inbox prewarm avoids Git discovery for unapproved and no-hook roots`` () =
    withTempRoot (fun root ->
        let main = Path.Combine(root, "main")
        let checkout = linkedWorktree main "worker"
        let previous = Environment.GetEnvironmentVariable("JARVIS_APPROVED_CODE_QUALITY_ROOTS")
        try
            Environment.SetEnvironmentVariable("JARVIS_APPROVED_CODE_QUALITY_ROOTS", checkout)
            hasApprovedPrewarmCandidate main |> shouldEqual false
            config checkout """{"codeQuality":{"hooks":[{"enabled":false,"name":"format","adapter":"roslyn-format","trigger":"after-write"}]}}"""
            hasApprovedPrewarmCandidate main |> shouldEqual false
            config checkout """{"codeQuality":{"hooks":[{"enabled":true,"name":"format","adapter":"roslyn-format","trigger":"after-write"}]}}"""
            hasApprovedPrewarmCandidate main |> shouldEqual true
            Environment.SetEnvironmentVariable("JARVIS_APPROVED_CODE_QUALITY_ROOTS", main)
            hasApprovedPrewarmCandidate main |> shouldEqual false
            Environment.SetEnvironmentVariable("JARVIS_APPROVED_CODE_QUALITY_ROOTS", checkout + "-unapproved")
            hasApprovedPrewarmCandidate main |> shouldEqual false
        finally
            Environment.SetEnvironmentVariable("JARVIS_APPROVED_CODE_QUALITY_ROOTS", previous))

[<Test; NonParallelizable>]
let ``approved hooks from another project cannot prewarm this project's inbox`` () =
    withTempRoot (fun root ->
        let aMain = Path.Combine(root, "main-a")
        let bMain = Path.Combine(root, "main-b")
        let aCheckout = linkedWorktree aMain "a-worktree"
        let bCheckout = linkedWorktree bMain "b-worktree"
        let enabled = """{"codeQuality":{"hooks":[{"enabled":true,"name":"format","adapter":"roslyn-format","trigger":"after-write"}]}}"""
        config aCheckout enabled
        let previous = Environment.GetEnvironmentVariable("JARVIS_APPROVED_CODE_QUALITY_ROOTS")
        try
            Environment.SetEnvironmentVariable("JARVIS_APPROVED_CODE_QUALITY_ROOTS", aCheckout)
            hasApprovedPrewarmCandidate aMain |> shouldEqual true
            hasApprovedPrewarmCandidate bMain |> shouldEqual false
            // Even an unapproved enabled hook in B must not use A's approval.
            config bCheckout enabled
            hasApprovedPrewarmCandidate bMain |> shouldEqual false
            // An explicitly approved and hooked B checkout becomes eligible.
            Environment.SetEnvironmentVariable(
                "JARVIS_APPROVED_CODE_QUALITY_ROOTS",
                aCheckout + string Path.PathSeparator + bCheckout)
            hasApprovedPrewarmCandidate bMain |> shouldEqual true
            hasApprovedPrewarmCandidate aMain |> shouldEqual true
        finally
            Environment.SetEnvironmentVariable("JARVIS_APPROVED_CODE_QUALITY_ROOTS", previous))


[<Test; NonParallelizable>]
let ``one approved hooked sibling cannot permanently cache an unapproved selected checkout`` () =
    withTempRoot (fun root ->
        let main = Path.Combine(root, "main")
        let approved = linkedWorktree main "approved"
        let selected = linkedWorktree main "selected"
        let enabled = """{"codeQuality":{"hooks":[{"enabled":true,"name":"format","adapter":"roslyn-format","trigger":"after-write"}]}}"""
        config approved enabled
        config selected enabled
        let previous = Environment.GetEnvironmentVariable("JARVIS_APPROVED_CODE_QUALITY_ROOTS")
        try
            Environment.SetEnvironmentVariable("JARVIS_APPROVED_CODE_QUALITY_ROOTS", approved)
            hasApprovedPrewarmCandidate main |> shouldEqual true
            hasApprovedPrewarmCheckout approved |> shouldEqual true
            hasApprovedPrewarmCheckout selected |> shouldEqual false
            tryPrewarm selected |> shouldEqual false
            // The same source-ref checkout can become eligible later without
            // changing the assignment key or borrowing another root's approval.
            Environment.SetEnvironmentVariable(
                "JARVIS_APPROVED_CODE_QUALITY_ROOTS",
                approved + string Path.PathSeparator + selected)
            hasApprovedPrewarmCheckout selected |> shouldEqual true
            hasApprovedPrewarmCandidate main |> shouldEqual true
        finally
            Environment.SetEnvironmentVariable("JARVIS_APPROVED_CODE_QUALITY_ROOTS", previous))
