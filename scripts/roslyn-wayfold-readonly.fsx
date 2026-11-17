#r "../Common/bin/Release/net11.0/Common.dll"
#r "../Client/bin/Release/net11.0/JarvisClient.dll"

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open Client

// Read-only against the supplied Wayfold checkout. All transformed source
// text stays in memory; dotnet format runs only on copies in a disposable root.
let args = fsi.CommandLineArgs |> Array.skip 1
if args.Length <> 1 then
    failwith "Usage: dotnet fsi scripts/roslyn-wayfold-readonly.fsx -- /absolute/Wayfold-checkout"
let root = Path.GetFullPath args[0]
let files =
    [ "small", "src/Wayfold.Desktop/InformationOverlayUiState.cs"
      "large", "src/Wayfold.Desktop/ImportedPreviewSessionController.cs" ]

let sha (text: string) =
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes text)).ToLowerInvariant()

let inMemoryEdit (content: string) =
    let original = "namespace Wayfold.Desktop;"
    if not (content.Contains(original, StringComparison.Ordinal)) then
        failwith "Expected namespace anchor is missing; refusing unrelated edit."
    content.Replace(original, "namespace    Wayfold.Desktop;", StringComparison.Ordinal)

let format label filePath (content: string) =
    let watch = Stopwatch.StartNew()
    let result = RoslynLsp.formatDocument root filePath content
    watch.Stop()
    match result with
    | Error message -> failwithf "%s: Roslyn failed after %d ms: %s" label watch.ElapsedMilliseconds message
    | Ok formatted ->
        printfn "ROSLYN %s durationMs=%d editCount=%d diagnostics=%d inputSha=%s outputSha=%s finalNewline=%b"
            label watch.ElapsedMilliseconds formatted.EditCount formatted.Diagnostics.Length
            (sha content) (sha formatted.Content) (formatted.Content.EndsWith("\n", StringComparison.Ordinal))
        for diagnostic in formatted.Diagnostics |> List.truncate 4 do
            printfn "DIAGNOSTIC %s: %s" label diagnostic
        formatted.Content

let scratch = Path.Combine(Path.GetTempPath(), "jarvis-roslyn-wayfold-parity-" + Guid.NewGuid().ToString("N"))
let hookRoot = scratch + "-hook"
Directory.CreateDirectory scratch |> ignore
Directory.CreateDirectory hookRoot |> ignore

let printRss () =
    if OperatingSystem.IsLinux() then
        for procDir in Directory.EnumerateDirectories("/proc") do
            match Int32.TryParse(Path.GetFileName procDir) with
            | false, _ -> ()
            | true, pid ->
                try
                    let command = File.ReadAllText(Path.Combine(procDir, "cmdline")).Replace('\u0000', ' ')
                    if command.Contains("roslyn-language-server", StringComparison.OrdinalIgnoreCase) then
                        let rss =
                            File.ReadLines(Path.Combine(procDir, "status"))
                            |> Seq.tryFind (fun line -> line.StartsWith("VmRSS:", StringComparison.Ordinal))
                            |> Option.defaultValue "VmRSS: unknown"
                        printfn "PROCESS pid=%d %s command=%s" pid rss (command.Substring(0, min 150 command.Length))
                with _ -> ()

