module AtomicTextFilesTests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
open Client
open Client.IO
open Client.ProjectBrowser
open Common
open NUnit.Framework

let private createRoot () =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-atomic-text-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(Path.Combine(root, "Project1")) |> ignore
    root

let private projectPath root = Path.Combine(root, "Project1")

let private cleanup root =
    if Directory.Exists(root) then Directory.Delete(root, true)

type private RealContext(root: string) =
    interface ProjectIO with
        member _.Project =
            { Root = ProjectDirectory root; SpecialFiles = []; FolderFilters = [] }
    interface FileIO with
        member _.File = FileOperations.impl
        member _.EditText(path, options, plan) =
            AtomicTextFiles.edit path options plan

let private write mode expected relative content createParents =
    { ProjectName = "Project1"
      FilePath = relative
      Content = content
      FileWriteMode = mode
      ExpectedHash = expected
      CreateParents = Some createParents }

let private digest (text: string) =
    SHA256.HashData(Encoding.UTF8.GetBytes text)
    |> Convert.ToHexString
    |> fun value -> "sha256:" + value.ToLowerInvariant()

let private editOptions root =
    { SourceRoot = FolderPath(projectPath root)
      CreateParents = false
      DryRun = false }

let private assertNoStages (path: string) =
    let name = Path.GetFileName path
    let dir = Path.GetDirectoryName path
    let remaining = Directory.GetFiles(dir, $".{name}.*.jarvis-tmp")
    Assert.That(remaining, Is.Empty, "Temporary staged content must be cleaned after completion or error.")

[<Test>]
let write_and_append_use_same_transaction_and_preserve_Unicode () =
    let root = createRoot()
    try
        let context = RealContext root
        let path = Path.Combine(projectPath root, "nested", "u.txt")
        let initial = "café ☕\n"
        Assert.That(writeFile (write FileWriteMode.Write (Some "none") "nested/u.txt" initial true) context |> Result.isOk,
                    Is.True)
        Assert.That(writeFile (write FileWriteMode.Append (Some(digest initial)) "nested/u.txt" "😀\n" false) context |> Result.isOk,
                    Is.True)
        Assert.That(File.ReadAllText(path), Is.EqualTo("café ☕\n😀\n"))
        Assert.That(writeFile (write FileWriteMode.Write None "nested/u.txt" "" false) context |> Result.isOk,
                    Is.True)
        Assert.That(File.ReadAllText path, Is.Empty)
        assertNoStages path
    finally cleanup root

[<Test>]
let append_preserves_existing_raw_bytes_and_BOM () =
    let root = createRoot()
    try
        let path = Path.Combine(projectPath root, "bom.txt")
        let original = Array.append [| 0xEFuy; 0xBBuy; 0xBFuy |] (Encoding.UTF8.GetBytes "hello")
        File.WriteAllBytes(path, original)
        let result = writeFile (write FileWriteMode.Append None "bom.txt" " 😊" false) (RealContext root)
        Assert.That(result |> Result.isOk, Is.True)
        let expected = Array.append original (Encoding.UTF8.GetBytes " 😊")
        Assert.That((File.ReadAllBytes path = expected), Is.True)
        assertNoStages path
    finally cleanup root

[<Test>]
let failed_hash_precondition_does_not_create_parent_directories () =
    let root = createRoot()
    try
        let context = RealContext root
        let parent = Path.Combine(projectPath root, "absent", "nested")
        let result =
            writeFile
                (write FileWriteMode.Write (Some "sha256:notpresent") "absent/nested/new.txt" "unused" true)
                context
        match result with
        | Error(ValidationError message) -> Assert.That(message, Does.Contain("file does not exist"))
        | other -> Assert.Fail($"Expected stale hash with missing source, got {other}")
        Assert.That(Directory.Exists parent, Is.False)
    finally cleanup root

