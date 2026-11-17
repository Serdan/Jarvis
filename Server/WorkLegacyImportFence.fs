namespace Jarvis.WorkQueues

open System
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Server

// C2 is a deliberately unhosted protocol boundary. It cannot fence a live
// original Git writer without an independently authenticated gateway which
// performs the original writer's durable admission fence and Git CAS.
// Neither these records nor a caller's boolean can authorize live cutover.
[<RequireQualifiedAccess>]
type WorkLegacyGitScope =
    | Account
    | Project

type WorkLegacyGitBinding =
    { Scope: WorkLegacyGitScope
      EntityId: Guid
      LedgerId: Guid
      LedgerEpoch: int
      DeviceId: Guid
      TrustedRemoteId: string
      PinnedHead: string
      SourceHighWater: int64 }

type WorkLegacyFenceApproval =
    { DecisionId: Guid
      DecisionVersion: int64
      DeviceId: Guid
      AccountId: Guid
      ProjectIds: Guid list
      FenceOperationId: Guid
      ImportOperationId: Guid }

type WorkLegacyFencePlan =
    { Account: WorkLegacyGitBinding
      Projects: WorkLegacyGitBinding list
      Approval: WorkLegacyFenceApproval }

type WorkLegacyGitFenceReceipt =
    { Binding: WorkLegacyGitBinding
      DecisionId: Guid
      DecisionVersion: int64
      FenceOperationId: Guid
      ExclusiveReceiptId: Guid
      OriginalHead: string
      FencedHead: string
      OriginalHighWater: int64
      FenceHighWater: int64
      WriterGeneration: int64
      NoNewOriginalMutations: bool
      DurableUntilExplicitCutover: bool
      OriginalPendingIntentHighWater: int64
      OriginalSourceEffectHighWater: int64
      MinimumClientProtocolVersion: int
      AllOriginalClientsQuiesced: bool
      AllClientQuiescenceProofSha256: string
      AuditSha256: string }

/// Production implementation MUST authenticate exact account/project/device,
/// verify an operator decision from its original authority, acquire an
/// exclusive durable writer-admission barrier, and perform an original-Git CAS
/// from PinnedHead to FencedHead. The barrier must reject *all* old-writer
/// operations (including other clients and stale/retried operations) until
/// separately authorized cutover or recovery. Unknown effects are errors.
/// VerifyDurableFence must independently reread original remote source and
/// durable admission-barrier evidence, all-client protocol deployment and
/// high-water proofs for pending intents/source effects, not echo caller data.
/// No valid witness means NO-GO, even when Git CAS itself succeeded.
/// There is intentionally NO production gateway/host binding in this phase.
type IWorkLegacyOriginalGitFenceGateway =
    abstract member VerifyOperatorDecision:
        WorkLegacyFenceApproval -> Result<WorkLegacyFenceApproval, string>
    abstract member ReadTrustedBinding:
        WorkLegacyGitBinding -> Result<WorkLegacyGitBinding, string>
    abstract member AcquireExclusiveBarrier:
        WorkLegacyFenceApproval -> Result<Guid, string>
    abstract member CasFenceOriginalWriter:
        WorkLegacyGitBinding * WorkLegacyFenceApproval * Guid
            -> Result<WorkLegacyGitFenceReceipt, string>
    abstract member VerifyDurableFence:
        WorkLegacyGitBinding * WorkLegacyGitFenceReceipt
            -> Result<WorkLegacyGitFenceReceipt, string>

[<RequireQualifiedAccess>]
type WorkLegacyFenceFailure =
    | UnsafeDestination of string
    | PreflightRejected of WorkLegacyImportGap list
    | InvalidPlan of string
    | GatewayFailure of string
    | InvalidFenceReceipt of string
    | ImportRejected of WorkLegacyImportSqliteGap

type WorkLegacyFencedOfflineReceipt =
    { Import: WorkLegacyImportOfflineReceipt
      ApprovalId: Guid
      OriginalWriterFence: WorkLegacyGitFenceReceipt list }

