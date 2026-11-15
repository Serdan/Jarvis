module SourceEffectRouteBridgeTests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks
open Client.SourceEffectJournal
open Client.SourceEffectRouteBridge
open Common
open NUnit.Framework

let private hash (value: string) =
    SHA256.HashData(Encoding.UTF8.GetBytes value)
    |> Convert.ToHexString
    |> fun h -> h.ToLowerInvariant()

let private get = function
    | Ok value -> value
    | Error reason -> failwith reason

let private denied (result: Result<'a, string>) =
    match result with
    | Error reason -> Assert.That(reason, Does.Contain "RecoveryBlocked")
    | Ok _ -> Assert.Fail("Expected fail-closed route boundary.")

let private openAtHostRoot root =
    Client.SourceEffectJournal.openExistingAtHostRoot root (root + ".continuity.anchor")

let private withJournal callback =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-wq03-route-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(root) |> ignore
    try
        provisionTrustedHostRoot root (root + ".continuity.anchor") |> get |> ignore
        callback root (openAtHostRoot root |> get)
    finally
        if Directory.Exists root then Directory.Delete(root, true)
        if File.Exists(root + ".continuity.anchor") then File.Delete(root + ".continuity.anchor")

let private scope () =
    { ProjectName = "Fixture"
      ProjectId = Guid.NewGuid()
      LedgerId = Guid.NewGuid()
      LedgerEpoch = 3
      RepositoryId = hash "trusted-repository"
      ApprovedRemoteFingerprint = hash "approved-remote"
      SourceRef = "refs/heads/main"
      CheckoutId = "wq03-work-01-launch-01-g4"
      WorkId = "work-01"
      LaunchUuid = Guid.NewGuid()
      ClaimGeneration = 4 }

let private selector (identity: SourceEffectScope) = identity.ProjectName + "::" + identity.CheckoutId

let private command identity =
    RunCommandCommand
        { ProjectName = selector identity
          Executable = "git"
          Args = [ "push"; "origin"; "HEAD" ]
          Reason = Some "fixture"
          WorkingDirectory = Some "."
          TimeoutSeconds = Some 3
          MaxOutputBytes = Some 2048 }

let private fileCommand identity =
    WriteFileCommand
        { ProjectName = selector identity
          FilePath = "write.txt"
          Content = "effect"
          FileWriteMode = FileWriteMode.Write
          ExpectedHash = None
          CreateParents = Some false }

let private intent identity kind cmd id =
    { OriginalOperationId = id
      Scope = identity
      Kind = kind
      RequestSha256 = requestSha256 cmd }

let private execute invocation command handler =
    executeDiagnostic invocation command handler
    |> fun job -> job.GetAwaiter().GetResult()

[<Test>]
let actual_dispatch_route_inventory_never_mints_a_source_uuid () =
    let identity = scope()
    let run = command identity
    let write = fileCommand identity
    let taskRun = RunProjectTaskCommand { ProjectName = selector identity; TaskName = "local-test" }
    let start = StartJobCommand
                    { ProjectName = selector identity; Executable = "git"; Args = [ "push" ]
                      Reason = None; WorkingDirectory = None; MaxOutputBytes = None }
    let commit = GitCommitCommand
                    { ProjectName = selector identity; Message = "fixture"; Body = None
                      Paths = [ "write.txt" ]; AllowEmpty = false }
    let import = ImportFileCommand
                    { ProjectName = selector identity; FilePath = "file"; DownloadUrl = "https://example.invalid/file"
                      FileId = "x"; MimeType = None; FileName = None
                      Overwrite = false; CreateParents = false; MaxBytes = None }
    Assert.That(classify run = JournalCandidate("RunCommand", ManagedCommand, false), Is.True)
    Assert.That(classify write = JournalCandidate("WriteFile", FileEdit, true), Is.True)
    Assert.That(classify taskRun = JournalCandidate("RunProjectTask", ManagedCommand, false), Is.True)
    Assert.That(classify start = JournalCandidate("StartJob", ManagedJob, false), Is.True)
    Assert.That(classify commit = JournalCandidate("GitCommit", GitCommit, false), Is.True)
    Assert.That(classify import = JournalCandidate("ImportFile", FileEdit, false), Is.True)
    let uncovered = CreateWorktreeCommand
                        { ProjectName = selector identity; Name = "wq"; Branch = "wq"; BaseRef = "main" }
    Assert.That(classify uncovered = Uncovered "CreateWorktree", Is.True)

[<Test>]
let explicit_nonproduction_gate_denies_unbound_generation_without_changing_legacy () =
    let identity = scope()
    let run = command identity
    Assert.That(denyUnboundGenerationDispatch false run |> Result.isOk, Is.True)
    denied (denyUnboundGenerationDispatch true run)
    let ordinary =
        match run with
        | RunCommandCommand cmd -> RunCommandCommand { cmd with ProjectName = "Fixture::ordinary-worktree" }
        | _ -> failwith "fixture"
    Assert.That(denyUnboundGenerationDispatch true ordinary |> Result.isOk, Is.True)
    let read = ReadFileCommand
                    { ProjectName = selector identity; FilePath = "readme"; StartLine = None
                      EndLine = None; IncludeLineNumbers = None }
    Assert.That(denyUnboundGenerationDispatch true read |> Result.isOk, Is.True)
    let uncovered = CreateWorktreeCommand
                        { ProjectName = selector identity; Name = "wq"; Branch = "wq"; BaseRef = "main" }
    denied (denyUnboundGenerationDispatch true uncovered)

[<Test>]
let real_route_handler_sees_original_durable_pre_dispatch_and_remains_uncertain () =
    withJournal (fun root store ->
        let identity = scope()
        let cmd = command identity
        let original = intent identity ManagedCommand cmd (Guid.NewGuid())
        let invocation = { Journal = store; TrustedScope = identity; Intent = original }
        let mutable invocations = 0
        let result =
            execute invocation cmd (fun () -> task {
                invocations <- invocations + 1
                let evidence = inspectForHandoff store identity [ original.OriginalOperationId ]
                Assert.That(evidence.Records.Head.State, Is.EqualTo Prepared)
                Assert.That(evidence.RecoveryBlocked, Is.True)
                return (Ok 42 : Result<int, string>)
            })
        Assert.That((result = Ok(Ok 42)), Is.True)
        Assert.That(invocations, Is.EqualTo 1)
        let restored = openAtHostRoot root |> get
        let report = inspectForHandoff restored identity [ original.OriginalOperationId ]
        match report.Records.Head.State with
        | Uncertain message -> Assert.That(message, Does.Contain "process-tree")
        | _ -> Assert.Fail("Process result is not a terminal proof.")
        denied (execute { invocation with Journal = restored } cmd
                    (fun () -> task { invocations <- invocations + 1; return (Ok 43 : Result<int,string>) }))
        Assert.That(invocations, Is.EqualTo 1))

[<Test>]
let awaited_host_file_edit_is_terminal_but_not_complete_source_coverage () =
    withJournal (fun root store ->
        let identity = scope()
        let cmd = fileCommand identity
        let original = intent identity FileEdit cmd (Guid.NewGuid())
        let target = Path.Combine(root, "effect.txt")
        let invocation = { Journal = store; TrustedScope = identity; Intent = original }
        let result = execute invocation cmd (fun () -> task {
            let before = inspectForHandoff store identity [ original.OriginalOperationId ]
            Assert.That(before.Records.Head.State, Is.EqualTo Prepared)
            File.WriteAllText(target, "first")
            return (Ok 7 : Result<int,string>)
        })
        Assert.That((result = Ok(Ok 7)), Is.True)
        let restarted = openAtHostRoot root |> get
        let report = inspectForHandoff restarted identity [ original.OriginalOperationId ]
        Assert.That(report.AllKnownOperationsTerminal, Is.True)
        Assert.That(report.RecoveryBlocked, Is.True)
        Assert.That(report.UninstrumentedRoutes, Does.Contain "LegacyOrOutOfBandOSGit")
        denied (execute { invocation with Journal = restarted } cmd
                    (fun () -> task { File.WriteAllText(target, "again"); return (Ok 8 : Result<int,string>) }))
        Assert.That(File.ReadAllText target, Is.EqualTo "first"))

[<Test>]
let file_edit_error_or_crash_cannot_be_terminalized_on_restart () =
    withJournal (fun root store ->
        let identity = scope()
        let cmd = fileCommand identity
        let original = intent identity FileEdit cmd (Guid.NewGuid())
        let invocation = { Journal = store; TrustedScope = identity; Intent = original }
        let output = Path.Combine(root, "partial.txt")
        let outcome = execute invocation cmd (fun () -> task {
            File.WriteAllText(output, "partial")
            return (Error "write outcome unverified" : Result<int,string>)
        })
        Assert.That((outcome = Ok(Error "write outcome unverified")), Is.True)
        let reopened = openAtHostRoot root |> get
        let evidence = inspectForHandoff reopened identity [ original.OriginalOperationId ]
        match evidence.Records.Head.State with
        | Uncertain _ -> ()
        | _ -> Assert.Fail("Partial file write must remain uncertain.")
        denied (execute { invocation with Journal = reopened } cmd
                    (fun () -> task { File.WriteAllText(output, "replayed"); return (Ok 1 : Result<int,string>) }))
        Assert.That(File.ReadAllText output, Is.EqualTo "partial"))

[<Test>]
let two_clients_same_original_uuid_serialize_before_real_route_handler () =
    withJournal (fun root first ->
        let other = openAtHostRoot root |> get
        let identity = scope()
        let cmd = command identity
        let original = intent identity ManagedCommand cmd (Guid.NewGuid())
        let stores = [| first; other |]
        let invoked = ref 0
        let results = Array.zeroCreate<Result<Result<int,string>,string>> 2
        Parallel.For(0, 2, fun index ->
            results.[index] <-
                execute { Journal = stores.[index]; TrustedScope = identity; Intent = original } cmd
                    (fun () -> task {
                        System.Threading.Interlocked.Increment invoked |> ignore
                        return (Ok 1 : Result<int,string>)
                    })) |> ignore
        Assert.That(!invoked, Is.EqualTo 1)
        Assert.That(results |> Array.filter Result.isOk |> Array.length, Is.EqualTo 1)
        Assert.That(results |> Array.filter Result.isError |> Array.length, Is.EqualTo 1)
        Assert.That((inspectForHandoff other identity [ original.OriginalOperationId ]).RecoveryBlocked, Is.True))

[<Test>]
let altered_payload_checkout_project_ledger_and_kind_fail_before_dispatch () =
    withJournal (fun _ store ->
        let identity = scope()
        let cmd = command identity
        let original = intent identity ManagedCommand cmd (Guid.NewGuid())
        let mutable invoked = 0
        let fake = { Journal = store; TrustedScope = identity; Intent = original }
        let check blockedCommand forged =
            denied (execute { fake with Intent = forged } blockedCommand (fun () -> task {
                invoked <- invoked + 1
                return (Ok 1 : Result<int,string>)
            }))
        let otherPayload =
            match cmd with
            | RunCommandCommand c -> RunCommandCommand { c with Args = [ "push"; "--force" ] }
            | _ -> failwith "fixture"
        check otherPayload original
        check cmd { original with Scope = { identity with CheckoutId = "wq03-other-launch-g4" } }
        check cmd { original with Scope = { identity with ProjectId = Guid.NewGuid() } }
        check cmd { original with Scope = { identity with LedgerId = Guid.NewGuid() } }
        check cmd { original with Kind = ManagedJob }
        Assert.That(invoked, Is.Zero))

[<Test>]
let uninstrumented_route_cannot_execute_even_with_plausible_intent () =
    withJournal (fun _ store ->
        let identity = scope()
        let cmd = CreateWorktreeCommand
                    { ProjectName = selector identity; Name = "fake"; Branch = "fake"; BaseRef = "main" }
        let candidate = intent identity ManagedCommand cmd (Guid.NewGuid())
        let mutable called = false
        denied (execute { Journal = store; TrustedScope = identity; Intent = candidate } cmd (fun () -> task {
            called <- true
            return (Ok() : Result<unit,string>)
        }))
        Assert.That(called, Is.False))
