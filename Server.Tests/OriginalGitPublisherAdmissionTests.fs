module ServerOriginalGitPublisherAdmissionTests

open System
open System.Diagnostics
open System.IO
open NUnit.Framework
open Jarvis.WorkQueues

let private shaA = String.replicate 64 "a"
let private shaB = String.replicate 64 "b"
let private oidA = String.replicate 40 "a"
let private account = Guid.Parse "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"
let private project = Guid.Parse "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"
let private ledger = Guid.Parse "cccccccc-cccc-4ccc-8ccc-cccccccccccc"
let private publisher = Guid.Parse "dddddddd-dddd-4ddd-8ddd-dddddddddddd"
let private device = Guid.Parse "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee"
let private operation = Guid.Parse "ffffffff-ffff-4fff-8fff-ffffffffffff"

let private projectTarget =
    { Purpose = OriginalGitRefPurpose.ProjectLedger
      AccountId = account; ProjectId = Some project
      LedgerId = ledger; LedgerEpoch = 1
      CanonicalRepositoryId = shaA
      ApprovedRemoteFingerprintSha256 = shaB
      GitRef = "refs/heads/jarvis-orchestration" }

let private accountTarget =
    { projectTarget with
        Purpose = OriginalGitRefPurpose.AccountLedger
        ProjectId = None
        GitRef = "refs/heads/jarvis-account-admission" }

let private publisherIdentity =
    { PublisherId = publisher; DeviceId = device
      ProtocolVersion = 1; CredentialGeneration = 1L }

let private cas =
    { Target = projectTarget; Publisher = publisherIdentity
      OperationId = operation; RequestSha256 = shaA
      ExpectedRemoteHead = Some oidA; ProposedRemoteHead = Some (String.replicate 40 "b")
      SourceEffectOperationId = None }

let private invalid = function
    | Error(OriginalGitPublisherFailure.InvalidEnvelope _) -> true
    | _ -> false

[<Test>]
let ``valid project and account envelopes remain only syntactic evidence`` () =
    Assert.That(OriginalGitPublisherAdmission.validateTarget projectTarget = Ok (), Is.True)
    Assert.That(OriginalGitPublisherAdmission.validateTarget accountTarget = Ok (), Is.True)
    Assert.That(OriginalGitPublisherAdmission.validateCas cas = Ok (), Is.True)

[<Test>]
let ``invalid credential epoch repository head and UUID fail closed`` () =
    Assert.That(invalid (OriginalGitPublisherAdmission.validateCas
        { cas with Publisher = { publisherIdentity with CredentialGeneration = 0L } }), Is.True)
    Assert.That(invalid (OriginalGitPublisherAdmission.validateCas
        { cas with Target = { projectTarget with LedgerEpoch = 0 } }), Is.True)
    Assert.That(invalid (OriginalGitPublisherAdmission.validateCas
        { cas with Target = { projectTarget with ApprovedRemoteFingerprintSha256 = "caller" } }), Is.True)
    Assert.That(invalid (OriginalGitPublisherAdmission.validateCas
        { cas with ExpectedRemoteHead = Some "not-an-oid" }), Is.True)
    Assert.That(invalid (OriginalGitPublisherAdmission.validateCas
        { cas with OperationId = Guid.Empty }), Is.True)
    Assert.That(invalid (OriginalGitPublisherAdmission.validateCas
        { cas with ExpectedRemoteHead = None; ProposedRemoteHead = None }), Is.True)

[<Test>]
let ``source effects require a stable original effect ID and safe ref`` () =
    let source = { projectTarget with
                    Purpose = OriginalGitRefPurpose.SourceEffect
                    GitRef = "refs/heads/jarvis-review/task/1234" }
    Assert.That(invalid (OriginalGitPublisherAdmission.validateCas
        { cas with Target = source }), Is.True)
    Assert.That(OriginalGitPublisherAdmission.validateCas
        { cas with Target = source; SourceEffectOperationId = Some(Guid.NewGuid()) } = Ok (), Is.True)
    Assert.That(invalid (OriginalGitPublisherAdmission.validateTarget
        { source with GitRef = "refs/heads/jarvis-review/../escape" }), Is.True)
    Assert.That(invalid (OriginalGitPublisherAdmission.validateTarget
        { source with GitRef = "refs/heads/forged.lock" }), Is.True)

[<Test>]
let ``barrier envelope requires distinct canonical account and project authorities`` () =
    let fixture =
        { FenceOperationId = operation; OperatorDecisionId = Guid.NewGuid()
          AccountId = account; Targets = [accountTarget; projectTarget] }
    Assert.That(OriginalGitPublisherAdmission.validateFenceRequest fixture = Ok (), Is.True)
    Assert.That(invalid (OriginalGitPublisherAdmission.validateFenceRequest
        { fixture with Targets = [projectTarget] }), Is.True)
    Assert.That(invalid (OriginalGitPublisherAdmission.validateFenceRequest
        { fixture with Targets = [accountTarget] }), Is.True)
    Assert.That(invalid (OriginalGitPublisherAdmission.validateFenceRequest
        { fixture with Targets = [accountTarget; projectTarget; projectTarget] }), Is.True)
    Assert.That(invalid (OriginalGitPublisherAdmission.validateFenceRequest
        { fixture with Targets = [accountTarget; {projectTarget with AccountId = Guid.NewGuid()}] }), Is.True)

