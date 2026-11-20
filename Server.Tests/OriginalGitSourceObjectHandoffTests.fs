module OriginalGitSourceObjectHandoffTests

open System
open System.Diagnostics
open System.IO
open System.Threading.Tasks
open NUnit.Framework

// Disposable Git-native evidence only. These local remotes are NOT an authenticated
// publisher, original operation journal, or license to write a production staging ref.
let private git directory args =
    let start = ProcessStartInfo("git")
    start.WorkingDirectory <- directory
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    start.UseShellExecute <- false
    // Keep disposable Git isolated from credentials, global/system configuration and hooks.
    start.Environment["GIT_CONFIG_NOSYSTEM"] <- "1"
    start.Environment["GIT_CONFIG_GLOBAL"] <- Path.Combine(directory, ".fixture-no-global-config")
    start.Environment["GIT_TERMINAL_PROMPT"] <- "0"
    start.Environment["GIT_CONFIG_COUNT"] <- "1"
    start.Environment["GIT_CONFIG_KEY_0"] <- "core.hooksPath"
    start.Environment["GIT_CONFIG_VALUE_0"] <- Path.Combine(directory, ".fixture-no-hooks")
    for arg in args do start.ArgumentList.Add arg
    use child = Process.Start start
    // Concurrent drains prevent pipe-buffer deadlock; one deadline covers exit and both EOFs.
    let stdout = child.StandardOutput.ReadToEndAsync()
    let stderr = child.StandardError.ReadToEndAsync()
    let completed = Task.WhenAll([| child.WaitForExitAsync(); stdout :> Task; stderr :> Task |])
    try completed.WaitAsync(TimeSpan.FromSeconds(15.0)).GetAwaiter().GetResult()
    with :? TimeoutException ->
        try
            if not child.HasExited then child.Kill true
        with :? InvalidOperationException -> ()
        // Never wait without a deadline during failure cleanup, including inherited pipe handles.
        try completed.WaitAsync(TimeSpan.FromSeconds(2.0)).GetAwaiter().GetResult()
        with _ -> ()
        Assert.Fail(sprintf "Disposable git %A timed out before exit and both pipe EOFs." args)
    child.ExitCode, stdout.GetAwaiter().GetResult().Trim(), stderr.GetAwaiter().GetResult().Trim()

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

// NON-authoritative fixture decision: the trusted path/ref are independently pinned,
// not taken from the offered source. This is not real remote authentication or receipt verification.
let private fixturePinnedSourceCheck trustedOriginal offeredRemote trustedRef offeredRef expectedOid expectedParent reader =
    if not (String.Equals(Path.GetFullPath(trustedOriginal), Path.GetFullPath(offeredRemote), StringComparison.Ordinal)) then
        Error "repository mismatch"
    elif offeredRef <> trustedRef then
        Error "ref mismatch"
    elif advertised reader offeredRemote offeredRef <> Some expectedOid then
        Error "remote head mismatch"
    elif parent reader expectedOid <> [ expectedParent ] then
        Error "parent mismatch"
    else Ok ()

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
        // A legitimate sibling commit is a plausible but incorrect expected parent.
        mustGit producer [ "checkout"; "-q"; "-b"; "alternate-parent"; baseOid ] |> ignore
        let alternateParent = commit producer "alternate-parent"
        mustGit producer [ "checkout"; "-q"; "main" ] |> ignore
        let aliasRef = "refs/heads/fixture-object-alias"
        mustGit producer [ "push"; "--porcelain"; original; candidate + ":" + managedRef ] |> ignore
        // Both adversarial routes advertise the SAME candidate object as the pinned original.
        mustGit producer [ "push"; "--porcelain"; unrelated; candidate + ":" + managedRef ] |> ignore
        mustGit producer [ "push"; "--porcelain"; original; candidate + ":" + aliasRef ] |> ignore
        mustGit reader [ "fetch"; "--no-tags"; "--no-write-fetch-head"; "--refmap=";
                         original; managedRef + ":" + localStage ] |> ignore
        Assert.That(mustGit reader [ "cat-file"; "-t"; candidate ], Is.EqualTo("commit"))
        Assert.That(advertised reader unrelated managedRef, Is.EqualTo(Some candidate))
        Assert.That(advertised reader original aliasRef, Is.EqualTo(Some candidate))
        let check repo gitRef expectedParent =
            fixturePinnedSourceCheck original repo managedRef gitRef candidate expectedParent reader
        Assert.That(check original managedRef baseOid = Ok (), Is.True, "Fixture positive control")
        Assert.That(check unrelated managedRef baseOid = Error "repository mismatch", Is.True,
                    "Wrong repository with identical OID/ref must not satisfy pinned fixture identity")
        Assert.That(check original aliasRef baseOid = Error "ref mismatch", Is.True,
                    "Unapproved ref alias with identical OID must not satisfy pinned ref")
        Assert.That(check original managedRef alternateParent = Error "parent mismatch", Is.True,
                    "Valid but different expected parent must be rejected")
        let successor = commit producer "successor"
        // Remote advance can be observed, but a durable effect-receipt/unknown push
        // reconciliation state machine cannot be tested by Git operations alone.
        mustGit producer [ "push"; "--porcelain"; original; successor + ":" + managedRef ] |> ignore
        Assert.That(advertised reader original managedRef, Is.EqualTo(Some successor))
        Assert.That(check original managedRef baseOid = Error "remote head mismatch", Is.True,
                    "Cached candidate must fail after remote head moves")
        Assert.That(mustGit reader [ "cat-file"; "-t"; candidate ], Is.EqualTo("commit"),
                    "A cached object alone must not authorize a replay"))
