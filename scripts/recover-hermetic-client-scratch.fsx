#r "System.IO.FileSystem.AccessControl"
#r "System.Security.AccessControl"
#r "System.Security.Principal.Windows"
#load "../Client.Tests/HermeticWindowsGuard.fs"

// Recovery of one explicitly identified, formerly private WQ-11 scratch root.
// No testhost is started, no wildcards are accepted, and no real Windows user
// profile path may overlap the token- and known-folder-attested parent.
open System
open System.Diagnostics
open System.IO
open System.Text.RegularExpressions
open Jarvis.Testing

let args =
    fsi.CommandLineArgs |> Array.skip 1 |> Array.toList
    |> function | "--" :: tail -> tail | rest -> rest

let scratchParent, scratchId =
    match args with
    | [ "--scratch-parent"; parent; "--scratch-id"; id ] -> parent, id
    | _ -> failwith "Usage: dotnet fsi scripts/recover-hermetic-client-scratch.fsx -- --scratch-parent ABSOLUTE_PATH --scratch-id jarvis-client-tests-32hex"

if not (OperatingSystem.IsWindows()) then
    failwith "Explicit scratch recovery is Windows-only."

if not (Regex.IsMatch(scratchId, @"\Ajarvis-client-tests-[a-f0-9]{32}\z", RegexOptions.CultureInvariant)) then
    failwith "Recovery refuses a scratch ID not matching the exact 32-hex disposable prefix."

// This attests token-derived physical profile and native known folders
// before any writes; the recovery never operates on an arbitrary path.
let parent = WindowsDisposableGuard.attestScratchParent scratchParent
let root = Path.GetFullPath(Path.Combine(parent, scratchId))
if not (String.Equals(Path.GetDirectoryName root, parent, StringComparison.OrdinalIgnoreCase)) then
    failwith "Recovered root must be an immediate child of the attested parent."

let rec removeTree (path: string) =
    let attributes = File.GetAttributes path
    if attributes.HasFlag(FileAttributes.ReparsePoint) then
        if attributes.HasFlag(FileAttributes.Directory) then Directory.Delete(path)
        else File.Delete(path)
    elif attributes.HasFlag(FileAttributes.Directory) then
        for child in Directory.EnumerateFileSystemEntries(path) |> Seq.toArray do
            removeTree child
        if attributes.HasFlag(FileAttributes.ReadOnly) then
            File.SetAttributes(path, attributes &&& ~~~FileAttributes.ReadOnly)
        Directory.Delete(path)
    else
        if attributes.HasFlag(FileAttributes.ReadOnly) then
            File.SetAttributes(path, attributes &&& ~~~FileAttributes.ReadOnly)
        File.Delete(path)

let elapsed = Stopwatch.StartNew()
let mutable retries = 0
let rec safelyRemove () =
    if Directory.Exists root then
        WindowsDisposableGuard.assertNoLinkedAncestors root
        WindowsDisposableGuard.assertOwnerOnly root true true
        try
            removeTree root
        with
        | :? IOException when elapsed.Elapsed < TimeSpan.FromSeconds 30. ->
            retries <- retries + 1
            System.Threading.Thread.Sleep(200)
            safelyRemove ()

safelyRemove ()
// Directory.Exists returns false on access denial as well as absence.
// Only a native missing-path result is proof that the scratch is gone.
try
    File.GetAttributes(root) |> ignore
    failwith "Recovery did not remove the explicit scratch root."
with
| :? FileNotFoundException
| :? DirectoryNotFoundException -> ()
printfn "Hermetic Client.Tests: explicitly attested stale scratch %s; cleanup retries %d; scratch cleanup verified = true" scratchId retries
