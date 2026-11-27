module RoleInstructionDiagnostics

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open Client.LedgerStore
open Client.RoleInstructions
open NUnit.Framework

let private git path args =
    use proc = new Process()
    proc.StartInfo.FileName <- "git"
    proc.StartInfo.WorkingDirectory <- path
    proc.StartInfo.RedirectStandardOutput <- true
    proc.StartInfo.RedirectStandardError <- true
    args |> List.iter proc.StartInfo.ArgumentList.Add
    Assert.That(proc.Start(), Is.True)
    let stdout = proc.StandardOutput.ReadToEnd()
    let stderr = proc.StandardError.ReadToEnd()
    proc.WaitForExit()
    Assert.That(proc.ExitCode, Is.EqualTo(0), stderr)
    stdout.Trim()

let private ok = function
    | Ok value -> value
    | Error error -> failwithf "Unexpected ledger error: %A" error

let private fixture action =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-role-diagnostics-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(root) |> ignore
    try
        let bare = Path.Combine(root, "remote.git")
        let owner = Path.Combine(root, "owner")
        git root [ "init"; "--bare"; bare ] |> ignore
        git root [ "clone"; bare; owner ] |> ignore
        git owner [ "config"; "user.name"; "Role Fixture" ] |> ignore
        git owner [ "config"; "user.email"; "fixture@example.test" ] |> ignore
        File.WriteAllText(Path.Combine(owner, "source.txt"), "untouched")
        git owner [ "add"; "source.txt" ] |> ignore
        git owner [ "commit"; "-m"; "base" ] |> ignore
        git owner [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
        let binding : LedgerBinding =
            { RepositoryPath = owner; RemoteName = "origin"
              ApprovedFetchUrl = bare; ApprovedPushUrl = bare
              ProjectId = Guid.NewGuid(); LedgerId = Guid.NewGuid(); Epoch = 1 }
        LedgerStore.initialize binding true |> ok |> ignore
        action binding
    finally
        Directory.Delete(root, true)

let private repeat (c: char) n = String(c, n)
let private bytes (s: string) = Encoding.UTF8.GetByteCount s
let private escapedBytes (s: string) = bytes (JsonSerializer.Serialize s)

let private invalid (expected: string) outcome =
    match outcome with
    | Error(InvalidBinding message) ->
        Assert.That(message, Is.EqualTo(expected))
        Assert.That(message, Does.Not.Contain("SECRET"))
    | other -> Assert.Fail(sprintf "Expected InvalidBinding: %A" other)

[<Test>]
let ``role write diagnostics are field specific and rejected requests leave ledger unchanged`` () =
    fixture (fun binding ->
        let create actor id purpose body =
            RoleInstructions.createRole binding actor (Guid.NewGuid()) id purpose body
        let update actor version purpose body =
            RoleInstructions.updateRole binding actor (Guid.NewGuid()) "Role" version purpose body
        create "Lead" "Role" "Purpose" "Ordinary instructions" |> ok |> ignore
        let before = (LedgerStore.fetch binding |> ok).Commit
        let reject expected result =
            invalid expected result
            Assert.That((LedgerStore.fetch binding |> ok).Commit, Is.EqualTo(before))
        for value in [ null; ""; " \t" ] do
            reject "Role purpose must be nonblank." (update "Lead" 1 value "Instructions")
            reject "Role instructions must be nonblank." (update "Lead" 1 "Purpose" value)
        reject "Role ID must match ^[A-Za-z0-9_-]{1,80}$ (measured 81 UTF-16 code units)." (create "Lead" (repeat 'x' 81) "Purpose" "Instructions")
        reject "Role ID must match ^[A-Za-z0-9_-]{1,80}$ (measured 7 UTF-16 code units)." (create "Lead" "SECRET!" "Purpose" "Instructions")
        reject "Role ID must match ^[A-Za-z0-9_-]{1,80}$ (null)." (create "Lead" null "Purpose" "Instructions")
        reject "Role version must be at least 1 (measured -1)." (update "Lead" -1 null null)
        reject "Role version must be at least 1 (measured 0)." (update "Lead" 0 "Purpose" "Instructions")
        reject "Role actor must be nonblank." (update " " 1 "Purpose" "Instructions")
        reject "Role purpose exceeds 4096 UTF-8 bytes (measured 4097)." (update "Lead" 1 (repeat 'é' 2048 + "x") "Instructions")
        // Raw byte size is checked before JSON escaping, even though a raw
        // value this large also necessarily exceeds the escaped limit.
        reject "Role instructions exceed 32768 UTF-8 bytes (measured 32769)." (update "Lead" 1 "Purpose" (repeat 'é' 16384 + "x"))
        for raw in [32767; 32768] do
            let body = repeat 'a' raw
            reject (sprintf "Role instructions exceed 16384 JSON-escaped UTF-8 bytes including quotes (measured %d)." (raw + 2)) (update "Lead" 1 "Purpose" body)
        // Exact escaped +1 with ASCII, non-ASCII and control-heavy inputs.
        for body in [ repeat 'a' 16383; repeat 'é' 2730 + "aaa"; repeat '\u0001' 2730 + "aaa" ] do
            Assert.That(escapedBytes body, Is.EqualTo(16385))
            reject "Role instructions exceed 16384 JSON-escaped UTF-8 bytes including quotes (measured 16385)." (update "Lead" 1 "Purpose" body)
        // Preserve first failing field when several arguments are invalid.
        reject "Role ID must match ^[A-Za-z0-9_-]{1,80}$ (measured 1 UTF-16 code units)." (create "" "!" null null)
        reject "Role purpose must be nonblank." (update "" 1 null null)
        match update "Lead" 2 "Purpose" "Instructions" with
        | Error(StaleEntityVersion _) -> ()
        | other -> Assert.Fail(sprintf "Expected stale version: %A" other)
        Assert.That((LedgerStore.fetch binding |> ok).Commit, Is.EqualTo(before))
        let role = (RoleInstructions.getRole binding "Role" |> ok).Role
        Assert.That(role.Version, Is.EqualTo(1))
        Assert.That(role.Instructions, Is.EqualTo("Ordinary instructions")))

[<Test>]
let ``accepted boundary edits round trip and replay idempotently`` () =
    fixture (fun binding ->
        let roleId = repeat 'a' 80
        let actor = "Lead"
        let initialPurpose = repeat 'é' 2047 + "x"
        RoleInstructions.createRole binding actor (Guid.NewGuid()) roleId initialPurpose "Ordinary" |> ok |> ignore
        let bodies = [ repeat 'a' 16381; repeat 'a' 16382; repeat 'é' 2730 + "aa"; repeat '\u0001' 2730 + "aa" ]
        for i, body in List.indexed bodies do
            let expected = i + 1
            let purpose = repeat 'é' 2048
            let op = Guid.NewGuid()
            Assert.That(bytes purpose, Is.EqualTo(4096))
            Assert.That(escapedBytes body, Is.InRange(16383, 16384))
            let edit () = RoleInstructions.updateRole binding actor op roleId expected purpose body |> ok
            edit () |> ignore
            let head = (LedgerStore.fetch binding |> ok).Commit
            edit () |> ignore
            Assert.That((LedgerStore.fetch binding |> ok).Commit, Is.EqualTo(head))
            let role = (RoleInstructions.getRole binding roleId |> ok).Role
            Assert.That(role.Version, Is.EqualTo(expected + 1))
            Assert.That(role.Purpose, Is.EqualTo(purpose))
            Assert.That(role.Instructions, Is.EqualTo(body)))
