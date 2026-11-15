namespace global

open System
open System.IO
open NUnit.Framework
open Client

/// Global NUnit fixture: even an ordinary "dotnet test" invocation must not
/// exercise the operator's Jarvis identity, activity or ledger bindings.
[<SetUpFixture>]
type HermeticClientTestEnvironment() =
    let mutable ownedRoot : string option = None
    let mutable originalEnvironment : (string * string) list = []

    static member Under (root: string) (path: string) =
        let comparison =
            if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase
            else StringComparison.Ordinal
        let prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + string Path.DirectorySeparatorChar
        Path.GetFullPath(path).StartsWith(prefix, comparison)

    static member private Layout (root: string) =
        [ "HOME", "home"; "USERPROFILE", "home"
          "XDG_CONFIG_HOME", "config"; "XDG_DATA_HOME", "data"
          "XDG_STATE_HOME", "state"; "XDG_CACHE_HOME", "cache"
          "XDG_RUNTIME_DIR", "runtime"; "DOTNET_CLI_HOME", "dotnet-cli"
          "TMPDIR", "tmp"; "TEMP", "tmp"; "TMP", "tmp"
          "APPDATA", "config"; "LOCALAPPDATA", "data" ]
        |> List.map (fun (key, name) -> key, Path.Combine(root, name))

    [<OneTimeSetUp>]
    member _.BeforeAll() =
        let externallyManaged = Environment.GetEnvironmentVariable("JARVIS_CLIENT_TEST_HERMETIC_ROOT")
        let root =
            if String.IsNullOrWhiteSpace externallyManaged then
                let created = Directory.CreateTempSubdirectory("jarvis-client-tests-direct-").FullName
                ownedRoot <- Some created
                created
            else Path.GetFullPath(externallyManaged)

        try
            if not (Directory.Exists root) then
                failwith "Hermetic test root does not exist."
            let layout = HermeticClientTestEnvironment.Layout root
            let gitConfig = Path.Combine(root, "gitconfig")
            if ownedRoot.IsSome then
                File.WriteAllText(gitConfig,
                    "[user]\n\tname = Jarvis Test\n\temail = jarvis-test@example.invalid\n")
                if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
                    File.SetUnixFileMode(gitConfig,
                        UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
            if ownedRoot.IsSome then
                originalEnvironment <-
                    [ for key, _ in layout do
                          key, Environment.GetEnvironmentVariable key ]
                    @ [ "JARVIS_CLIENT_TEST_HERMETIC_ROOT",
                        Environment.GetEnvironmentVariable("JARVIS_CLIENT_TEST_HERMETIC_ROOT")
                        "GIT_CONFIG_GLOBAL", Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL") ]
                for key, location in layout do
                    Directory.CreateDirectory(location) |> ignore
                    if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
                        File.SetUnixFileMode(location,
                            UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
                    Environment.SetEnvironmentVariable(key, location)
                Environment.SetEnvironmentVariable("JARVIS_CLIENT_TEST_HERMETIC_ROOT", root)
                Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", gitConfig)

            // Fail closed instead of silently exercising the actual client
            // configuration or ledger if a special-folder mapping was cached.
            let paths =
                [ "ClientConfig", ClientConfig.configPath()
                  "ApplicationData", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                  "LocalApplicationData", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                  "test temporary directory", Path.GetTempPath()
                  "Git global config", Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL") ]
            for description, path in paths do
                if not (HermeticClientTestEnvironment.Under root path) then
                    failwith $"Refusing non-hermetic Client.Tests: {description} is outside private test root."
        with _ ->
            if ownedRoot.IsSome then
                for key, value in originalEnvironment do
                    Environment.SetEnvironmentVariable(key, value)
                originalEnvironment <- []
                ownedRoot |> Option.iter (fun path -> Directory.Delete(path, true))
                ownedRoot <- None
            reraise()

    [<OneTimeTearDown>]
    member _.AfterAll() =
        if ownedRoot.IsSome then
            for key, value in originalEnvironment do
                Environment.SetEnvironmentVariable(key, value)
            ownedRoot |> Option.iter (fun path -> Directory.Delete(path, true))
            ownedRoot <- None

module HermeticHarnessTests =
    [<Test>]
    let client_test_config_is_disposable_and_not_the_live_jarvis_config () =
        let root = Environment.GetEnvironmentVariable("JARVIS_CLIENT_TEST_HERMETIC_ROOT")
        Assert.That(root, Is.Not.Null.And.Not.Empty)
        let config = ClientConfig.configPath()
        Assert.That(HermeticClientTestEnvironment.Under root config, Is.True)
        Assert.That(config, Does.EndWith(Path.Combine("Jarvis", "config.json")))
        let git = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL")
        Assert.That(HermeticClientTestEnvironment.Under root git, Is.True)
        Assert.That(File.ReadAllText(git), Does.Contain("jarvis-test@example.invalid"))
