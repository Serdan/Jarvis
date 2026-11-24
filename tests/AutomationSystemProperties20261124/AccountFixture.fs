module SystemPropertyAccountFixture

open System
open System.IO
open System.Security.Cryptography
open Client.AccountCapacityLedger
open Client.LedgerStore
open Client.NativeSchedulerRosterPolicy
open Client.NativeSchedulerAccountReservation
open Client.ReviewWorkflow
open AutomationHealthHostedFixture

type AccountFixture =
    { Root: string
      Accounts: AccountBinding array
      Signer: ECDsa
      Subject: string
      Projects: LedgerBinding array array }
    interface IDisposable with
        member f.Dispose() = f.Signer.Dispose()

let private privateDirectory path =
    Directory.CreateDirectory(path) |> ignore
    if not (OperatingSystem.IsWindows()) then
        File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

let create root projects =
    privateDirectory root
    File.WriteAllText(Path.Combine(root, ".wq10-disposable-fixture"), "WQ10_DISPOSABLE_FIXTURE_V1")
    let remote = Path.Combine(root, "account.git")
    git root ["init"; "--bare"; "--initial-branch=main"; remote] |> ignore
    let accounts =
        Array.init 2 (fun i ->
            let scope = Path.Combine(root, sprintf "wq10-private-system-%d" i)
            privateDirectory scope
            let local = Path.Combine(scope, "account")
            git root ["clone"; remote; local] |> ignore
            git local ["config"; "user.name"; "System property fixture"] |> ignore
            git local ["config"; "user.email"; "system@example.test"] |> ignore
            local)
    let a,l = Guid.NewGuid(),Guid.NewGuid()
    let bindings =
        accounts |> Array.map (fun path ->
            { RepositoryPath = path; RemoteName = "origin"
              ApprovedFetchUrl = remote; ApprovedPushUrl = remote
              AccountId = a; LedgerId = l; Epoch = 1 })
    AccountCapacityLedger.initialize bindings[0] 2 |> success |> ignore
    { Root = root; Accounts = bindings; Signer = ECDsa.Create(ECCurve.NamedCurves.nistP256)
      Subject = "auth0|automation-system-disposable"; Projects = projects }

let private key (f: AccountFixture) = f.Signer.ExportSubjectPublicKeyInfo()

let approve f client previous accountLimit projectLimit =
    let b = f.Accounts[client]
    let projects = f.Projects[client]
    let policy : NativeRosterPolicy =
        { SchemaVersion = 1; AccountId = string b.AccountId; AccountLedgerId = string b.LedgerId
          AccountEpoch = b.Epoch
          AccountAuthoritySha256 = NativeSchedulerRosterPolicy.accountAuthoritySha256
              (string b.AccountId) (string b.LedgerId) b.Epoch b.ApprovedFetchUrl b.ApprovedPushUrl
          AuthenticatedSubject = f.Subject; Version = previous + 1L; MembershipComplete = true
          AccountLimits = { Worker = accountLimit; Reviewer = 1 }
          Projects =
            projects |> Array.mapi (fun i p ->
                let authority =
                    { ProjectId = p.ProjectId; LedgerId = p.LedgerId; Epoch = p.Epoch
                      ApprovedFetchUrl = p.ApprovedFetchUrl; ApprovedPushUrl = p.ApprovedPushUrl }
                { ProjectId = string p.ProjectId; Alias = sprintf "Property project %d" i
                  LedgerId = string p.LedgerId; Epoch = p.Epoch
                  SourceRef = NativeSchedulerRosterPolicy.ProjectSourceRef
                  RemoteAuthoritySha256 = AccountCapacityLedger.projectAuthoritySha256 authority
                  Limits = { Worker = projectLimit; Reviewer = 1 } }) }
    let approval : NativeSignedRosterApproval =
        { SchemaVersion = 1; OperationId = string (Guid.NewGuid()); Actor = "Disposable property operator"
          PreviousVersion = previous; ExpectedAccountHead = (AccountCapacityLedger.fetch b |> success).Head
          Policy = policy; SignerPublicKeySha256 = NativeSchedulerRosterPolicy.keySha256 (key f)
          SignatureBase64 = "" }
    let signed =
        { approval with SignatureBase64 = Convert.ToBase64String(f.Signer.SignData(
                            NativeSchedulerRosterPolicy.approvalMessage approval, HashAlgorithmName.SHA256)) }
    AccountCapacityLedger.setVerifiedDisposableNativeRosterPolicy f.Root b (key f) f.Subject projects signed

let coverage f client =
    let p = AccountCapacityLedger.readVerifiedDisposableNativeRosterPolicy
                f.Root f.Accounts[client] (key f) f.Subject f.Projects[client]
    if p.Status <> "PolicyVerified" then failwithf "No complete disposable roster: %A" p.HoldReasons
    { AccountHead = p.VerifiedAccountHead.Value; ProjectHeads = p.VerifiedProjectHeads
      ObservedAtUtc = DateTimeOffset.UtcNow; Complete = true }

