module SelfUpdateTests

open System
open System.Diagnostics
open System.IO
open System.Reflection
open Client
open Client.SelfUpdate
open NUnit.Framework
open FsUnitTyped

let private writeExecutable (path: string) (content: string) =
    File.WriteAllText(path, content)
    File.SetUnixFileMode(
        path,
        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

let private startActivationHost livePath stagedPath backupPath markerPath errorPath forwardedArgument =
    let assemblyPath = Assembly.GetExecutingAssembly().Location
    let startInfo = ProcessStartInfo("dotnet")
    startInfo.UseShellExecute <- false
    startInfo.ArgumentList.Add assemblyPath
    startInfo.ArgumentList.Add "--self-update-test-host"
    [ livePath; stagedPath; backupPath; markerPath; errorPath; forwardedArgument ]
    |> List.iter startInfo.ArgumentList.Add
    Process.Start startInfo

[<Test>]
let ``checksum parser selects exact published filename`` () =
    let manifest =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  JarvisClient-linux-x64\n" +
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb  JarvisClient-osx-arm64\n"
    parseChecksum "JarvisClient-linux-x64" manifest
    |> shouldEqual (Ok "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")

[<Test>]
let ``checksum parser rejects missing or malformed entry`` () =
    parseChecksum "JarvisClient-linux-x64" "not-a-hash  JarvisClient-linux-x64\n"
    |> function
       | Error(ValidationError _) -> ()
       | other -> Assert.Fail($"Expected validation error, got {other}")

[<Test>]
[<Platform("Linux,MacOsX")>]
let ``unix activation replaces the process and preserves its pid and arguments`` () =
    let directory = Path.Combine(Path.GetTempPath(), $"jarvis-update-{Guid.NewGuid():N}")
    Directory.CreateDirectory directory |> ignore

    try
        let livePath = Path.Combine(directory, "JarvisClient")
        let stagedPath = Path.Combine(directory, ".JarvisClient.next")
        let backupPath = livePath + ".prev"
        let markerPath = Path.Combine(directory, "activated.txt")
        let errorPath = Path.Combine(directory, "activation-error.txt")
        let forwardedArgument = "argument with spaces"
        writeExecutable livePath "#!/bin/sh\nexit 99\n"
        writeExecutable stagedPath "#!/bin/sh\nprintf '%s\\n%s\\n' \"$$\" \"$2\" > \"$1\"\n"

        use child = startActivationHost livePath stagedPath backupPath markerPath errorPath forwardedArgument
        isNull child |> shouldEqual false
        let originalPid = child.Id
        child.WaitForExit(10_000) |> shouldEqual true
        child.ExitCode |> shouldEqual 0

        let activation = File.ReadAllLines markerPath
        activation |> shouldEqual [| string originalPid; forwardedArgument |]
        File.Exists backupPath |> shouldEqual true
        File.ReadAllText(backupPath) |> shouldEqual "#!/bin/sh\nexit 99\n"
        File.Exists errorPath |> shouldEqual false
    finally
        Directory.Delete(directory, true)

[<Test>]
[<Platform("Linux,MacOsX")>]
let ``unix activation restores the original executable when exec fails`` () =
    let directory = Path.Combine(Path.GetTempPath(), $"jarvis-update-{Guid.NewGuid():N}")
    Directory.CreateDirectory directory |> ignore

    try
        let livePath = Path.Combine(directory, "JarvisClient")
        let stagedPath = Path.Combine(directory, ".JarvisClient.next")
        let backupPath = livePath + ".prev"
        let markerPath = Path.Combine(directory, "should-not-exist.txt")
        let errorPath = Path.Combine(directory, "activation-error.txt")
        let original = "#!/bin/sh\nexit 99\n"
        writeExecutable livePath original
        writeExecutable stagedPath "not an executable image"

        use child = startActivationHost livePath stagedPath backupPath markerPath errorPath "unused"
        child.WaitForExit(10_000) |> shouldEqual true
        child.ExitCode |> shouldEqual 1

        File.ReadAllText(livePath) |> shouldEqual original
        File.Exists backupPath |> shouldEqual false
        File.ReadAllText(errorPath).Contains("execv returned", StringComparison.Ordinal) |> shouldEqual true
    finally
        Directory.Delete(directory, true)
