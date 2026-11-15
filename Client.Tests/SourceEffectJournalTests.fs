module SourceEffectJournalTests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks
open Client.SourceEffectJournal
open NUnit.Framework

let private hash (value: string) =
    SHA256.HashData(Encoding.UTF8.GetBytes value)
    |> Convert.ToHexString
    |> fun text -> text.ToLowerInvariant()

let private success (result: Result<'a, string>) =
    match result with Ok value -> value | Error error -> failwith error
let private denied (result: Result<'a, string>) =
    match result with
    | Error reason -> Assert.That(reason, Does.Contain "RecoveryBlocked")
    | Ok _ -> Assert.Fail("Expected fail-closed rejection.")

let private withStore callback =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-wq03-effect-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    try callback root (openAtHostRoot root |> success)
    finally if Directory.Exists root then Directory.Delete(root, true)

let private scope () =
    { ProjectName = "Fixture"
      ProjectId = Guid.NewGuid()
      LedgerId = Guid.NewGuid()
      LedgerEpoch = 3
      RepositoryId = hash "approved-repository"
      ApprovedRemoteFingerprint = hash "approved-fetch-push"
      SourceRef = "refs/heads/main"
      CheckoutId = "checkout-generation-1"
      WorkId = "work-01"
      LaunchUuid = Guid.NewGuid()
      ClaimGeneration = 5 }

let private intent scope kind operationId request =
    { OriginalOperationId = operationId
      Scope = scope
      Kind = kind
      RequestSha256 = hash request }

let private git cwd args =
    use p = new Process()
    p.StartInfo.FileName <- "git"
    p.StartInfo.WorkingDirectory <- cwd
    p.StartInfo.UseShellExecute <- false
    p.StartInfo.RedirectStandardError <- true
    for arg in args do p.StartInfo.ArgumentList.Add arg
    if not (p.Start()) then failwith "Cannot start test Git."
    let stderr = p.StandardError.ReadToEnd()
    p.WaitForExit()
    if p.ExitCode <> 0 then failwithf "Git fixture %A: %s" args stderr

[<Test>]
let predispatch_is_durable_before_callback () =
    withStore (fun _ store ->
        let binding = scope ()
        let original = intent binding ManagedJob (Guid.NewGuid()) "job-payload"
        let mutable invoked = 0
        let value = dispatchOnce store original (fun () ->
            invoked <- invoked + 1
            let evidence = inspectForHandoff store binding [ original.OriginalOperationId ]
            Assert.That(evidence.Records.Length, Is.EqualTo 1)
            Assert.That(evidence.Records.Head.State, Is.EqualTo Prepared)
            Assert.That(evidence.RecoveryBlocked, Is.True)
            "started")
        Assert.That((value = Ok "started"), Is.True)
        denied (dispatchOnce store original (fun () -> invoked <- invoked + 1))
        Assert.That(invoked, Is.EqualTo 1))

[<Test>]
let restart_original_uuid_never_redispatches () =
    withStore (fun root first ->
        let binding = scope ()
        let original = intent binding ManagedCommand (Guid.NewGuid()) "command"
        Assert.That((beginBeforeDispatch first original = Ok FirstDispatch), Is.True)
        let restarted = openAtHostRoot root |> success
        Assert.That((beginBeforeDispatch restarted original = Ok (AlreadyRecorded Prepared)), Is.True)
        let mutable invoked = false
        denied (dispatchOnce restarted original (fun () -> invoked <- true))
        Assert.That(invoked, Is.False)
        Assert.That((inspectForHandoff restarted binding [ original.OriginalOperationId ]).RecoveryBlocked, Is.True))

[<Test>]
let two_git_clones_cannot_borrow_other_checkout () =
    withStore (fun root first ->
        let bare, a, b = Path.Combine(root, "bare.git"), Path.Combine(root, "clone-a"), Path.Combine(root, "clone-b")
        git root [ "init"; "--bare"; "-q"; bare ]
        git root [ "clone"; "-q"; bare; a ]
        git root [ "clone"; "-q"; bare; b ]
        git a [ "rev-parse"; "--is-inside-work-tree" ]
        git b [ "rev-parse"; "--is-inside-work-tree" ]
        let identity = scope ()
        let original = intent identity GitPush (Guid.NewGuid()) "original"
        beginBeforeDispatch first original |> success |> ignore
        let other = openAtHostRoot root |> success
        let newCheckout = { identity with CheckoutId = "checkout-generation-2" }
        denied (beginBeforeDispatch other { original with Scope = newCheckout })
        let report = inspectForHandoff other newCheckout [ original.OriginalOperationId ]
        Assert.That(report.MissingOriginalOperations = [ original.OriginalOperationId ], Is.True)
        Assert.That(report.RecoveryBlocked, Is.True))

[<Test>]
let concurrent_two_client_dispatch_is_at_most_once () =
    withStore (fun root first ->
        let other = openAtHostRoot root |> success
        let identity = scope ()
        let original = intent identity ManagedCommand (Guid.NewGuid()) "source-mutation"
        let mutable invoked = 0
        let guard = obj()
        let results = Array.zeroCreate<Result<int, string>> 2
        let clients = [| first; other |]
        Parallel.For(0, 2, fun n ->
            results.[n] <- dispatchOnce clients.[n] original (fun () ->
                lock guard (fun () -> invoked <- invoked + 1)
                1)) |> ignore
        Assert.That(invoked, Is.EqualTo 1)
        Assert.That(results |> Array.filter Result.isOk |> Array.length, Is.EqualTo 1)
        Assert.That(results |> Array.filter Result.isError |> Array.length, Is.EqualTo 1))

[<Test>]
let lost_push_ack_retains_original_uuid_and_source_fence () =
    withStore (fun root store ->
        let identity = scope ()
        let original = intent identity GitPush (Guid.NewGuid()) "refs/heads/jarvis-wip/old"
        let mutable pushed = 0
        dispatchOnce store original (fun () -> pushed <- pushed + 1) |> success |> ignore
        recordUncertain store original "remote reply lost; original may still land" |> success
        let restarted = openAtHostRoot root |> success
        let evidence = inspectForHandoff restarted identity [ original.OriginalOperationId ]
        Assert.That(evidence.RecoveryBlocked, Is.True)
        Assert.That(evidence.UninstrumentedRoutes, Does.Contain "GitPush")
        match evidence.Records.Head.State with
        | Uncertain reason -> Assert.That(reason, Does.Contain "original may still land")
        | _ -> Assert.Fail("Expected unresolved original push")
        denied (dispatchOnce restarted original (fun () -> pushed <- pushed + 1))
        Assert.That(pushed, Is.EqualTo 1))

[<Test>]
let clean_checkout_does_not_prove_old_process_terminal () =
    withStore (fun root store ->
        let identity = scope ()
        let original = intent identity ManagedJob (Guid.NewGuid()) "late-process"
        beginBeforeDispatch store original |> success |> ignore
        let cleanCheckout = Path.Combine(root, "clean-checkout")
        Directory.CreateDirectory cleanCheckout |> ignore
        let report = inspectForHandoff store identity [ original.OriginalOperationId ]
        Assert.That(report.Records.Head.State, Is.EqualTo Prepared)
        Assert.That(report.AllKnownOperationsTerminal, Is.False)
        Assert.That(report.RecoveryBlocked, Is.True))

[<Test>]
let finished_file_edit_does_not_attest_all_mutation_routes () =
    withStore (fun root store ->
        let identity = scope ()
        let original = intent identity FileEdit (Guid.NewGuid()) "file-edit"
        let output = Path.Combine(root, "sample.txt")
        let completion = runSynchronousFileEdit store original (fun () ->
            File.WriteAllText(output, "first")
            123)
        Assert.That((completion = Ok 123), Is.True)
        let report = inspectForHandoff store identity [ original.OriginalOperationId ]
        Assert.That(report.AllKnownOperationsTerminal, Is.True)
        Assert.That(report.RecoveryBlocked, Is.True)
        Assert.That(report.UninstrumentedRoutes, Does.Contain "LegacyOrOutOfBandOSGit")
        denied (runSynchronousFileEdit store original (fun () -> File.WriteAllText(output, "again")))
        Assert.That(File.ReadAllText(output), Is.EqualTo "first"))

[<Test>]
let changed_payload_project_and_ledger_are_rejected () =
    withStore (fun root store ->
        let identity = scope ()
        let original = intent identity GitMerge (Guid.NewGuid()) "merge-1"
        beginBeforeDispatch store original |> success |> ignore
        denied (beginBeforeDispatch store { original with RequestSha256 = hash "merge-2" })
        let another = openAtHostRoot root |> success
        denied (beginBeforeDispatch another { original with Scope = { identity with ProjectId = Guid.NewGuid() } })
        denied (beginBeforeDispatch another { original with Scope = { identity with LedgerId = Guid.NewGuid() } }))

[<Test>]
let callback_exception_becomes_uncertain_and_cannot_be_replayed () =
    withStore (fun root store ->
        let identity = scope ()
        let original = intent identity Retry (Guid.NewGuid()) "retry-unknown"
        let mutable invoked = 0
        denied (dispatchOnce store original (fun () ->
            invoked <- invoked + 1
            failwith "disconnected after effect"))
        let restarted = openAtHostRoot root |> success
        let report = inspectForHandoff restarted identity [ original.OriginalOperationId ]
        match report.Records.Head.State with Uncertain _ -> () | _ -> Assert.Fail("Lost uncertainty")
        denied (dispatchOnce restarted original (fun () -> invoked <- invoked + 1))
        Assert.That(invoked, Is.EqualTo 1))

[<Test>]
let missing_journal_legacy_route_and_inventory_block () =
    withStore (fun _ store ->
        let identity = scope ()
        let empty = inspectForHandoff store identity []
        Assert.That(empty.RecoveryBlocked, Is.True)
        Assert.That(empty.UninstrumentedRoutes, Does.Contain "RunCommand")
        Assert.That(empty.AllKnownOperationsTerminal, Is.False)
        let missing = inspectForHandoff store identity [ Guid.NewGuid() ]
        Assert.That(missing.MissingOriginalOperations.Length, Is.EqualTo 1)
        Assert.That(missing.RecoveryBlocked, Is.True))

[<Test>]
let durable_recovery_block_reason_survives_restart () =
    withStore (fun root store ->
        let identity = scope ()
        markRecoveryBlocked store identity "Original push receipt unverified" |> success
        let recovered = openAtHostRoot root |> success
        let report = inspectForHandoff recovered identity []
        Assert.That(report.RecoveryBlocked, Is.True)
        Assert.That(report.BlockReasons, Does.Contain "Original push receipt unverified"))

[<Test>]
let corrupt_store_fails_closed () =
    withStore (fun root _ ->
        File.WriteAllText(Path.Combine(root, "source-effects-v1.sqlite"), "not a database")
        match openAtHostRoot root with
        | Ok _ -> Assert.Fail("Corrupt source journal should fail closed.")
        | Error reason -> Assert.That(reason, Does.Contain "RecoveryBlocked"))
