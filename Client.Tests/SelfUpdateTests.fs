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

let private cleanReleaseManifest =
    """{
      "schemaVersion": 1,
      "applicationVersion": "0.1.0",
      "gitCommit": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
      "dirty": false,
      "releaseId": "0.1.0-aaaaaaaaaaaa",
      "artifacts": [{
        "rid": "linux-x64",
        "fileName": "JarvisClient-linux-x64",
        "releasePath": "releases/0.1.0-aaaaaaaaaaaa/JarvisClient-linux-x64",
        "sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        "bytes": 123456
      }]
    }"""

[<Test>]
let ``release pointer selects exact platform hash`` () =
    parseReleaseManifest "linux-x64" "JarvisClient-linux-x64" cleanReleaseManifest
    |> shouldEqual (Ok "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")

[<Test>]
let ``release pointer rejects dirty and cross platform artifacts`` () =
    for payload in
        [ cleanReleaseManifest.Replace("\"dirty\": false", "\"dirty\": true")
          cleanReleaseManifest.Replace("releases/0.1.0-aaaaaaaaaaaa/JarvisClient-linux-x64",
                                       "releases/old/JarvisClient-linux-x64")
          cleanReleaseManifest.Replace("\"sha256\": \"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"",
                                       "\"sha256\": \"wrong\"")
          "{bad json" ] do
        match parseReleaseManifest "linux-x64" "JarvisClient-linux-x64" payload with
        | Error(ValidationError _) -> ()
        | other -> Assert.Fail($"Expected fail-closed release validation, got {other}")

    match parseReleaseManifest "win-x64" "JarvisClient-win-x64.exe" cleanReleaseManifest with
    | Error(ValidationError _) -> ()
    | other -> Assert.Fail($"Expected missing-platform rejection, got {other}")

[<Test>]
let ``update channel requires agreement between latest and SHA256SUMS`` () =
    let sums =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb  JarvisClient-linux-x64\n"
    verifyReleaseDownloads "linux-x64" "JarvisClient-linux-x64" sums cleanReleaseManifest
    |> shouldEqual (Ok "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")

    let staleSums = sums.Replace("bbbbbbbb", "cccccccc")
    match verifyReleaseDownloads "linux-x64" "JarvisClient-linux-x64" staleSums cleanReleaseManifest with
    | Error(ValidationError message) -> Assert.That(message, Does.Contain("disagree"))
    | other -> Assert.Fail($"Expected mismatched download manifests to be rejected, got {other}")