// A necessary real-Git negative test. This is NOT a positive gateway test:
// installing a hook is not serialized with an in-flight push, and a caller
// with bare-repository filesystem access can bypass receive-pack entirely.
let private git (directory: string) (arguments: string list) =
    let info = ProcessStartInfo("git")
    info.WorkingDirectory <- directory
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    for arg in arguments do info.ArgumentList.Add(arg)
    use gitProcess = Process.Start(info)
    let stdout = gitProcess.StandardOutput.ReadToEndAsync()
    let stderr = gitProcess.StandardError.ReadToEndAsync()
    if not (gitProcess.WaitForExit 30000) then
        gitProcess.Kill(true)
        failwithf "git timed out: %A" arguments
    gitProcess.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult()

let private succeeds directory arguments =
    let code, output, error = git directory arguments
    Assert.That(code, Is.EqualTo(0), sprintf "git %A failed: %s %s" arguments output error)

let private configuredClone root remote name =
    let path = Path.Combine(root, name)
    succeeds root ["clone"; remote; path]
    succeeds path ["config"; "user.name"; "fixture"]
    succeeds path ["config"; "user.email"; "fixture@example.invalid"]
    path

let private setupRemote root name protectedRef =
    let remote = Path.Combine(root, name + ".git")
    succeeds root ["init"; "--bare"; "--initial-branch=main"; remote]
    let seed = configuredClone root remote (name + "-seed")
    succeeds seed ["commit"; "--allow-empty"; "-m"; "seed"]
    succeeds seed ["push"; "origin"; "HEAD:refs/heads/main"; "HEAD:" + protectedRef]
    let stale = configuredClone root remote (name + "-stale")
    succeeds stale ["commit"; "--allow-empty"; "-m"; "still-valid-before-fence"]
    // This direct push proves the stale path is genuinely capable of writing
    // without a Jarvis SDK or cooperative client check BEFORE enforcement.
    succeeds stale ["push"; "origin"; "HEAD:" + protectedRef]
    let observed, _, _ = git root ["--git-dir=" + remote; "rev-parse"; protectedRef]
    Assert.That(observed, Is.EqualTo(0))
    let _, head, _ = git root ["--git-dir=" + remote; "rev-parse"; protectedRef]
    remote, stale, head.Trim()

let private installRemoteRejectHook remote =
    let hook = Path.Combine(remote, "hooks", "pre-receive")
    File.WriteAllText(hook, "#!/bin/sh\necho ORIGINAL_REMOTE_FENCED >&2\nexit 1\n")
    File.SetUnixFileMode(hook,
        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

[<Test>]
let ``disposable account and two project remotes reject stale push at receive-pack`` () =
    if not (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) then
        Assert.Ignore("POSIX Git pre-receive hook fixture")
    let root = Path.Combine(Path.GetTempPath(), "jarvis-original-publisher-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(root) |> ignore
    try
        let scopes =
            [ "project-one", "refs/heads/jarvis-orchestration"
              "project-two", "refs/heads/jarvis-orchestration"
              "account", "refs/heads/jarvis-account-admission" ]
        let active =
            scopes |> List.map (fun (name, protectedRef) ->
                let remote, stale, head = setupRemote root name protectedRef
                installRemoteRejectHook remote
                remote, stale, protectedRef, head)
        for remote, stale, protectedRef, previousHead in active do
            succeeds stale ["commit"; "--allow-empty"; "-m"; "post-fence-stale-push"]
            let code, _, error = git stale ["push"; "origin"; "HEAD:" + protectedRef]
            Assert.That(code, Is.Not.EqualTo(0), error)
            Assert.That(error, Does.Contain("ORIGINAL_REMOTE_FENCED"))
            let exit, output, _ = git root ["--git-dir=" + remote; "rev-parse"; protectedRef]
            Assert.That(exit, Is.EqualTo(0))
            Assert.That(output.Trim(), Is.EqualTo(previousHead))
        // A stale publisher must not escape through an alternate source ref.
        let projectRemote, projectStale, _, _ = active.Head
        let code, _, error = git projectStale ["push"; "origin"; "HEAD:refs/heads/jarvis-review/unregistered/alternate"]
        Assert.That(code, Is.Not.EqualTo(0), error)
        Assert.That(error, Does.Contain("ORIGINAL_REMOTE_FENCED"))
        let exit, _, _ = git root ["--git-dir=" + projectRemote; "show-ref"; "--verify"; "refs/heads/jarvis-review/unregistered/alternate"]
        Assert.That(exit, Is.Not.EqualTo(0))
    finally
        if Directory.Exists root then Directory.Delete(root, true)
