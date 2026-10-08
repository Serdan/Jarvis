module SelfUpdateTests

open Client
open Client.SelfUpdate
open NUnit.Framework
open FsUnitTyped

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