try
    File.Copy(Path.Combine(root, ".editorconfig"), Path.Combine(scratch, ".editorconfig"))
    File.Copy(Path.Combine(root, ".editorconfig"), Path.Combine(hookRoot, ".editorconfig"))
    File.WriteAllText(Path.Combine(hookRoot, ".jarvis.json"),
        """{"codeQuality":{"hooks":[{"enabled":true,"name":"read-only-wayfold-parity","trigger":"after-write",
            "adapter":"roslyn-format","extensions":[".cs"],"autoFix":true,"failure":"blocking"}]}}""")
    let inputs =
        files |> List.map (fun (label, relative) ->
            let filePath = Path.Combine(root, relative)
            let original = File.ReadAllText filePath
            let modified = inMemoryEdit original
            printfn "SOURCE %s bytes=%d originalSha=%s editedSha=%s" label
                (Encoding.UTF8.GetByteCount original) (sha original) (sha modified)
            label, filePath, modified)
    let formatted =
        inputs |> List.map (fun (label, path, modified) ->
            let first = format (label + "-first") path modified
            let again = format (label + "-warm-edit") path (inMemoryEdit first)
            label, path, modified, again)
    printRss()
    for label, _, modified, _ in formatted do
        let fileName = if label = "small" then "InformationOverlayUiState.cs" else "ImportedPreviewSessionController.cs"
        File.WriteAllText(Path.Combine(scratch, fileName), modified, UTF8Encoding(false))
        File.WriteAllText(Path.Combine(hookRoot, fileName), modified, UTF8Encoding(false))
    // Exercise the complete configured hook on disposable copies, never the
    // original Wayfold checkout. Approval is temporary to this script process.
    let priorApproval = Environment.GetEnvironmentVariable("JARVIS_APPROVED_CODE_QUALITY_ROOTS")
    try
        Environment.SetEnvironmentVariable("JARVIS_APPROVED_CODE_QUALITY_ROOTS", hookRoot)
        for label, _, _, _ in formatted do
            let fileName = if label = "small" then "InformationOverlayUiState.cs" else "ImportedPreviewSessionController.cs"
            match CodeQualityHooks.run hookRoot CodeQualityHooks.AfterWrite [ fileName ] with
            | Ok(Some report) when report.BlockingFailures.IsEmpty ->
                let text = File.ReadAllText(Path.Combine(hookRoot, fileName))
                printfn "HOOK %s modified=%b reportCount=%d finalNewline=%b outputSha=%s"
                    label (report.ModifiedFiles |> List.contains fileName) report.Reports.Length
                    (text.EndsWith("\n", StringComparison.Ordinal)) (sha text)
            | other -> failwithf "Configured hook failed for %s: %A" label other
    finally
        Environment.SetEnvironmentVariable("JARVIS_APPROVED_CODE_QUALITY_ROOTS", priorApproval)
    use dotnet = new Process()
    let info = ProcessStartInfo("dotnet")
    info.ArgumentList.Add "format"
    info.ArgumentList.Add "whitespace"
    info.ArgumentList.Add "--folder"
    info.ArgumentList.Add scratch
    info.ArgumentList.Add "--verbosity"
    info.ArgumentList.Add "quiet"
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    dotnet.StartInfo <- info
    if not (dotnet.Start()) then failwith "Could not start dotnet format whitespace."
    let output = dotnet.StandardOutput.ReadToEnd()
    let errors = dotnet.StandardError.ReadToEnd()
    if not (dotnet.WaitForExit(120000)) then
        dotnet.Kill(true)
        failwith "dotnet format whitespace exceeded 120 seconds."
    printfn "DOTNET_FORMAT exit=%d stdout=%s stderr=%s" dotnet.ExitCode output errors
    if dotnet.ExitCode <> 0 then failwith "dotnet format whitespace baseline failed."
    for label, _, _, roslyn in formatted do
        let fileName = if label = "small" then "InformationOverlayUiState.cs" else "ImportedPreviewSessionController.cs"
        let reference = File.ReadAllText(Path.Combine(scratch, fileName))
        let hook = File.ReadAllText(Path.Combine(hookRoot, fileName))
        printfn "HOOK_PARITY %s identical=%b hookSha=%s dotnetSha=%s hookFinalNewline=%b dotnetFinalNewline=%b"
            label (String.Equals(hook, reference, StringComparison.Ordinal))
            (sha hook) (sha reference) (hook.EndsWith("\n", StringComparison.Ordinal))
            (reference.EndsWith("\n", StringComparison.Ordinal))
        printfn "PARITY %s identical=%b roslynSha=%s dotnetSha=%s roslynFinalNewline=%b dotnetFinalNewline=%b"
            label (String.Equals(roslyn, reference, StringComparison.Ordinal))
            (sha roslyn) (sha reference)
            (roslyn.EndsWith("\n", StringComparison.Ordinal))
            (reference.EndsWith("\n", StringComparison.Ordinal))
finally
    RoslynLsp.disposeCheckout root
    RoslynLsp.disposeCheckout hookRoot
    if Directory.Exists scratch then Directory.Delete(scratch, true)
    if Directory.Exists hookRoot then Directory.Delete(hookRoot, true)
