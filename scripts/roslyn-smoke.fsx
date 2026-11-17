#r "../Common/bin/Release/net11.0/Common.dll"
#r "../Client/bin/Release/net11.0/JarvisClient.dll"

open System
open System.Diagnostics
open System.IO
open Client

let root = Path.Combine(Path.GetTempPath(), "jarvis-roslyn-smoke-" + Guid.NewGuid().ToString("N"))
Directory.CreateDirectory(root) |> ignore
let path = Path.Combine(root, "Sample.cs")
let input = "class Sample{public int Add(int a,int b){return a+b;}}"
File.WriteAllText(path, input)
let print label (result: Result<RoslynLsp.FormatResult, string>) (elapsed: int) =
    match result with
    | Error message -> failwithf "%s Roslyn failed after %d ms: %s" label elapsed message
    | Ok formatted ->
        printfn "%s: %d ms, %d edits, %d diagnostics, content=%s" label elapsed formatted.EditCount formatted.Diagnostics.Length (formatted.Content.Replace("\n", "\\n"))
        formatted.Content

try
    let watch = Stopwatch.StartNew()
    let first = RoslynLsp.formatDocument root path input
    let firstTime = int watch.ElapsedMilliseconds
    let formatted = print "cold" first firstTime
    watch.Restart()
    let second = RoslynLsp.formatDocument root path formatted
    let warmTime = int watch.ElapsedMilliseconds
    let warm = print "warm" second warmTime
    if not (warm.Contains("class Sample")) then failwith "Roslyn unexpectedly dropped class content."
    let secondRoot = root + "-other"
    Directory.CreateDirectory(secondRoot) |> ignore
    let secondPath = Path.Combine(secondRoot, "Other.cs")
    let otherInput = "class Other{public int X=1;}"
    File.WriteAllText(secondPath, otherInput)
    watch.Restart()
    let third = RoslynLsp.formatDocument secondRoot secondPath otherInput
    let otherTime = int watch.ElapsedMilliseconds
    print "other-worktree" third otherTime |> ignore
    let config =
        """{"codeQuality":{"hooks":[{"enabled":true,"name":"format-csharp","trigger":"after-write","adapter":"roslyn-format","extensions":[".cs"],"autoFix":true}]}}"""
    for checkout in [ root; secondRoot ] do
        File.WriteAllText(Path.Combine(checkout, ".jarvis.json"), config)
    File.WriteAllText(Path.Combine(root, ".editorconfig"), "[*.cs]\ninsert_final_newline = true\n")
    File.WriteAllText(Path.Combine(secondRoot, ".editorconfig"), "[*.cs]\ninsert_final_newline = false\n")
    let previousApproval = Environment.GetEnvironmentVariable("JARVIS_APPROVED_CODE_QUALITY_ROOTS")
    try
        Environment.SetEnvironmentVariable(
            "JARVIS_APPROVED_CODE_QUALITY_ROOTS",
            String.Join(string Path.PathSeparator, [ root; secondRoot ]))
        for checkout, relative in [ root, "Sample.cs"; secondRoot, "Other.cs" ] do
            match CodeQualityHooks.run checkout CodeQualityHooks.AfterWrite [ relative ] with
            | Ok(Some report) when report.ModifiedFiles = [ relative ] -> ()
            | other -> failwithf "Hook failed for %s: %A" relative other
        if not (File.ReadAllText(path).EndsWith("\n")) then
            failwith "First checkout ignored insert_final_newline = true."
        if File.ReadAllText(secondPath).EndsWith("\n") then
            failwith "Second checkout unexpectedly added a final newline."
        printfn "HOOK PASS: checkout-specific final newline rules"
    finally
        Environment.SetEnvironmentVariable("JARVIS_APPROVED_CODE_QUALITY_ROOTS", previousApproval)
    RoslynLsp.disposeCheckout secondRoot
    Directory.Delete(secondRoot, true)
    printfn "SMOKE PASS"
finally
    RoslynLsp.disposeCheckout root
    Directory.Delete(root, true)
