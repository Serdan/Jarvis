namespace global

open System
open System.Diagnostics
open System.IO
open NUnit.Framework
open Client

/// Global NUnit fixture: neither wrapped nor direct Client.Tests runs may use
/// the operator's config, activity, ledger, temporary paths, or Git identity.
/// Windows requires an externally provisioned disposable root; macOS is not verified.
[<SetUpFixture>]
type HermeticClientTestEnvironment() =
    let mutable ownedRoot : string option = None
    let mutable knownFolderOverride : IDisposable option = None
    let mutable originalEnvironment : (string * string) list = []

    static member Under (root: string) (path: string) =
        if String.IsNullOrWhiteSpace(root) || String.IsNullOrWhiteSpace(path) then false
        else
            let comparison =
                if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase
                else StringComparison.Ordinal
            let prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + string Path.DirectorySeparatorChar
            Path.GetFullPath(path).StartsWith(prefix, comparison)

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
        if not (OperatingSystem.IsWindows()) && File.GetUnixFileMode(path) <> expected then
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
                [ "JARVIS_CLIENT_TEST_HERMETIC_ROOT" ] @
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
