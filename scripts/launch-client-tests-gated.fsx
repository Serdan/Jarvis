#r "System.IO.FileSystem.AccessControl"
#r "System.Security.AccessControl"
#r "System.Security.Principal.Windows"
#load "../Client.Tests/HermeticWindowsGuard.fs"

// Test-only Windows launch gate. FSI itself must never start VSTest until
// the parent has atomically confirmed our membership in a kill-on-close job.
// This prevents VSTest from spawning a pre-assignment orphaned testhost.
open System
open System.Diagnostics
open System.IO
open System.Threading
open Jarvis.Testing

let args =
    fsi.CommandLineArgs |> Array.skip 1 |> Array.toList
    |> function | "--" :: tail -> tail | rest -> rest

let gateArgument, executable, childArgs =
    match args with
    | "--gate-file" :: gate :: "--" :: executable :: rest
        when not (String.IsNullOrWhiteSpace executable) -> gate, executable, rest
    | _ -> failwith "Gated Windows launcher requires --gate-file ABSOLUTE -- executable args."

if not (OperatingSystem.IsWindows()) then
    failwith "Gated VSTest launch is Windows-only."
if not (Path.IsPathFullyQualified gateArgument) then
    failwith "The Windows test launch gate must be an absolute path."

let rawRoot = Environment.GetEnvironmentVariable("JARVIS_CLIENT_TEST_HERMETIC_ROOT")
if String.IsNullOrWhiteSpace rawRoot || not (Path.IsPathFullyQualified rawRoot) then
    failwith "The Windows gated launcher requires a disposable scratch root."
let root = Path.TrimEndingDirectorySeparator(Path.GetFullPath rawRoot)
let gatePath = Path.GetFullPath gateArgument
if not (String.Equals(Path.GetDirectoryName gatePath, root, StringComparison.OrdinalIgnoreCase))
   || not (String.Equals(Path.GetFileName gatePath, ".process-start-gate", StringComparison.Ordinal)) then
    failwith "Windows launch gate must be the exact child of its private root."

WindowsDisposableGuard.assertNoLinkedAncestors root
WindowsDisposableGuard.assertOwnerOnly root true true
WindowsDisposableGuard.attestScratchParentTokenOnly (Path.GetDirectoryName root) |> ignore

// The gate file must be created POSITIVELY after job assignment. A dead
// parent leaves no gate, so an unassigned FSI process times out harmlessly.
// A file-access error or broad/reparse ACL is not treated as authorization.
let timeout = Stopwatch.StartNew()
let mutable ready = false
while not ready && timeout.Elapsed < TimeSpan.FromSeconds 20. do
    if not (WindowsDisposableGuard.provenAbsent gatePath) then
        WindowsDisposableGuard.assertOwnerOnly gatePath false false
        ready <- true
    else Thread.Sleep 50
if not ready then
    failwith "No verified Windows job-assignment release gate; refusing to launch VSTest."

let start = ProcessStartInfo(executable)
start.UseShellExecute <- false
start.WorkingDirectory <- Environment.CurrentDirectory
// Preserve inherited synthetic HOME/APPDATA/TMP and Git identity exactly;
// no shell or ambient profile resolution is required.
for arg in childArgs do start.ArgumentList.Add arg
let child = new Process(StartInfo = start)
let exitCode =
    try
        if not (child.Start()) then
            failwith "Gated launcher could not start isolated VSTest."
        // The parent owns the bounded deadline and Windows job termination.
        child.WaitForExit()
        child.ExitCode
    finally child.Dispose()
Environment.Exit(exitCode)
