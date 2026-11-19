module OriginalGitSourceObjectHandoffTests

open System
open System.Diagnostics
open System.IO
open NUnit.Framework

// Disposable Git-native evidence only. These local remotes are NOT an authenticated
// publisher, original operation journal, or license to write a production staging ref.
let private git directory args =
    let start = ProcessStartInfo("git")
    start.WorkingDirectory <- directory
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    start.UseShellExecute <- false
    for arg in args do start.ArgumentList.Add arg
    use child = Process.Start start
    let stdout = child.StandardOutput.ReadToEnd().Trim()
    let stderr = child.StandardError.ReadToEnd().Trim()
    if not (child.WaitForExit 15000) then
        child.Kill true
        Assert.Fail(sprintf "Disposable git %A timed out." args)
    child.ExitCode, stdout, stderr

let private mustGit directory args =
    let code, output, error = git directory args
    if code <> 0 then Assert.Fail(sprintf "Disposable git %A failed: %s" args error)
    output

let private missingGit directory args =
    let code, _, _ = git directory args
    code <> 0

let private withRepositories action =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-original-handoff-" + Guid.NewGuid().ToString("N"))
    let folder name =
        let result = Path.Combine(root, name)
        Directory.CreateDirectory result |> ignore
        result
    let producer, original, unrelated, reader = folder "producer", folder "original.git", folder "unrelated.git", folder "reader"
    try
        mustGit producer [ "init"; "-q"; "-b"; "main" ] |> ignore
        mustGit producer [ "config"; "user.name"; "Disposable Handoff" ] |> ignore
        mustGit producer [ "config"; "user.email"; "handoff@example.invalid" ] |> ignore
        mustGit original [ "init"; "-q"; "--bare" ] |> ignore
        mustGit unrelated [ "init"; "-q"; "--bare" ] |> ignore
        mustGit reader [ "init"; "-q"; "-b"; "main" ] |> ignore
        action producer original unrelated reader
    finally
        if Directory.Exists root then Directory.Delete(root, true)

let private commit (producer: string) (label: string) =
    File.WriteAllText(Path.Combine(producer, "fixture.txt"), label)
    mustGit producer [ "add"; "fixture.txt" ] |> ignore
    mustGit producer [ "commit"; "-q"; "-m"; label ] |> ignore
    mustGit producer [ "rev-parse"; "HEAD" ]

let private managedRef = "refs/heads/fixture-object-handoff"
let private localStage = "refs/jarvis/fixture-object-handoff"

let private advertised producer remote gitRef =
    let response = mustGit producer [ "ls-remote"; "--refs"; remote; gitRef ]
    if response = "" then None
    else
        let parts = response.Split('\t')
        Assert.That(parts.Length, Is.EqualTo(2), "Ambiguous remote ref response")
        Assert.That(parts[1], Is.EqualTo(gitRef), "Wrong remote ref")
        Some parts[0]

let private parent reader oid =
    let line = mustGit reader [ "rev-list"; "--parents"; "-n"; "1"; oid ]
    let parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
    Assert.That(parts[0], Is.EqualTo(oid))
    parts |> Array.skip 1 |> Array.toList

[<Test>]
let client_local_sha_does_not_make_object_available_in_independent_original () =
    withRepositories (fun producer original unrelated reader ->
        let candidate = commit producer "candidate-local-only"
        Assert.That(mustGit producer [ "cat-file"; "-t"; candidate ], Is.EqualTo("commit"))
        Assert.That(advertised producer original managedRef, Is.EqualTo(None))
        Assert.That(missingGit original [ "cat-file"; "-e"; candidate + "^{commit}" ], Is.True)
        Assert.That(missingGit unrelated [ "cat-file"; "-e"; candidate + "^{commit}" ], Is.True)
        Assert.That(missingGit reader [ "cat-file"; "-e"; candidate + "^{commit}" ], Is.True)
        // SHA, local log and ls-remote do not transport an unpublished object.
        Assert.That(missingGit original [ "show-ref"; "--verify"; "--quiet"; "refs/heads/jarvis-orchestration" ], Is.True))

[<Test>]
let disposable_nonprotected_ref_can_deliver_exact_commit_tree_and_parent () =
    withRepositories (fun producer original unrelated reader ->
        let baseOid = commit producer "base"
        let candidate = commit producer "candidate"
        Assert.That(parent producer candidate = [ baseOid ], Is.True)
        Assert.That(advertised producer original managedRef, Is.EqualTo(None))
        // This local bare fixture is deliberately not the protected original ref.
        mustGit producer [ "push"; "--porcelain"; original; candidate + ":" + managedRef ] |> ignore
        Assert.That(advertised producer original managedRef, Is.EqualTo(Some candidate))
        Assert.That(advertised producer unrelated managedRef, Is.EqualTo(None))
        mustGit reader [ "fetch"; "--no-tags"; "--no-write-fetch-head"; "--refmap=";
                         original; managedRef + ":" + localStage ] |> ignore
        Assert.That(mustGit reader [ "rev-parse"; "--verify"; localStage + "^{commit}" ], Is.EqualTo(candidate))
        Assert.That(mustGit reader [ "cat-file"; "-t"; candidate ], Is.EqualTo("commit"))
        let tree = mustGit reader [ "rev-parse"; candidate + "^{tree}" ]
        Assert.That(mustGit reader [ "cat-file"; "-t"; tree ], Is.EqualTo("tree"))
        Assert.That(parent reader candidate = [ baseOid ], Is.True)
        Assert.That(missingGit original [ "show-ref"; "--verify"; "--quiet"; "refs/heads/jarvis-orchestration" ], Is.True)
        Assert.That(missingGit original [ "show-ref"; "--verify"; "--quiet"; "refs/heads/jarvis-account-admission" ], Is.True))

[<Test>]
let cached_object_cannot_prove_wrong_repo_ref_parent_or_stale_remote_head () =
    withRepositories (fun producer original unrelated reader ->
        let baseOid = commit producer "base"
        let candidate = commit producer "candidate"
        mustGit producer [ "push"; "--porcelain"; original; candidate + ":" + managedRef ] |> ignore
        mustGit reader [ "fetch"; "--no-tags"; "--no-write-fetch-head"; "--refmap=";
                         original; managedRef + ":" + localStage ] |> ignore
        Assert.That(mustGit reader [ "cat-file"; "-t"; candidate ], Is.EqualTo("commit"))
        Assert.That(advertised reader unrelated managedRef, Is.EqualTo(None), "Wrong remote is not an authoritative source")
        Assert.That(advertised reader original "refs/heads/unrelated", Is.EqualTo(None), "Wrong ref must fail")
        Assert.That(parent reader candidate = [ candidate ], Is.False, "Wrong parent must fail")
        Assert.That(parent reader candidate = [ baseOid ], Is.True)
        let successor = commit producer "successor"
        // Model lost push acknowledgement: only the remote observation survives.
        mustGit producer [ "push"; "--porcelain"; original; successor + ":" + managedRef ] |> ignore
        let callerHasPushAcknowledgement = false
        Assert.That(callerHasPushAcknowledgement, Is.False)
        Assert.That(advertised reader original managedRef, Is.EqualTo(Some successor))
        Assert.That(advertised reader original managedRef, Is.Not.EqualTo(Some candidate),
                    "Cached candidate is stale after delayed/unknown remote advance")
        Assert.That(mustGit reader [ "cat-file"; "-t"; candidate ], Is.EqualTo("commit"),
                    "A cached object alone must not authorize a replay"))