let read f client =
    NativeSchedulerAccountReservation.readVerifiedDisposable f.Root f.Accounts[client] (key f) f.Subject f.Projects[client] (coverage f client)
    |> success |> snd

let candidate (binding: LedgerBinding) taskId =
    let snapshot = LedgerStore.fetch binding |> success
    let _, a = ReviewWorkflow.getTaskAt binding snapshot taskId |> success
    let a = a.Value
    let raw, _ = LedgerStore.readAt binding snapshot (sprintf "tasks/%s/attempts/%s.json" taskId a.AttemptId) |> success
    { DispatchId = string (Guid.NewGuid()); EffectOperationId = string (Guid.NewGuid()); Kind = "Worker"
      ProjectId = string binding.ProjectId; ProjectLedgerId = string binding.LedgerId; ProjectEpoch = binding.Epoch
      OwnerAgentId = a.OwnerAgentId; TaskId = taskId; AttemptId = a.AttemptId
      AssignmentGeneration = int64 a.AssignmentGeneration
      SourceRepositoryId = a.SourceRepositoryId; SourceBaseOid = a.BaseCommit; SourceRef = a.SourceRef
      SourceLeaseSha256 = NativeSchedulerRosterPolicy.sha256 raw.Value
      ReviewId = ""; ReviewVersion = 0L; ReviewedHeadOid = ""; ReviewRef = ""; ClaimOperationId = "" }

let reserveWithCoverage f client proof operation c =
    NativeSchedulerAccountReservation.reserveVerifiedDisposable
        f.Root f.Accounts[client] (key f) f.Subject f.Projects[client] proof operation "Disposable system property" c

let reserve f client operation c =
    reserveWithCoverage f client (coverage f client) operation c

let claim f client project original candidate =
    let proof = coverage f client
    let cmd : Client.AutomationNativeAccountProjectIngress.StartedProjectClaim =
        { OperationId = Guid.NewGuid(); ExpectedProjectHead = (LedgerStore.fetch f.Projects.[client].[project] |> success).Commit
          AccountHead = proof.AccountHead; OriginalReservationOperationId = original
          Candidate = candidate; DeviceId = "disposable"; WindowRegistration = "disposable" }
    let result = Client.AutomationNativeAccountProjectIngress.claimVerifiedDisposable
                    f.Root f.Accounts[client] (key f) f.Subject f.Projects[client] proof cmd
    cmd, result

let transition f client dispatch effect version status fence session =
    NativeSchedulerAccountReservation.transitionVerifiedDisposable
        f.Root f.Accounts[client] (key f) f.Subject f.Projects[client] (coverage f client)
        (Guid.NewGuid()) "Disposable system property" dispatch effect version status fence session

let denyRelease f client original (c: NativeDispatchCandidate) =
    let locator : NativeTerminalLocator =
        { DispatchId = Guid.Parse c.DispatchId; OriginalReservationId = original
          EffectOperationId = Guid.Parse c.EffectOperationId; TerminalOperationId = Guid.NewGuid() }
    let verifier =
        { new INativeDisposableTerminalVerifier with
            member _.VerifyCurrentTerminal _ = Error "No original terminal provider observation" }
    NativeSchedulerAccountReservation.releaseVerifiedDisposable
        f.Root f.Accounts[client] (key f) f.Subject f.Projects[client] (coverage f client)
        verifier (Guid.NewGuid()) "Disposable system property" locator

let forgeTerminal f client project original (c: NativeDispatchCandidate) =
    let record = (read f client).Records |> Array.find (fun r -> r.Candidate.DispatchId = c.DispatchId)
    let binding = f.Projects.[client].[project]
    let head = (LedgerStore.fetch binding |> success).Commit
    let locator : NativeTerminalLocator =
        { DispatchId = Guid.Parse c.DispatchId; OriginalReservationId = original
          EffectOperationId = Guid.Parse c.EffectOperationId; TerminalOperationId = Guid.NewGuid() }
    // All IDs and apparent proof fields match; no immutable terminal receipt
    // exists. Even a positive disposable verifier cannot invent that receipt.
    let verifier =
        { new INativeDisposableTerminalVerifier with
            member _.VerifyCurrentTerminal l =
                Ok { DispatchId = l.DispatchId; OriginalReservationId = l.OriginalReservationId
                     EffectOperationId = l.EffectOperationId; TerminalOperationId = l.TerminalOperationId
                     ProjectId = binding.ProjectId; ProjectLedgerId = binding.LedgerId; ProjectEpoch = binding.Epoch
                     CanonicalKey = record.CanonicalKey; ProviderSessionId = record.ProviderSessionId
                     ProjectHead = head; ProjectTerminalOid = head; ProviderTerminalOid = head } }
    NativeSchedulerAccountReservation.releaseVerifiedDisposable
        f.Root f.Accounts[client] (key f) f.Subject f.Projects[client] (coverage f client)
        verifier (Guid.NewGuid()) "Disposable forged terminal control" locator