module internal WorkLegacyImportFence =
    let private oid = Regex("^[0-9a-f]{40}([0-9a-f]{24})?$", RegexOptions.Compiled)
    let private sha256 = Regex("^[0-9a-f]{64}$", RegexOptions.Compiled)
    let private valid (re: Regex) (value: string) =
        not (isNull value) && re.IsMatch value
    let private nonempty (value: string) = not (String.IsNullOrWhiteSpace value)

    let private label (binding: WorkLegacyGitBinding) =
        sprintf "%A/%O/%O" binding.Scope binding.EntityId binding.LedgerId

    // Avoid relying on a source-provided name: two same-named projects on
    // different devices are distinct authorities and must never alias.
    let private matchesPlan (inspection: WorkLegacyImportInspection)
                            (plan: WorkLegacyFencePlan) =
        let approval = plan.Approval
        let identities =
            [ plan.Account ] @ plan.Projects
        let validBinding binding =
            binding.EntityId <> Guid.Empty
            && binding.LedgerId <> Guid.Empty
            && binding.LedgerEpoch > 0
            && binding.DeviceId <> Guid.Empty
            && binding.DeviceId = approval.DeviceId
            && nonempty binding.TrustedRemoteId
            && valid oid binding.PinnedHead
            && binding.SourceHighWater >= 0L
            && binding.SourceHighWater < Int64.MaxValue
        let boundAccount =
            plan.Account.Scope = WorkLegacyGitScope.Account
            && plan.Account.EntityId = inspection.Account.AccountId
            && plan.Account.LedgerId = inspection.Account.LedgerId
            && plan.Account.LedgerEpoch = inspection.Account.LedgerEpoch
            && plan.Account.PinnedHead = inspection.Account.Head.PinnedCommit
            && plan.Account.SourceHighWater = inspection.Account.Head.SourceHighWater
        let boundProjects =
            let inspected =
                inspection.Projects
                |> List.sortBy (fun project -> project.ProjectId)
                |> List.map (fun project ->
                    project.ProjectId, project.LedgerId, project.LedgerEpoch,
                    project.Head.PinnedCommit, project.Head.SourceHighWater)
            let supplied =
                plan.Projects
                |> List.sortBy (fun project -> project.EntityId)
                |> List.map (fun project ->
                    project.EntityId, project.LedgerId, project.LedgerEpoch,
                    project.PinnedHead, project.SourceHighWater)
            supplied = inspected
            && (plan.Projects |> List.forall (fun project ->
                project.Scope = WorkLegacyGitScope.Project))
        let approvedProjectIds =
            (approval.ProjectIds |> List.sort) =
                (inspection.Projects |> List.map (fun project -> project.ProjectId) |> List.sort)
        approval.DecisionId <> Guid.Empty
        && approval.DecisionVersion > 0L
        && approval.FenceOperationId <> Guid.Empty
        && approval.ImportOperationId <> Guid.Empty
        && approval.FenceOperationId <> approval.ImportOperationId
        && approval.AccountId = inspection.Account.AccountId
        && approval.DeviceId <> Guid.Empty
        && not inspection.Projects.IsEmpty
        && (identities |> List.forall validBinding)
        && boundAccount && boundProjects && approvedProjectIds

    let private verifiedReceipt (approval: WorkLegacyFenceApproval)
                                exclusiveReceiptId
                                (binding: WorkLegacyGitBinding)
                                (receipt: WorkLegacyGitFenceReceipt) =
        receipt.Binding = binding
        && receipt.DecisionId = approval.DecisionId
        && receipt.DecisionVersion = approval.DecisionVersion
        && receipt.FenceOperationId = approval.FenceOperationId
        && receipt.ExclusiveReceiptId = exclusiveReceiptId
        && receipt.OriginalHead = binding.PinnedHead
        && receipt.FencedHead <> binding.PinnedHead
        && valid oid receipt.FencedHead
        && receipt.OriginalHighWater = binding.SourceHighWater
        && receipt.FenceHighWater = binding.SourceHighWater + 1L
        && receipt.WriterGeneration > 0L
        && receipt.NoNewOriginalMutations
        && receipt.DurableUntilExplicitCutover
        && receipt.OriginalPendingIntentHighWater >= 0L
        && receipt.OriginalSourceEffectHighWater >= 0L
        && receipt.MinimumClientProtocolVersion > 0
        && receipt.AllOriginalClientsQuiesced
        && valid sha256 receipt.AllClientQuiescenceProofSha256
        && valid sha256 receipt.AuditSha256

    let private call phase (action: unit -> Result<'t, string>) :
                     Result<'t, WorkLegacyFenceFailure> =
        try
            action()
            |> Result.mapError (fun error ->
                WorkLegacyFenceFailure.GatewayFailure(phase + ": " + error))
        with _ ->
            Error(WorkLegacyFenceFailure.GatewayFailure(phase + ": unavailable/unknown outcome"))

    let private traverse f items =
        let rec loop acc = function
            | [] -> Ok(List.rev acc)
            | item :: rest ->
                match f item with
                | Error error -> Error error
                | Ok value -> loop (value :: acc) rest
        loop [] items

    let private auditMutation (plan: WorkLegacyFencePlan)
                              (receipts: WorkLegacyGitFenceReceipt list) =
        let a = JsonObject()
        let approval = plan.Approval
        a.Add("SchemaVersion", JsonValue.Create(1))
        a.Add("Mode", JsonValue.Create("c2-disposable-offline-only"))
        a.Add("DecisionId", JsonValue.Create(approval.DecisionId.ToString("D")))
        a.Add("DecisionVersion", JsonValue.Create(approval.DecisionVersion))
        a.Add("AccountId", JsonValue.Create(approval.AccountId.ToString("D")))
        a.Add("DeviceId", JsonValue.Create(approval.DeviceId.ToString("D")))
        a.Add("FenceOperationId", JsonValue.Create(approval.FenceOperationId.ToString("D")))
        a.Add("ImportOperationId", JsonValue.Create(approval.ImportOperationId.ToString("D")))
        let ledgers = JsonArray()
        for receipt in receipts do
            let binding = receipt.Binding
            let l = JsonObject()
            l.Add("Scope", JsonValue.Create(sprintf "%A" binding.Scope))
            l.Add("EntityId", JsonValue.Create(binding.EntityId.ToString("D")))
            l.Add("LedgerId", JsonValue.Create(binding.LedgerId.ToString("D")))
            l.Add("LedgerEpoch", JsonValue.Create(binding.LedgerEpoch))
            l.Add("DeviceId", JsonValue.Create(binding.DeviceId.ToString("D")))
            l.Add("TrustedRemoteId", JsonValue.Create(binding.TrustedRemoteId))
            l.Add("OriginalHead", JsonValue.Create(receipt.OriginalHead))
            l.Add("FencedHead", JsonValue.Create(receipt.FencedHead))
            l.Add("FenceHighWater", JsonValue.Create(receipt.FenceHighWater))
            l.Add("WriterGeneration", JsonValue.Create(receipt.WriterGeneration))
            l.Add("ExclusiveReceiptId", JsonValue.Create(receipt.ExclusiveReceiptId.ToString("D")))
            l.Add("AuditSha256", JsonValue.Create(receipt.AuditSha256))
            l.Add("NoNewOriginalMutations", JsonValue.Create(receipt.NoNewOriginalMutations))
            l.Add("DurableUntilExplicitCutover", JsonValue.Create(receipt.DurableUntilExplicitCutover))
            l.Add("OriginalPendingIntentHighWater", JsonValue.Create(receipt.OriginalPendingIntentHighWater))
            l.Add("OriginalSourceEffectHighWater", JsonValue.Create(receipt.OriginalSourceEffectHighWater))
            l.Add("MinimumClientProtocolVersion", JsonValue.Create(receipt.MinimumClientProtocolVersion))
            l.Add("AllOriginalClientsQuiesced", JsonValue.Create(receipt.AllOriginalClientsQuiesced))
            l.Add("AllClientQuiescenceProofSha256", JsonValue.Create(receipt.AllClientQuiescenceProofSha256))
            ledgers.Add(l)
        a.Add("OriginalGitFences", ledgers)
        { Key =
            { ProjectId = approval.AccountId.ToString("D")
              Kind = WorkSqliteRecordKind.Project
              EntityId = "legacy-import-c2-authority-audit" }
          ExpectedVersion = None
          Value =
            { BodyJson = a.ToJsonString()
              GraphVersion = 0L
              StageGeneration = 0L
              ClaimGeneration = 0L
              StopGeneration = 0L
              RoleVersion = 0L
              SourceSha = Some plan.Account.PinnedHead
              ReviewSha = None
              IntegrationSha = None
              ExternalEffect = WorkSqliteEffect.Prepared } }

    // Explicitly disposable only. Never called from host setup, CLI or
    // scheduler; it has no capability to activate production SQLite.
    // Failure after acquiring some original writer fences intentionally leaves
    // those fences durable for recovery; never silently releases them.
    let commitFencedOfflineFixture (gateway: IWorkLegacyOriginalGitFenceGateway)
                                   (databasePath: string)
                                   (inspection: WorkLegacyImportInspection)
                                   (plan: WorkLegacyFencePlan)
                                   : Result<WorkLegacyFencedOfflineReceipt, WorkLegacyFenceFailure> =
        if not (WorkLegacyImportSqlite.isDisposableFixturePath databasePath) then
            Error(WorkLegacyFenceFailure.UnsafeDestination databasePath)
        else
            match WorkLegacyImportPreflight.inspect inspection with
            | Error gaps -> Error(WorkLegacyFenceFailure.PreflightRejected gaps)
            | Ok _ when inspection.Projects |> List.exists (fun project ->
                project.Queue.Migration <> MigrationEpoch.Frozen) ->
                Error(WorkLegacyFenceFailure.InvalidPlan "All original projects must be Frozen.")
            | Ok _ when not (matchesPlan inspection plan) ->
                Error(WorkLegacyFenceFailure.InvalidPlan "Canonical account/project/device/head/decision mismatch.")
            | Ok _ ->
                let approval = plan.Approval
                let bindings = plan.Account :: (plan.Projects |> List.sortBy (fun p -> p.EntityId))
                let verified =
                    call "operator decision" (fun () -> gateway.VerifyOperatorDecision approval)
                    |> Result.bind (fun sourceApproval ->
                        if sourceApproval = approval then Ok ()
                        else Error(WorkLegacyFenceFailure.InvalidPlan "Verified approval is stale or mismatched."))
                let trusted =
                    verified
                    |> Result.bind (fun () ->
                        traverse (fun binding ->
                            call ("authenticated Git binding " + label binding)
                                (fun () -> gateway.ReadTrustedBinding binding)
                            |> Result.bind (fun observed ->
                                if observed = binding then Ok binding
                                else Error(WorkLegacyFenceFailure.InvalidPlan
                                    ("Untrusted original-Git binding/head/device: " + label binding))))
                            bindings)
                trusted
                |> Result.bind (fun trustedBindings ->
                    call "exclusive old-writer quiescence"
                        (fun () -> gateway.AcquireExclusiveBarrier approval)
                    |> Result.bind (fun exclusive ->
                        if exclusive = Guid.Empty then
                            Error(WorkLegacyFenceFailure.InvalidFenceReceipt "Empty exclusive barrier receipt.")
                        else
                            traverse (fun binding ->
                                call ("original Git CAS " + label binding)
                                    (fun () -> gateway.CasFenceOriginalWriter(binding, approval, exclusive))
                                |> Result.bind (fun receipt ->
                                    if verifiedReceipt approval exclusive binding receipt then Ok receipt
                                    else Error(WorkLegacyFenceFailure.InvalidFenceReceipt
                                        ("Unproved old-writer CAS: " + label binding))))
                                trustedBindings
                            |> Result.bind (fun receipts ->
                                // Verify every durable fence again after *all* CAS writes.
                                // The original admission gate must remain exclusive and
                                // prevent new mutations throughout the SQLite transaction.
                                traverse (fun receipt ->
                                    call ("durable original Git fence " + label receipt.Binding)
                                        (fun () -> gateway.VerifyDurableFence(receipt.Binding, receipt))
                                    |> Result.bind (fun observed ->
                                        if observed = receipt
                                           && verifiedReceipt approval exclusive receipt.Binding observed then
                                            Ok observed
                                        else
                                            Error(WorkLegacyFenceFailure.InvalidFenceReceipt
                                                ("Lost or changed original writer fence: " + label receipt.Binding))))
                                    receipts
                                |> Result.bind (fun finalReceipts ->
                                    let offlineFence =
                                        { AccountHead = plan.Account.PinnedHead
                                          ProjectHeads =
                                            plan.Projects
                                            |> List.map (fun project -> project.EntityId, project.PinnedHead)
                                          FenceVersion = approval.DecisionVersion
                                          FenceOperationId = approval.FenceOperationId }
                                    let audit = auditMutation plan finalReceipts
                                    WorkLegacyImportSqlite.commitOfflineFixtureWithAudit
                                        databasePath approval.ImportOperationId inspection offlineFence [audit]
                                    |> Result.mapError WorkLegacyFenceFailure.ImportRejected
                                    |> Result.map (fun imported ->
                                        { Import = imported
                                          ApprovalId = approval.DecisionId
                                          OriginalWriterFence = finalReceipts })))))
