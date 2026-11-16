namespace global

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Security.AccessControl
open System.Security.Principal
open Jarvis.Testing
open NUnit.Framework
open Client

// Read the real profile from the process token (userenv), not USERPROFILE.
// Shell known folders may reflect Client.Tests' synthetic environment and
// therefore cannot independently establish the operator profile boundary.
module private WindowsKnownFolderNative =
    [<DllImport("kernel32.dll", ExactSpelling = true)>]
    extern nativeint GetCurrentProcess()
    [<DllImport("kernel32.dll", ExactSpelling = true)>]
    extern bool CloseHandle(nativeint handle)
    [<DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)>]
    extern bool OpenProcessToken(nativeint processHandle, uint32 desiredAccess, nativeint& token)
    [<DllImport("userenv.dll", EntryPoint = "GetUserProfileDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)>]
    extern bool GetUserProfileDirectory(nativeint token, System.Text.StringBuilder buffer, uint32& length)
    [<DllImport("shell32.dll", ExactSpelling = true)>]
    extern int SHGetKnownFolderPath(Guid& folderId, uint32 flags, nativeint token, nativeint& path)

/// Global NUnit fixture: neither wrapped nor direct Client.Tests runs may use
/// the operator's config, activity, ledger, temporary paths, or Git identity.
/// Windows requires an externally provisioned disposable root; macOS is not verified.
[<SetUpFixture>]
type HermeticClientTestEnvironment() =
    let mutable ownedRoot : string option = None
    let mutable knownFolderOverride : IDisposable option = None
    let mutable processHelperOverride : IDisposable option = None
    let mutable originalEnvironment : (string * string) list = []

    static member Under (root: string) (path: string) =
        if String.IsNullOrWhiteSpace(root) || String.IsNullOrWhiteSpace(path) then false
        else
            let comparison =
                if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase
                else StringComparison.Ordinal
            let prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + string Path.DirectorySeparatorChar
            Path.GetFullPath(path).StartsWith(prefix, comparison)

    /// The testhost independently checks protected paths; caller-provided
    /// HOME, USERPROFILE, APPDATA and LOCALAPPDATA do not attest provenance.
    /// Pure validation permits synthetic-profile tests with no operator I/O.
    static member AssertOutsideProtectedFolders (root: string) (protectedFolders: string list) =
        if String.IsNullOrWhiteSpace root || not (Path.IsPathFullyQualified root) then
            raise (UnauthorizedAccessException("Windows Client.Tests root must be absolute."))
        let full = Path.TrimEndingDirectorySeparator(Path.GetFullPath root)
        let prefix = "jarvis-client-tests-"
        let leaf = Path.GetFileName full
        if not (leaf.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) then
            raise (UnauthorizedAccessException("Windows Client.Tests root must be a named disposable child."))
        match Guid.TryParseExact(leaf.Substring(prefix.Length), "N") with
        | true, _ -> ()
        | _ -> raise (UnauthorizedAccessException("Windows Client.Tests root requires a unique disposable ID."))
        let sameOrUnder (ancestor: string) (candidate: string) =
            let ancestor = Path.TrimEndingDirectorySeparator(Path.GetFullPath ancestor)
            let candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath candidate)
            String.Equals(ancestor, candidate, StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith(ancestor + string Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        if List.isEmpty protectedFolders then
            raise (UnauthorizedAccessException("Windows Client.Tests could not attest OS profile folders."))
        for protectedFolder in protectedFolders do
            if String.IsNullOrWhiteSpace protectedFolder ||
               not (Path.IsPathFullyQualified protectedFolder) ||
               sameOrUnder protectedFolder full then
                raise (UnauthorizedAccessException("Refusing Windows Client.Tests root overlapping an actual OS profile folder."))

    /// USERPROFILE and shell known-folders can be redirected by our test
    /// process. userenv resolves the *token's* actual Windows profile path,
    /// independent of that synthetic environment, without touching files.
    static member private TokenProfileDirectory () =
        let mutable token = IntPtr.Zero
        if not (WindowsKnownFolderNative.OpenProcessToken(
                    WindowsKnownFolderNative.GetCurrentProcess(), 0x8u, &token)) then
            raise (UnauthorizedAccessException("Windows Client.Tests cannot query the OS user token."))
        try
            let mutable capacity = 0u
            WindowsKnownFolderNative.GetUserProfileDirectory(token, null, &capacity) |> ignore
            if capacity = 0u || capacity > 32768u then
                raise (UnauthorizedAccessException("Windows Client.Tests cannot size actual token profile path."))
            let buffer = System.Text.StringBuilder(int capacity)
            if not (WindowsKnownFolderNative.GetUserProfileDirectory(token, buffer, &capacity)) then
                raise (UnauthorizedAccessException("Windows Client.Tests cannot resolve actual token profile path."))
            let profile = buffer.ToString()
            if String.IsNullOrWhiteSpace profile then
                raise (UnauthorizedAccessException("Windows Client.Tests resolved empty token profile path."))
            profile
        finally
            WindowsKnownFolderNative.CloseHandle(token) |> ignore

    /// Run before installing Client overrides or any Client persistence.
    static member AssertWindowsDisposableRoot (root: string) =
        let parentValue = Environment.GetEnvironmentVariable("JARVIS_CLIENT_TEST_DISPOSABLE_PARENT")
        if String.IsNullOrWhiteSpace parentValue || not (Path.IsPathFullyQualified parentValue) then
            raise (UnauthorizedAccessException("Windows Client.Tests requires an explicitly attested disposable parent."))
        let parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath parentValue)
        let rootParent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath root))
        if not (String.Equals(parent, rootParent, StringComparison.OrdinalIgnoreCase)) then
            raise (UnauthorizedAccessException("Windows Client.Tests root is not a direct child of its disposable parent."))
        let nativeFolder (textId: string) =
            let mutable folderId = Guid(textId)
            let mutable pointer = IntPtr.Zero
            // KF_FLAG_DONT_VERIFY: inspect identity without creating folders.
            let result = WindowsKnownFolderNative.SHGetKnownFolderPath(&folderId, 0x4000u, IntPtr.Zero, &pointer)
            if result <> 0 || pointer = IntPtr.Zero then
                raise (UnauthorizedAccessException("Windows Client.Tests could not resolve actual OS profile folders."))
            try
                let path = Marshal.PtrToStringUni(pointer)
                if String.IsNullOrWhiteSpace path then
                    raise (UnauthorizedAccessException("Windows Client.Tests obtained an empty OS profile folder."))
                path
            finally Marshal.FreeCoTaskMem(pointer)
        let tokenProfile = HermeticClientTestEnvironment.TokenProfileDirectory()
        // The disposable root may contain synthetic shell known folders, but
        // may never contain the real user token's profile (cleanup hazard).
        if HermeticClientTestEnvironment.Under root tokenProfile then
            raise (UnauthorizedAccessException("Refusing Windows Client.Tests root containing actual OS user profile."))
        let actualFolders =
            [ tokenProfile // Actual user token, not forged env
              nativeFolder "5E6C858F-0E22-4760-9AFE-EA3317B67173" // Shell Profile (may be synthetic)
              nativeFolder "3EB685DB-65F9-4CF6-A03A-E3EF65729F3D" // RoamingAppData
              nativeFolder "F1B32785-6FBA-4FCF-9D55-7B8E7F157091" ] // LocalAppData
        HermeticClientTestEnvironment.AssertOutsideProtectedFolders root actualFolders
        // Independently repeat the parent process's token/physical-identity
        // check; environmental mappings cannot authorize testhost writes.
        WindowsDisposableGuard.attestScratchParentTokenOnly parent |> ignore
        HermeticClientTestEnvironment.AssertPrivatePath parent root

    /// Lexical containment is insufficient: reject any symlink/junction/reparse
    /// component from the sandbox root through the requested path. Never follow
    /// a linked ancestor before rejecting it.
    static member AssertPrivatePath (root: string) (path: string) =
        if not (HermeticClientTestEnvironment.Under root path) then
            raise (UnauthorizedAccessException("Refusing a path outside the hermetic Client.Tests sandbox."))
        let canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))
        let rejectLink candidate =
            let file = FileInfo(candidate)
            let directory = DirectoryInfo(candidate)
            if not (isNull file.LinkTarget) ||
               not (isNull directory.LinkTarget) ||
               ((File.Exists(candidate) || Directory.Exists(candidate)) &&
                File.GetAttributes(candidate).HasFlag(FileAttributes.ReparsePoint)) then
                raise (UnauthorizedAccessException("Refusing a linked/reparse-point Client.Tests sandbox path."))
        if not (Directory.Exists canonicalRoot) then
            raise (UnauthorizedAccessException("Hermetic Client.Tests sandbox root is missing."))
        // Reject a linked ancestor of the root as well as linked descendants.
        // A Windows junction above the root is otherwise invisible to a
        // lexical Path.GetFullPath check.
        let mutable ancestor = canonicalRoot
        let mutable checking = true
        while checking do
            rejectLink ancestor
            let parent = Path.GetDirectoryName ancestor
            if String.IsNullOrEmpty parent || parent = ancestor then
                checking <- false
            else ancestor <- parent
        let relative = Path.GetRelativePath(canonicalRoot, Path.GetFullPath(path))
        let segments = relative.Split([| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |],
                                      StringSplitOptions.RemoveEmptyEntries)
        let mutable candidate = canonicalRoot
        for segment in segments do
            candidate <- Path.Combine(candidate, segment)
            rejectLink candidate

    /// Linux owner-only 0700 directories and 0600 files are required for
    /// externally managed scratch roots as well as direct NUnit-owned roots.
    /// With no group/other access, fixture paths cannot be shared accidentally.
    static member AssertOwnerOnly (path: string) (isDirectory: bool) =
        let expected =
            if isDirectory then
                UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
            else
                UnixFileMode.UserRead ||| UnixFileMode.UserWrite
        if OperatingSystem.IsWindows() then
            let root = Environment.GetEnvironmentVariable("JARVIS_CLIENT_TEST_HERMETIC_ROOT")
            let rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath root)
            let currentPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath path)
            WindowsDisposableGuard.assertOwnerOnly path isDirectory
                (String.Equals(rootPath, currentPath, StringComparison.OrdinalIgnoreCase))
        elif File.GetUnixFileMode(path) <> expected then
            raise (UnauthorizedAccessException("Hermetic Client.Tests requires owner-only scratch permissions."))

    /// Windows Git makes loose objects read-only. Dispose only verified test
    /// descendants, clearing ReadOnly on regular files; never follow junctions
    /// or symlinks while cleaning up. This is test-only, not production GC.
    static member DeletePrivateTree (root: string) (path: string) =
        HermeticClientTestEnvironment.AssertPrivatePath root path
        let rec remove (candidate: string) =
            let attributes = File.GetAttributes candidate
            if attributes.HasFlag(FileAttributes.ReparsePoint) then
                if attributes.HasFlag(FileAttributes.Directory) then Directory.Delete(candidate)
                else File.Delete(candidate)
            elif attributes.HasFlag(FileAttributes.Directory) then
                for child in Directory.EnumerateFileSystemEntries(candidate) |> Seq.toArray do
                    remove child
                if attributes.HasFlag(FileAttributes.ReadOnly) then
                    File.SetAttributes(candidate, attributes &&& ~~~FileAttributes.ReadOnly)
                Directory.Delete(candidate)
            else
                if attributes.HasFlag(FileAttributes.ReadOnly) then
                    File.SetAttributes(candidate, attributes &&& ~~~FileAttributes.ReadOnly)
                File.Delete(candidate)
        if Directory.Exists path then remove path

    /// Shared cleanup for Git fixtures. Git for Windows marks loose objects
    /// read-only; Directory.Delete(path, true) is not portable. Every target
    /// must be an already verified descendant of this testhost's scratch.
    static member DeleteTestTree (path: string) =
        let scratch = Environment.GetEnvironmentVariable("JARVIS_CLIENT_TEST_HERMETIC_ROOT")
        if String.IsNullOrWhiteSpace scratch then
            raise (UnauthorizedAccessException("No hermetic root for test fixture cleanup."))
        HermeticClientTestEnvironment.DeletePrivateTree scratch path

    static member private Layout (root: string) =
        [ "HOME", "home"; "USERPROFILE", "home"
          "XDG_CONFIG_HOME", "config"; "XDG_DATA_HOME", "data"
          "XDG_STATE_HOME", "state"; "XDG_CACHE_HOME", "cache"
          "XDG_RUNTIME_DIR", "runtime"; "DOTNET_CLI_HOME", "dotnet-cli"
          "TMPDIR", "tmp"; "TEMP", "tmp"; "TMP", "tmp"
          "APPDATA", "config"; "LOCALAPPDATA", "data" ]
        |> List.map (fun (key, name) -> key, Path.Combine(root, name))

    static member private GitIdentity =
        [ "GIT_CONFIG_GLOBAL"; "GIT_CONFIG_NOSYSTEM"
          "GIT_AUTHOR_NAME"; "GIT_AUTHOR_EMAIL"
          "GIT_COMMITTER_NAME"; "GIT_COMMITTER_EMAIL" ]

    member private _.Restore() =
        processHelperOverride |> Option.iter (fun scope -> scope.Dispose())
        processHelperOverride <- None
        knownFolderOverride |> Option.iter (fun scope -> scope.Dispose())
        knownFolderOverride <- None
        for key, value in originalEnvironment do
            Environment.SetEnvironmentVariable(key, value)
        originalEnvironment <- []
        ownedRoot |> Option.iter (fun path -> Directory.Delete(path, true))
        ownedRoot <- None

    [<OneTimeSetUp>]
    member this.BeforeAll() =
        if OperatingSystem.IsMacOS() then
            failwith "Hermetic Client.Tests macOS known-folder isolation has not been verified."
        let externallyManaged = Environment.GetEnvironmentVariable("JARVIS_CLIENT_TEST_HERMETIC_ROOT")
        // Direct Windows testhost execution would allocate inside the real
        // profile's TEMP. Require the runner to provision the scratch tree
        // before the NUnit process starts.
        if OperatingSystem.IsWindows() && String.IsNullOrWhiteSpace externallyManaged then
            failwith "Windows Client.Tests must run through the disposable hermetic harness."
        let root =
            if String.IsNullOrWhiteSpace externallyManaged then
                let created = Directory.CreateTempSubdirectory("jarvis-client-tests-direct-").FullName
                ownedRoot <- Some created
                created
            else Path.GetFullPath(externallyManaged)
        try
            if OperatingSystem.IsWindows() then
                HermeticClientTestEnvironment.AssertWindowsDisposableRoot root
            let layout = HermeticClientTestEnvironment.Layout root
            let gitConfig = Path.Combine(root, "gitconfig")
            HermeticClientTestEnvironment.AssertPrivatePath root gitConfig
            if ownedRoot.IsSome then
                File.SetUnixFileMode(root,
                    UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
            HermeticClientTestEnvironment.AssertOwnerOnly root true
            let gitVariables =
                Environment.GetEnvironmentVariables().Keys
                |> Seq.cast<string>
                |> Seq.filter (fun key -> key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase))
                |> Seq.toList
            let tracked =
                (layout |> List.map fst) @
                [ "JARVIS_CLIENT_TEST_HERMETIC_ROOT"
                  "JARVIS_CLIENT_TEST_DISPOSABLE_PARENT" ] @
                HermeticClientTestEnvironment.GitIdentity @ gitVariables
                |> List.distinct
            originalEnvironment <-
                tracked |> List.map (fun key -> key, Environment.GetEnvironmentVariable key)

            if ownedRoot.IsSome then
                for _, location in layout do
                    HermeticClientTestEnvironment.AssertPrivatePath root location
                    Directory.CreateDirectory(location) |> ignore
                    File.SetUnixFileMode(location,
                        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
                File.WriteAllText(gitConfig,
                    "[user]\n\tname = Jarvis Test\n\temail = jarvis-test@example.invalid\n")
                File.SetUnixFileMode(gitConfig, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
                for key, location in layout do
                    Environment.SetEnvironmentVariable(key, location)
                Environment.SetEnvironmentVariable("JARVIS_CLIENT_TEST_HERMETIC_ROOT", root)

            // Externally managed roots must supply exactly the expected layout.
            // Checking every directory before config/activity access rejects
            // config/data links even when their lexical prefix is private.
            let comparison =
                if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase
                else StringComparison.Ordinal
            for key, expected in layout do
                let actual = Environment.GetEnvironmentVariable key
                if String.IsNullOrWhiteSpace actual ||
                   not (String.Equals(Path.GetFullPath(actual), Path.GetFullPath(expected), comparison)) ||
                   not (Directory.Exists actual) then
                    raise (UnauthorizedAccessException($"Refusing non-hermetic Client.Tests mapping: {key}."))
                HermeticClientTestEnvironment.AssertPrivatePath root actual
                HermeticClientTestEnvironment.AssertOwnerOnly actual true

            if not (File.Exists gitConfig) then
                raise (UnauthorizedAccessException("Hermetic Git config is missing."))
            HermeticClientTestEnvironment.AssertPrivatePath root gitConfig
            HermeticClientTestEnvironment.AssertOwnerOnly gitConfig false
            // Only after containment and layout checks can the test assembly
            // replace Client-owned Windows known-folder resolution. The normal
            // executable has no environment-controlled path override.
            knownFolderOverride <- Some (ClientKnownFolders.installForTests root)
            if OperatingSystem.IsWindows() then
                // VSTest's testhost cannot handle Jarvis's internal process
                // group command. Use the .NET 11 Release Client apphost built
                // in this same ephemeral checkout, never an operator binary.
                let repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."))
                let apphost = Path.Combine(repository, "Client", "bin", "Release", "net11.0", "JarvisClient.exe")
                processHelperOverride <- Some (BoundedProcess.installTestHelperHost apphost)

            // Do not inherit even higher-precedence per-command Git identity,
            // configuration injection or repository/index overrides.
            for key in gitVariables do
                Environment.SetEnvironmentVariable(key, null)
            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", gitConfig)
            Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1")
            Environment.SetEnvironmentVariable("GIT_AUTHOR_NAME", "Jarvis Test")
            Environment.SetEnvironmentVariable("GIT_AUTHOR_EMAIL", "jarvis-test@example.invalid")
            Environment.SetEnvironmentVariable("GIT_COMMITTER_NAME", "Jarvis Test")
            Environment.SetEnvironmentVariable("GIT_COMMITTER_EMAIL", "jarvis-test@example.invalid")

            let paths =
                [ "ClientConfig", ClientConfig.configPath()
                  "ActivityLog", ActivityLog.defaultSettings().DirectoryPath
                  "Client ApplicationData", ClientKnownFolders.applicationData()
                  "Client LocalApplicationData", ClientKnownFolders.localApplicationData()
                  "Client UserProfile", ClientKnownFolders.userProfile()
                  "Ledger binding", OrchestrationApi.LedgerBindingConfig.pathForRoot root
                  "test temporary directory", Path.GetTempPath()
                  "Git global config", Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL") ]
                @ (if OperatingSystem.IsLinux() then
                       [ "OS ApplicationData", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                         "OS LocalApplicationData", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) ]
                   else [])
            for description, path in paths do
                try HermeticClientTestEnvironment.AssertPrivatePath root path
                with :? UnauthorizedAccessException as ex ->
                    raise (UnauthorizedAccessException($"Refusing non-hermetic Client.Tests: {description}.", ex))
        with _ ->
            this.Restore()
            reraise()

    [<OneTimeTearDown>]
    member this.AfterAll() = this.Restore()

module HermeticHarnessTests =
    [<Test>]
    let failed_windows_root_acquisition_removes_created_root_before_testhost () =
        if not (OperatingSystem.IsWindows()) then
            Assert.Ignore("Native Windows scratch acquisition requires Windows.")
        let disposableParent = Environment.GetEnvironmentVariable("JARVIS_CLIENT_TEST_DISPOSABLE_PARENT")
        Assert.That(Directory.Exists(disposableParent), Is.True)
        // Invoke only the setup-failure path: this cannot spawn a nested VSTest.
        let repository =
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."))
        let start = ProcessStartInfo("dotnet")
        start.UseShellExecute <- false
        start.RedirectStandardOutput <- true
        start.RedirectStandardError <- true
        start.WorkingDirectory <- repository
        for arg in
            [ "fsi"; "scripts/test-client-hermetic.fsx"; "--"
              "--scratch-parent"; disposableParent; "--self-test-after-root-creation" ] do
            start.ArgumentList.Add(arg)
        use child = new Process(StartInfo = start)
        Assert.That(child.Start(), Is.True)
        let stdout = child.StandardOutput.ReadToEndAsync()
        let stderr = child.StandardError.ReadToEndAsync()
        if not (child.WaitForExit(60000)) then
            child.Kill(true)
            child.WaitForExit()
            Assert.Fail("Root-creation failure injection did not exit in 60 seconds.")
        let output = stdout.Result
        let errors = stderr.Result
        Assert.That(child.ExitCode, Is.EqualTo(1), errors)
        let prefix = "Hermetic Client.Tests: failed root acquisition "
        let rootLine =
            output.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.tryFind (fun line -> line.StartsWith(prefix, StringComparison.Ordinal))
        Assert.That(rootLine.IsSome, Is.True, output + errors)
        let allocated = rootLine.Value.Substring(prefix.Length)
        Assert.That(Path.GetFileName(allocated), Does.StartWith("jarvis-client-tests-"))
        Assert.That(HermeticClientTestEnvironment.Under disposableParent allocated, Is.True)
        Assert.That(Directory.Exists(allocated), Is.False)
        Assert.That(output, Does.Contain("scratch cleanup verified = true"))
        Assert.That(output, Does.Not.Contain("child executable"))

    [<Test>]
    let forged_disposable_root_inside_synthetic_profile_is_rejected_before_any_writes () =
        // No operator profile is opened: every synthetic path belongs to this
        // invocation's already isolated test sandbox.
        let scratch = Environment.GetEnvironmentVariable("JARVIS_CLIENT_TEST_HERMETIC_ROOT")
        let fakeProfile = Path.Combine(scratch, "synthetic-os-profile")
        let forged = Path.Combine(fakeProfile, "jarvis-client-tests-" + Guid.NewGuid().ToString("N"))
        let fakeAppData = Path.Combine(fakeProfile, "AppData", "Roaming")
        let fakeLocalAppData = Path.Combine(fakeProfile, "AppData", "Local")
        // The forged mappings are fully self-consistent and would appear safe
        // if the testhost trusted HOME/USERPROFILE/APPDATA instead of the
        // separate OS profile provenance. Do not set them on this process:
        // VSTest can create mapped directories before NUnit setup runs.
        for mapped in [ "home"; "config"; "data" ] do
            Assert.That(HermeticClientTestEnvironment.Under forged (Path.Combine(forged, mapped)), Is.True)
        Assert.Throws<UnauthorizedAccessException>(
            TestDelegate(fun () ->
                HermeticClientTestEnvironment.AssertOutsideProtectedFolders
                    forged [ fakeProfile; fakeAppData; fakeLocalAppData ]))
        |> ignore
        Assert.That(Directory.Exists forged, Is.False, "Provenance rejection must precede test writes.")

    [<Test>]
    let preallocation_parent_gate_rejects_fake_native_profile_without_any_writes () =
        // This pure gate is used by the parent FSI process before creation.
        // Use synthetic paths only; never probe the actual operator profile.
        let scratch = Environment.GetEnvironmentVariable("JARVIS_CLIENT_TEST_HERMETIC_ROOT")
        let syntheticNativeProfile = Path.Combine(scratch, "synthetic-native-profile")
        let forgedParent = Path.Combine(syntheticNativeProfile, "AppData", "Local")
        let wouldCreate = Path.Combine(forgedParent, "jarvis-client-tests-" + Guid.NewGuid().ToString("N"))
        Assert.Throws<UnauthorizedAccessException>(
            TestDelegate(fun () ->
                WindowsDisposableGuard.assertOutsideProtectedFolders forgedParent
                    [syntheticNativeProfile]))
        |> ignore
        Assert.That(Directory.Exists wouldCreate, Is.False,
                    "Native profile rejection must precede even the first scratch write.")

    [<Test>]
    let windows_private_acl_rejects_broad_inherited_or_explicit_grants () =
        if not (OperatingSystem.IsWindows()) then
            Assert.Ignore("Native NTFS ACL validation requires Windows.")
        let root = Environment.GetEnvironmentVariable("JARVIS_CLIENT_TEST_HERMETIC_ROOT")
        WindowsDisposableGuard.assertOwnerOnly root true true
        let isolated = Path.Combine(root, "acl-adversarial-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(isolated) |> ignore
        try
            WindowsDisposableGuard.assertOwnerOnly isolated true false
            let info = DirectoryInfo(isolated)
            let security = FileSystemAclExtensions.GetAccessControl(info)
            // Preserve the inherited private owner grant. Disabling inheritance
            // here would leave an empty DACL when the injected grant is purged.
            Assert.That(security.AreAccessRulesProtected, Is.False)
            let everyone = SecurityIdentifier(WellKnownSidType.WorldSid, null)
            security.AddAccessRule(
                FileSystemAccessRule(everyone, FileSystemRights.ReadAndExecute,
                                     AccessControlType.Allow))
            FileSystemAclExtensions.SetAccessControl(info, security)
            Assert.Throws<UnauthorizedAccessException>(
                TestDelegate(fun () ->
                    WindowsDisposableGuard.assertOwnerOnly isolated true false))
            |> ignore
        finally
            // This child remains beneath a proven private parent; restore its
            // safe inherited DACL before invoking normal bounded teardown.
            let info = DirectoryInfo(isolated)
            let security = FileSystemAclExtensions.GetAccessControl(info)
            // Modify only DACL access, not the owner. Replacing a descriptor
            // and setting its owner requires privileges a disposable user
            // must not need, and caused an UnauthorizedAccessException.
            security.PurgeAccessRules(SecurityIdentifier(WellKnownSidType.WorldSid, null))
            FileSystemAclExtensions.SetAccessControl(info, security)
            // Child inherits the protected root's owner-only grant; only the
            // top-level scratch root itself must be ACL inheritance-protected.
            WindowsDisposableGuard.assertOwnerOnly isolated true false
            Directory.Delete(isolated)

    [<Test>]
    let synthetic_home_under_disposable_root_does_not_masquerade_as_real_profile () =
        let scratch = Environment.GetEnvironmentVariable("JARVIS_CLIENT_TEST_HERMETIC_ROOT")
        let root = Path.Combine(scratch, "jarvis-client-tests-" + Guid.NewGuid().ToString("N"))
        let otherProfile = Path.Combine(scratch, "distinct-synthetic-os-profile")
        let mappedHome = Path.Combine(root, "home")
        HermeticClientTestEnvironment.AssertOutsideProtectedFolders
            root [otherProfile; mappedHome]
        Assert.That(Directory.Exists root, Is.False)

    [<Test>]
    let read_only_git_object_is_cleaned_inside_disposable_fixture () =
        let scratch = Environment.GetEnvironmentVariable("JARVIS_CLIENT_TEST_HERMETIC_ROOT")
        let fixture = Path.Combine(scratch, "git-cleanup-" + Guid.NewGuid().ToString("N"))
        let objects = Path.Combine(fixture, ".git", "objects", "ab")
        Directory.CreateDirectory(objects) |> ignore
        let objectFile = Path.Combine(objects, "abcdef")
        File.WriteAllText(objectFile, "test")
        File.SetAttributes(objectFile, File.GetAttributes(objectFile) ||| FileAttributes.ReadOnly)
        HermeticClientTestEnvironment.DeleteTestTree fixture
        Assert.That(Directory.Exists fixture, Is.False)

    [<Test>]
    let client_test_config_is_disposable_and_not_the_live_jarvis_config () =
        let root = Environment.GetEnvironmentVariable("JARVIS_CLIENT_TEST_HERMETIC_ROOT")
        Assert.That(root, Is.Not.Null.And.Not.Empty)
        let config = ClientConfig.configPath()
        HermeticClientTestEnvironment.AssertPrivatePath root config
        Assert.That(config, Does.EndWith(Path.Combine("Jarvis", "config.json")))
        let git = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL")
        HermeticClientTestEnvironment.AssertPrivatePath root git
        Assert.That(File.ReadAllText(git), Does.Contain("jarvis-test@example.invalid"))

    [<Test>]
    let client_storage_and_ledger_are_resolved_inside_disposable_known_folders () =
        let root = Environment.GetEnvironmentVariable("JARVIS_CLIENT_TEST_HERMETIC_ROOT")
        let paths =
            [ ClientConfig.configPath()
              ActivityLog.defaultSettings().DirectoryPath
              OrchestrationApi.LedgerBindingConfig.pathForRoot root
              ClientKnownFolders.applicationData()
              ClientKnownFolders.localApplicationData()
              ClientKnownFolders.userProfile()
              Path.GetTempPath() ]
        for path in paths do HermeticClientTestEnvironment.AssertPrivatePath root path
        Assert.That(ClientKnownFolders.applicationData(), Is.EqualTo(Path.Combine(root, "config")))
        Assert.That(ClientKnownFolders.localApplicationData(), Is.EqualTo(Path.Combine(root, "data")))
        Assert.That(ClientKnownFolders.userProfile(), Is.EqualTo(Path.Combine(root, "home")))
        // Windows may honor or ignore APPDATA/LOCALAPPDATA. Neither outcome
        // matters: Client-owned storage must always resolve inside this root.
        let deviceId = DeviceIdentity.getOrCreate()
        Assert.That(Guid.TryParse deviceId |> fst, Is.True)
        let devicePath = Path.Combine(root, "config", "Jarvis", "device-id")
        HermeticClientTestEnvironment.AssertPrivatePath root devicePath
        Assert.That(File.Exists devicePath, Is.True)

    [<Test>]
    let bounded_git_process_uses_scoped_client_helper_in_testhost () =
        // On Windows VSTest owns Environment.ProcessPath. A bounded Git child
        // must nevertheless use the built Jarvis apphost as group supervisor.
        // This regression fails without the scoped test helper override.
        let root = Environment.GetEnvironmentVariable("JARVIS_CLIENT_TEST_HERMETIC_ROOT")
        HermeticClientTestEnvironment.AssertPrivatePath root (Path.Combine(root, "config"))
        let result =
            BoundedProcess.run
                { Executable = "git"
                  Args = [ "--version" ]
                  WorkingDirectory = root
                  Timeout = TimeSpan.FromSeconds 15.
                  MaxOutputBytes = Some 1024
                  StandardInput = BoundedProcess.ClosedInput
                  ExtraEnvironment = []
                  ConfigureStart = Some ProcessEnvironment.apply }
        match result with
        | Ok outcome ->
            Assert.That(outcome.ExitCode, Is.EqualTo(0), outcome.StdErr)
            Assert.That(outcome.StdOut, Does.Contain("git version"))
        | Error error -> Assert.Fail($"Bounded disposable git command failed: {error}")

    let private createLinkedDirectory (link: string) (outside: string) =
        if OperatingSystem.IsWindows() then
            // Junction creation is available to unprivileged Windows users.
            let start = ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{link}\" \"{outside}\"")
            start.UseShellExecute <- false
            start.RedirectStandardOutput <- true
            start.RedirectStandardError <- true
            use child = Process.Start(start)
            let output = child.StandardOutput.ReadToEnd()
            let error = child.StandardError.ReadToEnd()
            if not (child.WaitForExit(10000)) then
                child.Kill(true)
                failwith "Disposable Windows junction creation timed out."
            Assert.That(child.ExitCode, Is.EqualTo(0), output + error)
        else
            Directory.CreateSymbolicLink(link, outside) |> ignore

    [<Test>]
    let windows_parent_attestation_rejects_junction_alias_before_scratch_creation () =
        if not (OperatingSystem.IsWindows()) then
            Assert.Ignore("Native Windows junction preflight requires Windows.")
        let root = Environment.GetEnvironmentVariable("JARVIS_CLIENT_TEST_HERMETIC_ROOT")
        let target = Path.Combine(root, "target-parent-" + Guid.NewGuid().ToString("N"))
        let alias = Path.Combine(root, "linked-parent-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(target) |> ignore
        createLinkedDirectory alias target
        let notCreated = Path.Combine(target, "jarvis-client-tests-" + Guid.NewGuid().ToString("N"))
        try
            Assert.Throws<UnauthorizedAccessException>(
                TestDelegate(fun () ->
                    WindowsDisposableGuard.attestScratchParentTokenOnly alias |> ignore))
            |> ignore
            Assert.That(Directory.Exists(notCreated), Is.False,
                        "Junction preflight must not allocate or write behind an alias.")
        finally
            Directory.Delete(alias)
            Directory.Delete(target)

    [<Test>]
    let linked_config_and_activity_ancestors_are_rejected_without_outside_writes () =
        // Both roots are disposable. Never use or inspect a real user directory.
        let root = Directory.CreateTempSubdirectory("jarvis-link-root-").FullName
        let outside = Directory.CreateTempSubdirectory("jarvis-link-outside-").FullName
        let marker = Path.Combine(outside, "untouched.txt")
        File.WriteAllText(marker, "sentinel")
        try
            let normal = Path.Combine(root, "safe", "config.json")
            Directory.CreateDirectory(Path.GetDirectoryName normal) |> ignore
            HermeticClientTestEnvironment.AssertPrivatePath root normal
            let weakPermissions = Path.Combine(root, "weak-permissions")
            Directory.CreateDirectory(weakPermissions) |> ignore
            if not (OperatingSystem.IsWindows()) then
                File.SetUnixFileMode(weakPermissions,
                    UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute ||| UnixFileMode.GroupRead)
                Assert.Throws<UnauthorizedAccessException>(
                    TestDelegate(fun () -> HermeticClientTestEnvironment.AssertOwnerOnly weakPermissions true))
                |> ignore
            for name in [ "config"; "data" ] do
                let link = Path.Combine(root, name)
                createLinkedDirectory link outside
                try
                    let escaped = Path.Combine(link, "Jarvis", "activity", "state.json")
                    Assert.That(HermeticClientTestEnvironment.Under root escaped, Is.True)
                    Assert.Throws<UnauthorizedAccessException>(
                        TestDelegate(fun () -> HermeticClientTestEnvironment.AssertPrivatePath root escaped))
                    |> ignore
                    let nested = Path.Combine(outside, "nested")
                    Directory.CreateDirectory(nested) |> ignore
                    Assert.Throws<UnauthorizedAccessException>(
                        TestDelegate(fun () ->
                            HermeticClientTestEnvironment.AssertPrivatePath
                                (Path.Combine(link, "nested"))
                                (Path.Combine(link, "nested", "config.json"))))
                    |> ignore
                    Assert.That(File.ReadAllText marker, Is.EqualTo("sentinel"))
                    Assert.That(File.Exists(Path.Combine(outside, "Jarvis", "activity", "state.json")), Is.False)
                finally
                    Directory.Delete(link)
        finally
            Directory.Delete(root, true)
            Directory.Delete(outside, true)

    let private gitCommand directory (arguments: string list) =
        let start = ProcessStartInfo("git")
        start.UseShellExecute <- false
        start.RedirectStandardOutput <- true
        start.RedirectStandardError <- true
        start.WorkingDirectory <- directory
        for arg in arguments do start.ArgumentList.Add(arg)
        use gitProcess = Process.Start(start)
        let output = gitProcess.StandardOutput.ReadToEnd()
        let error = gitProcess.StandardError.ReadToEnd()
        if not (gitProcess.WaitForExit(15000)) then
            gitProcess.Kill(true)
            failwith "Hermetic Git identity probe timed out."
        Assert.That(gitProcess.ExitCode, Is.EqualTo(0), error)
        output.Trim()

    [<Test>]
    let git_commit_identity_does_not_inherit_operator_or_sentinel_overrides () =
        for variable in [ "GIT_CONFIG_COUNT"; "GIT_CONFIG_PARAMETERS"
                          "GIT_CONFIG_KEY_0"; "GIT_CONFIG_VALUE_0"; "GIT_DIR"
                          "GIT_WORK_TREE"; "GIT_INDEX_FILE" ] do
            Assert.That(Environment.GetEnvironmentVariable variable, Is.Null, variable)
        Assert.That(Environment.GetEnvironmentVariable("GIT_AUTHOR_NAME"), Is.EqualTo("Jarvis Test"))
        Assert.That(Environment.GetEnvironmentVariable("GIT_AUTHOR_EMAIL"), Is.EqualTo("jarvis-test@example.invalid"))
        Assert.That(Environment.GetEnvironmentVariable("GIT_COMMITTER_NAME"), Is.EqualTo("Jarvis Test"))
        Assert.That(Environment.GetEnvironmentVariable("GIT_COMMITTER_EMAIL"), Is.EqualTo("jarvis-test@example.invalid"))
        let scratch = Directory.CreateTempSubdirectory("jarvis-test-git-identity-").FullName
        try
            gitCommand scratch [ "init"; "-q" ] |> ignore
            gitCommand scratch [ "commit"; "--allow-empty"; "--no-gpg-sign"; "-m"; "isolated identity" ] |> ignore
            let identity = gitCommand scratch [ "log"; "-1"; "--format=%an|%ae|%cn|%ce" ]
            Assert.That(identity,
                Is.EqualTo("Jarvis Test|jarvis-test@example.invalid|Jarvis Test|jarvis-test@example.invalid"))
        finally
            let root = Environment.GetEnvironmentVariable("JARVIS_CLIENT_TEST_HERMETIC_ROOT")
            HermeticClientTestEnvironment.DeletePrivateTree root scratch