[<Test>]
let patch_transaction_retains_diagnostics_and_exact_target_mode () =
    let root = createRoot()
    try
        let path = Path.Combine(projectPath root, "script.sh")
        File.WriteAllText(path, "old\n")
        if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
            File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                                   ||| UnixFileMode.GroupRead ||| UnixFileMode.GroupExecute)
        let permissions =
            if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then Some(File.GetUnixFileMode path)
            else None
        let cmd =
            { ProjectName = "Project1"
              FilePath = "script.sh"
              ExpectedHash = Some(digest "old\n")
              Format = PatchFormat.UnifiedDiff
              Patch = "--- a/script.sh\n+++ b/script.sh\n@@ -1 +1 @@\n-old\n+new\n"
              DryRun = Some false
              FuzzyContextLines = Some 0
              ReturnContent = Some true }
        match patchFile cmd (RealContext root) with
        | Ok response ->
            Assert.That(response.HunksApplied, Is.EqualTo(1))
            Assert.That(response.BeforeHash, Is.EqualTo(digest "old\n"))
            Assert.That(response.AfterHash, Is.EqualTo(Some(digest "new\n")))
            Assert.That(response.Content, Is.EqualTo(Some "new\n"))
            Assert.That(File.ReadAllText path, Is.EqualTo("new\n"))
            permissions |> Option.iter (fun mode -> Assert.That(File.GetUnixFileMode path, Is.EqualTo(mode)))
            assertNoStages path
        | Error issue -> Assert.Fail($"Patch did not commit: {issue}")
    finally cleanup root

[<Test>]
let new_file_inherits_normal_creation_permissions () =
    if not (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) then
        Assert.Ignore("Unix creation mode comparison is platform-specific.")
    let root = createRoot()
    try
        let folder = projectPath root
        let baseline = Path.Combine(folder, "baseline.txt")
        File.WriteAllText(baseline, "baseline")
        let expectedMode = File.GetUnixFileMode baseline
        File.Delete baseline
        let target = Path.Combine(folder, "new.txt")
        let result = writeFile (write FileWriteMode.Write (Some "none") "new.txt" "new" false) (RealContext root)
        Assert.That(result |> Result.isOk, Is.True)
        Assert.That(File.GetUnixFileMode target, Is.EqualTo(expectedMode))
    finally cleanup root

[<Test>]
let existing_unix_readonly_target_cannot_be_silently_overwritten () =
    if not (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) then
        Assert.Ignore("Unix read-only bit check is platform-specific.")
    let root = createRoot()
    try
        let path = Path.Combine(projectPath root, "readonly.txt")
        File.WriteAllText(path, "protected")
        let oldMode = File.GetUnixFileMode path
        try
            File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.GroupRead ||| UnixFileMode.OtherRead)
            let result =
                writeFile (write FileWriteMode.Write (Some(digest "protected")) "readonly.txt" "replacement" false)
                          (RealContext root)
            Assert.That(result.IsError, Is.True, "Atomic rename must not bypass an existing read-only file.")
            Assert.That(File.ReadAllText path, Is.EqualTo("protected"))
            assertNoStages path
        finally
            File.SetUnixFileMode(path, oldMode)
    finally cleanup root

[<Test>]
let failed_staged_publication_leaves_original_intact () =
    let root = createRoot()
    try
        let path = Path.Combine(projectPath root, "data.txt")
        File.WriteAllText(path, "original")
        let result =
            AtomicTextFiles.editWithBeforePublish
                (fun () -> raise (IOException "Injected failure after staged file flush"))
                (FilePath path) (editOptions root)
                (fun _ -> Ok(ReplaceText "new content", ()))
        Assert.That(result.IsError, Is.True)
        Assert.That(File.ReadAllText path, Is.EqualTo("original"))
        assertNoStages path
    finally cleanup root

[<Test>]
let prepublication_changes_from_outside_writer_cause_conflict () =
    let root = createRoot()
    try
        let path = Path.Combine(projectPath root, "data.txt")
        File.WriteAllText(path, "original")
        let result =
            AtomicTextFiles.editWithBeforePublish
                (fun () -> File.WriteAllText(path, "external change"))
                (FilePath path) (editOptions root)
                (fun _ -> Ok(ReplaceText "new content", ()))
        match result with
        | Error(ValidationError message) -> Assert.That(message, Does.Contain("changed during"))
        | other -> Assert.Fail($"Expected conflict after external source mutation: {other}")
        Assert.That(File.ReadAllText path, Is.EqualTo("external change"))
        assertNoStages path
    finally cleanup root

