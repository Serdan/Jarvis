open System
open System.IO
open System.Diagnostics

// Source-exact generator for the standalone, test-only compilation unit.
// Writes exclusively inside this scripts/tests/... lease. Never changes source.
let here = __SOURCE_DIRECTORY__
let root = Path.GetFullPath(Path.Combine(here, "..", "..", ".."))
let pin = "5607baf9913f4976add13c984e0182906d4fe8c7"
let source = "Client/Modules/NativeSchedulerAccountReservation.fs"
let fixture = Path.Combine(root, "Client.Tests", "NativeSchedulerRosterPolicyTests.fs")
let property = Path.Combine(here, "Property.fs")
let output = Path.Combine(here, "Generated.NativeReservation.Property.fs")

let git (args: string list) =
    use p = new Process()
    p.StartInfo.FileName <- "git"
    p.StartInfo.WorkingDirectory <- root
    p.StartInfo.RedirectStandardOutput <- true
    p.StartInfo.RedirectStandardError <- true
    p.StartInfo.Environment["GIT_TERMINAL_PROMPT"] <- "0"
    args |> List.iter p.StartInfo.ArgumentList.Add
    if not (p.Start()) then failwith "Git did not start."
    let stdout = p.StandardOutput.ReadToEnd().Trim()
    let stderr = p.StandardError.ReadToEnd().Trim()
    if not (p.WaitForExit(30000)) then
        p.Kill(true)
        failwith "Bounded git operation timed out."
    p.ExitCode, stdout, stderr

let requireGit args =
    match git args with
    | 0, output, _ -> output
    | code, _, error -> failwithf "SOURCE_GIT_FAILURE(%d): %s" code error

let expected = requireGit [ "rev-parse"; pin + ":" + source ]
let actual = requireGit [ "rev-parse"; "HEAD:" + source ]
let control =
    if fsi.CommandLineArgs.Length > 1 then fsi.CommandLineArgs[1]
    else expected

if control <> actual then
    failwithf "SOURCE_PIN_MISMATCH expected=%s actual=%s" control actual

let ancestor, _, _ = git [ "merge-base"; "--is-ancestor"; pin; "HEAD" ]
if ancestor <> 0 then failwith "SOURCE_LINEAGE_MISMATCH: accepted source not an ancestor."
let frozenFixture = requireGit [ "rev-parse"; pin + ":Client.Tests/NativeSchedulerRosterPolicyTests.fs" ]
let worktreeFixture = requireGit [ "rev-parse"; "HEAD:Client.Tests/NativeSchedulerRosterPolicyTests.fs" ]
if frozenFixture <> worktreeFixture then
    failwith "FROZEN_FIXTURE_MISMATCH: original fixture changed from reviewed source."
if File.Exists(output) then
    failwith "GENERATED_PATH_NOT_EMPTY: refusing to overwrite a test compilation unit."

let original = File.ReadAllText(fixture)
if not (original.Contains("let success = NativeFixture.success")
        && original.Contains("let limits = NativeFixture.limits")) then
    failwith "FROZEN_FIXTURE_LAYOUT_MISMATCH: expected function aliases absent."

// In a single-file standalone assembly the original alias-field initializers
// are not automatically run. Eta-expansion changes only the generated copy,
// preserving the same calls while avoiding uninitialized FSharpFunc fields.
let standalone =
    original.Replace("let success = NativeFixture.success",
                     "let success value = NativeFixture.success value")
            .Replace("let limits = NativeFixture.limits",
                     "let limits w r = NativeFixture.limits w r")
let body = File.ReadAllText(property)
File.WriteAllText(output, standalone + body)
printfn "PREPARED_SOURCE_EXACT pin=%s sourceBlob=%s fixtureBlob=%s output=%s"
    pin actual frozenFixture output
