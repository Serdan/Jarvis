#r "../Client/bin/Debug/net11.0/JarvisClient.dll"

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
    RoslynLsp.disposeCheckout secondRoot
    Directory.Delete(secondRoot, true)
    printfn "SMOKE PASS"
finally
    RoslynLsp.disposeCheckout root
    Directory.Delete(root, true)