[<Test>]
let destination_replaced_by_symlink_before_publish_is_rejected () =
    let root = createRoot()
    let outside = Path.Combine(root, "outside.txt")
    try
        let path = Path.Combine(projectPath root, "data.txt")
        File.WriteAllText(path, "original")
        File.WriteAllText(outside, "do not change")
        let backup = Path.Combine(projectPath root, "old.txt")
        let result =
            AtomicTextFiles.editWithBeforePublish
                (fun () ->
                    File.Move(path, backup)
                    File.CreateSymbolicLink(path, outside) |> ignore)
                (FilePath path) (editOptions root)
                (fun _ -> Ok(ReplaceText "new content", ()))
        match result with
        | Error(Client.PermissionDenied _) -> ()
        | Error(Client.ExceptionError (:? UnauthorizedAccessException)) ->
            Assert.Ignore("Symbolic links unavailable in this test environment.")
        | other -> Assert.Fail($"Expected rejected symlink substitution, got {other}")
        Assert.That(File.ReadAllText(outside), Is.EqualTo("do not change"))
        Assert.That(File.ReadAllText(backup), Is.EqualTo("original"))
        assertNoStages path
    finally cleanup root

[<Test>]
let concurrent_threads_updating_same_expected_hash_cannot_both_commit () =
    let root = createRoot()
    try
        let context = RealContext root
        let path = Path.Combine(projectPath root, "shared.txt")
        File.WriteAllText(path, "original")
        use gate = new ManualResetEventSlim(false)
        let attempt value =
            System.Threading.Tasks.Task.Run(fun () ->
                gate.Wait()
                writeFile (write FileWriteMode.Write (Some(digest "original")) "shared.txt" value false) context)
        let a = attempt "one"
        let b = attempt "two"
        gate.Set()
        Assert.That(System.Threading.Tasks.Task.WaitAll([| a :> System.Threading.Tasks.Task; b :> System.Threading.Tasks.Task |], 15000), Is.True)
        let results = [ a.Result; b.Result ]
        Assert.That(results |> List.filter Result.isOk |> List.length, Is.EqualTo(1))
        Assert.That(File.ReadAllText(path), Is.AnyOf("one", "two"))
        assertNoStages path
    finally cleanup root

[<Test>]
let concurrent_readers_never_observe_partially_written_destination () =
    let root = createRoot()
    use staged = new ManualResetEventSlim(false)
    use publish = new ManualResetEventSlim(false)
    try
        let path = Path.Combine(projectPath root, "readers.txt")
        let original = String.replicate 100000 "o"
        let replacement = String.replicate 100000 "n"
        File.WriteAllText(path, original)
        let worker =
            System.Threading.Tasks.Task.Run(fun () ->
                AtomicTextFiles.editWithBeforePublish
                    (fun () ->
                        staged.Set()
                        if not (publish.Wait(10000)) then failwith "Reader barrier timed out.")
                    (FilePath path) (editOptions root)
                    (fun _ -> Ok(ReplaceText replacement, ())))
        Assert.That(staged.Wait(10000), Is.True, "Staged writer did not reach publication barrier.")
        for _ in 1 .. 8 do
            Assert.That(File.ReadAllText path, Is.EqualTo(original),
                        "An independent reader must always see the previous complete file.")
        publish.Set()
        Assert.That(worker.Wait(15000), Is.True)
        Assert.That(worker.Result |> Result.isOk, Is.True)
        Assert.That(File.ReadAllText path, Is.EqualTo(replacement))
        assertNoStages path
    finally
        publish.Set()
        cleanup root

[<Test>]
let selected_checkout_root_rejects_destination_outside_its_boundary () =
    let root = createRoot()
    try
        let different = Path.Combine(root, "different-checkout")
        Directory.CreateDirectory(different) |> ignore
        let path = Path.Combine(projectPath root, "outside.txt")
        File.WriteAllText(path, "untouched")
        let options =
            { SourceRoot = FolderPath different
              CreateParents = false
              DryRun = false }
        match AtomicTextFiles.edit (FilePath path) options (fun _ -> Ok(ReplaceText "no", ())) with
        | Error(Client.PermissionDenied _) -> ()
        | other -> Assert.Fail($"Expected selected-root protection, got {other}")
        Assert.That(File.ReadAllText path, Is.EqualTo("untouched"))
    finally cleanup root

