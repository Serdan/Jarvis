open System
open System.IO
open System.Text
open System.Security.Cryptography

// Compile the existing frozen disposable Git fixture and its tests unchanged
// except the same two eta expansions as the existing PropertyHarness generator.
// Appending tests in the same file lets them reuse private fixtures.
let here = __SOURCE_DIRECTORY__
let fixture = Path.GetFullPath(Path.Combine(here, "..", "..", "Client.Tests", "NativeSchedulerRosterPolicyTests.fs"))
let bytes = File.ReadAllBytes fixture
let gitBlob = Array.append (Encoding.UTF8.GetBytes(sprintf "blob %d\u0000" bytes.Length)) bytes
let actual = Convert.ToHexString(SHA1.HashData gitBlob).ToLowerInvariant()
let expected = "e6988ac42f4191f807b57f04dcb6ddec160b503f"
if actual <> expected then failwithf "FROZEN_FIXTURE_MISMATCH expected=%s actual=%s" expected actual
let source = Encoding.UTF8.GetString bytes
let original =
    source.Replace("let success = NativeFixture.success", "let success value = NativeFixture.success value")
          .Replace("let limits = NativeFixture.limits", "let limits w r = NativeFixture.limits w r")
let output = Path.Combine(here, "obj", "Generated.AuthorityTests.fs")
Directory.CreateDirectory(Path.GetDirectoryName output) |> ignore
File.WriteAllText(output, original + Environment.NewLine + File.ReadAllText(Path.Combine(here, "AuthorityTests.fs")))
printfn "Frozen fixture blob %s" actual
