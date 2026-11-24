module SystemPropertyAccountFixture

open System
open System.IO
open System.Text.Json
open Client.AccountCapacityLedger
open Client.LedgerStore
open Client.NativeSchedulerRosterPolicy
open Client.NativeSchedulerAccountReservation
open Client.ReviewWorkflow
open AutomationHealthHostedFixture

type AccountFixture =
    { Root: string
      Accounts: AccountBinding array
      Projects: LedgerBinding array array }
    interface IDisposable with
        member _.Dispose() = ()

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
    { Root = root; Accounts = bindings; Projects = projects }

let approve f client previous accountLimit projectLimit =
    let b = f.Accounts[client]
    let projects = f.Projects[client]
    let policy : NativeRosterPolicy =
        { SchemaVersion = 1; AccountId = string b.AccountId; AccountLedgerId = string b.LedgerId
          AccountEpoch = b.Epoch
          AccountAuthoritySha256 = NativeSchedulerRosterPolicy.accountAuthoritySha256
              (string b.AccountId) (string b.LedgerId) b.Epoch b.ApprovedFetchUrl b.ApprovedPushUrl
          Version = previous + 1L; MembershipComplete = true
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
    let update =
        { SchemaVersion = 1; OperationId = string (Guid.NewGuid()); Actor = "Disposable property operator"
          PreviousVersion = previous; ExpectedAccountHead = (AccountCapacityLedger.fetch b |> success).Head
          Policy = policy }
    AccountCapacityLedger.setNativeRosterPolicy b projects update

let coverage f client =
    let p = AccountCapacityLedger.readNativeRosterPolicy f.Accounts[client] f.Projects[client]
    if p.Status <> "PolicyVerified" then failwithf "No complete disposable roster: %A" p.HoldReasons
    { AccountHead = p.VerifiedAccountHead.Value; ProjectHeads = p.VerifiedProjectHeads
      ObservedAtUtc = DateTimeOffset.UtcNow }

let read f client =
    NativeSchedulerAccountReservation.readVerified f.Accounts[client] f.Projects[client] (coverage f client)
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
    NativeSchedulerAccountReservation.reserve
        f.Accounts[client] f.Projects[client] proof operation "Disposable system property" c

let reserve f client operation c =
    reserveWithCoverage f client (coverage f client) operation c

let claim f client project original candidate =
    let proof = coverage f client
    let cmd : Client.AutomationNativeAccountProjectIngress.StartedProjectClaim =
        { OperationId = Guid.NewGuid(); ExpectedProjectHead = (LedgerStore.fetch f.Projects.[client].[project] |> success).Commit
          AccountHead = proof.AccountHead; OriginalReservationOperationId = original
          Candidate = candidate; DeviceId = "disposable"; WindowRegistration = "disposable" }
    let result = Client.AutomationNativeAccountProjectIngress.claimStarted
                    f.Accounts[client] f.Projects[client] proof cmd
    cmd, result

let transition f client dispatch effect version status fence session =
    let record = (read f client).Records |> Array.find (fun r -> r.Candidate.DispatchId = string dispatch)
    let original = Guid.Parse record.OriginalOperationId
    let _, receipt = AccountCapacityLedger.readOperation f.Accounts[client] original |> success
    let locator : NativeOriginalReservationLocator =
        { ReservationOperationId = original; ReservationRequestSha256 = receipt.Value.RequestSha256
          Candidate = { record.Candidate with EffectOperationId = string effect }; ExpectedGeneration = version
          ExpectedProjectFenceOid = if version = 1L then "" else record.ProjectFenceOid
          ExpectedProviderSessionId = record.ProviderSessionId }
    NativeSchedulerAccountReservation.transition f.Accounts[client] f.Projects[client] (coverage f client)
        (Guid.NewGuid()) "Disposable system property" locator status fence session

let denyRelease f client original (c: NativeDispatchCandidate) =
    let locator : NativeTerminalLocator =
        { DispatchId = Guid.Parse c.DispatchId; OriginalReservationId = original
          ReservationRequestSha256 = ((AccountCapacityLedger.readOperation f.Accounts[client] (Guid.Parse ((read f client).Records |> Array.find (fun r -> r.Candidate.DispatchId = c.DispatchId)).OriginalOperationId) |> success |> snd).Value.RequestSha256)
          ExpectedGeneration = ((read f client).Records |> Array.find (fun r -> r.Candidate.DispatchId = c.DispatchId)).Generation
          EffectOperationId = Guid.Parse c.EffectOperationId; TerminalOperationId = Guid.NewGuid() }
    let verifier =
        { new INativeTerminalVerifier with
            member _.VerifyCurrentTerminal _ = Error "No original terminal provider observation" }
    NativeSchedulerAccountReservation.release
        f.Accounts[client] f.Projects[client] (coverage f client)
        verifier (Guid.NewGuid()) "Disposable system property" locator

let forgeTerminal f client project original (c: NativeDispatchCandidate) =
    let record = (read f client).Records |> Array.find (fun r -> r.Candidate.DispatchId = c.DispatchId)
    let binding = f.Projects.[client].[project]
    let head = (LedgerStore.fetch binding |> success).Commit
    let locator : NativeTerminalLocator =
        { DispatchId = Guid.Parse c.DispatchId; OriginalReservationId = original
          ReservationRequestSha256 = ((AccountCapacityLedger.readOperation f.Accounts[client] (Guid.Parse ((read f client).Records |> Array.find (fun r -> r.Candidate.DispatchId = c.DispatchId)).OriginalOperationId) |> success |> snd).Value.RequestSha256)
          ExpectedGeneration = ((read f client).Records |> Array.find (fun r -> r.Candidate.DispatchId = c.DispatchId)).Generation
          EffectOperationId = Guid.Parse c.EffectOperationId; TerminalOperationId = Guid.NewGuid() }
    // All IDs and apparent proof fields match; no immutable terminal receipt
    // exists. Even a positive disposable verifier cannot invent that receipt.
    let verifier =
        { new INativeTerminalVerifier with
            member _.VerifyCurrentTerminal l =
                Ok { DispatchId = l.DispatchId; OriginalReservationId = l.OriginalReservationId
                     EffectOperationId = l.EffectOperationId; TerminalOperationId = l.TerminalOperationId
                     ProjectId = binding.ProjectId; ProjectLedgerId = binding.LedgerId; ProjectEpoch = binding.Epoch
                     CanonicalKey = record.CanonicalKey; ProviderSessionId = record.ProviderSessionId
                     ProjectHead = head; ProjectTerminalOid = head; ProviderTerminalOid = head } }
    NativeSchedulerAccountReservation.release
        f.Accounts[client] f.Projects[client] (coverage f client)
        verifier (Guid.NewGuid()) "Disposable forged terminal control" locator

// Pre-claim exclusion commits through the actual project CAS and account
// consumer. The locator comes from the immutable original reserve receipt.
let excludeBeforeClaim f client project originalId (candidate: NativeDispatchCandidate) =
    let b = f.Accounts[client]
    let projects = f.Projects[client]
    let binding = projects[project]
    let proof = coverage f client
    let _, receipt = AccountCapacityLedger.readOperation b originalId |> success
    let locator : NativeOriginalReservationLocator =
        { ReservationOperationId = originalId; ReservationRequestSha256 = receipt.Value.RequestSha256
          Candidate = candidate; ExpectedGeneration = 1L; ExpectedProjectFenceOid = ""; ExpectedProviderSessionId = "" }
    let context = NativeSchedulerAccountReservation.readOriginal b projects proof locator |> success
    let identity : Client.NativeProjectEffectTransition.OriginalEffectIdentity =
        { AccountId = b.AccountId; AccountLedgerId = b.LedgerId; AccountEpoch = b.Epoch
          ReservationOperationId = originalId; CanonicalKey = context.CurrentReservation.CanonicalKey
          Candidate = candidate }
    let operation = Guid.NewGuid()
    let evidence : NativeNoSendEvidence =
        { SchemaVersion = 1; State = "VerifiedNoSend"; AccountId = string b.AccountId
          AccountLedgerId = string b.LedgerId; AccountEpoch = b.Epoch
          ReservationOrigin = context.ReservationOrigin; CanonicalKey = identity.CanonicalKey
          Original = locator; NoSendOperationId = operation }
    let raw = NativeSchedulerAccountReservation.serializeNoSendEvidence evidence
    let head = (LedgerStore.fetch binding |> success).Commit
    let request = sprintf """{"schemaVersion":1,"expectedProjectHead":"%s","predecessor":{"kind":"BeforeClaim"},"evidence":%s}""" head raw
    let intent : LedgerIntent =
        { OperationId = operation; Actor = candidate.OwnerAgentId; Kind = "NativeVerifiedNoSend"; RequestJson = request }
    let path = NativeSchedulerAccountReservation.noSendFencePath candidate
    LedgerStore.transact binding intent (fun snapshot ->
        match NativeSchedulerAccountReservation.readOriginal b projects proof locator with
        | Ok verified when verified = context ->
            Client.NativeProjectEffectTransition.planExclusion binding snapshot head identity path
                Client.NativeProjectEffectTransition.BeforeClaim intent
        | _ -> Error (InvalidBinding "Original account reservation changed"))
    |> success |> ignore
    let snapshot = LedgerStore.fetch binding |> success
    let _, oid = LedgerStore.readAt binding snapshot path |> success
    { Original = locator; NoSendOperationId = operation; ProjectHead = snapshot.Commit
      NoSendOrigin = snapshot.Commit; EvidenceBlobOid = oid.Value }

let releaseNoSend f client operation (locator: NativeNoSendLocator) =
    NativeSchedulerAccountReservation.releaseNoSend f.Accounts[client] f.Projects[client]
        (coverage f client) operation "Disposable system property" locator