[<Test>]
let independent_dotnet_processes_honor_same_expected_hash () =
    let root = createRoot()
    let children = ResizeArray<Process>()
    try
        let project = projectPath root
        let path = Path.Combine(project, "shared.txt")
        File.WriteAllText(path, "original")
        let assembly = typeof<Client.Runtime>.Assembly.Location
        let escaped (text: string) = text.Replace("\"", "\"\"")
        let script = Path.Combine(root, "concurrent.fsx")
        let program = $"""
#I @"{escaped (Path.GetDirectoryName assembly)}"
#r "Common.dll"
#r "JarvisClient.dll"
open System
open System.IO
open System.Threading
open Client
open Client.IO
let root = @"{escaped project}"
let target = Path.Combine(root, "shared.txt")
let label = fsi.CommandLineArgs[1]
File.WriteAllText(Path.Combine(root, "ready-" + label), "ready")
let waitFor name =
    let signal = Path.Combine(root, name)
    let until = DateTime.UtcNow.AddSeconds(20.)
    while not (File.Exists(signal)) && DateTime.UtcNow < until do Thread.Sleep(15)
    if not (File.Exists(signal)) then failwith ("Barrier timeout: " + name)
waitFor ("go-" + label)
File.WriteAllText(Path.Combine(root, "attempt-" + label), "attempting")
let options : TextEditOptions = {{ SourceRoot = FolderPath root; CreateParents = false; DryRun = false }}
match AtomicTextFiles.edit (FilePath target) options (fun before ->
    File.WriteAllText(Path.Combine(root, "entered-" + label), "entered")
    if label = "a" then waitFor "release-a"
    match before with
    | Some "original" -> Ok(ReplaceText ("new-" + label), ())
    | _ -> Error(ValidationError "Stale input")) with
| Ok () -> printfn "committed"; exit 0
| Error(ValidationError "Stale input") -> printfn "rejected"; exit 3
| Error other -> failwith ("Unexpected edit failure: " + string other)
"""
        File.WriteAllText(script, program)
        let start label =
            let startInfo = ProcessStartInfo("dotnet")
            startInfo.ArgumentList.Add("fsi")
            startInfo.ArgumentList.Add("--exec")
            startInfo.ArgumentList.Add(script)
            startInfo.ArgumentList.Add(label)
            startInfo.UseShellExecute <- false
            startInfo.RedirectStandardOutput <- true
            startInfo.RedirectStandardError <- true
            let child = Process.Start(startInfo)
            if isNull child then failwith "Cannot start dotnet fsi child."
            children.Add(child)
            child
        let waitMarker name =
            let marker = Path.Combine(project, name)
            let deadline = DateTime.UtcNow.AddSeconds(25.)
            while not (File.Exists marker) && DateTime.UtcNow < deadline do
                Thread.Sleep(20)
            Assert.That(File.Exists marker, Is.True, $"Child did not reach {name}.")
        let a = start "a"
        waitMarker "ready-a"
        File.WriteAllText(Path.Combine(project, "go-a"), "go")
        waitMarker "entered-a" // A now holds the exclusive cross-process lock.
        let b = start "b"
        waitMarker "ready-b"
        File.WriteAllText(Path.Combine(project, "go-b"), "go")
        waitMarker "attempt-b"
        Thread.Sleep 300
        Assert.That(File.Exists(Path.Combine(project, "entered-b")), Is.False,
                    "Second process must not enter the edit plan while the first holds its lock.")
        File.WriteAllText(Path.Combine(project, "release-a"), "release")
        Assert.That(a.WaitForExit(30000), Is.True, "First client did not finish.")
        Assert.That(b.WaitForExit(30000), Is.True, "Second client did not finish.")
        let exits = [ a.ExitCode; b.ExitCode ] |> List.sort
        Assert.That((exits = [ 0; 3 ]), Is.True, $"A: {a.StandardError.ReadToEnd()} B: {b.StandardError.ReadToEnd()}")
        Assert.That(File.ReadAllText(path), Is.AnyOf("new-a", "new-b"))
        assertNoStages path
    finally
        for proc in children do
            try
                if not proc.HasExited then proc.Kill(true)
                proc.WaitForExit(2000) |> ignore
                proc.Dispose()
            with _ -> ()
        cleanup root
