namespace Jarvis.WorkQueues

open System
open System.Text.RegularExpressions

// Source-only, default-off contract for the ORIGINAL project/account Git
// publishers. This is deliberately not a client-side allow flag and is not
// registered as a production adapter. An implementation must own the remote
// CAS and exclusive barrier under one authenticated enforcement domain.
[<RequireQualifiedAccess>]
type OriginalGitRefPurpose =
    | ProjectLedger
    | AccountLedger
    | SourceEffect

type OriginalGitPublisherTarget =
    { Purpose: OriginalGitRefPurpose
      AccountId: Guid
      ProjectId: Guid option
      LedgerId: Guid
      LedgerEpoch: int
      CanonicalRepositoryId: string
      ApprovedRemoteFingerprintSha256: string
      GitRef: string }

type OriginalGitPublisherIdentity =
    { PublisherId: Guid
      DeviceId: Guid
      ProtocolVersion: int
      CredentialGeneration: int64 }

type OriginalGitPublisherCas =
    { Target: OriginalGitPublisherTarget
      Publisher: OriginalGitPublisherIdentity
      OperationId: Guid
      RequestSha256: string
      ExpectedRemoteHead: string option
      ProposedRemoteHead: string option
      SourceEffectOperationId: Guid option }

type OriginalGitPublisherFenceRequest =
    { FenceOperationId: Guid
      OperatorDecisionId: Guid
      AccountId: Guid
      Targets: OriginalGitPublisherTarget list }

[<RequireQualifiedAccess>]
type OriginalGitPublisherFailure =
    | InvalidEnvelope of string
    | UntrustedAuthority
    | UnknownCredentialChannel
    | IncorrectEpoch
    | Fenced
    | StaleRemoteHead
    | ReusedOperationId
    | UnresolvedOriginalEffect
    | OutcomeUnknown of Guid

// These are RECEIPTS from an enforcing remote publisher, not permission
// tokens to hand to an old client before an ordinary Git push.
type OriginalGitPublisherCasReceipt =
    { OriginalOperationId: Guid
      CanonicalRequestSha256: string
      Target: OriginalGitPublisherTarget
      OldHead: string option
      NewHead: string option
      AppliedBarrierEpoch: int64
      RemoteReceiptSha256: string }

type OriginalGitPublisherBarrierReceipt =
    { FenceOperationId: Guid
      FenceEpoch: int64
      ExactTargets: OriginalGitPublisherTarget list
      RemoteWitnessSha256: string
      OriginalPendingIntentHighWater: int64
      OriginalSourceEffectHighWater: int64 }

// Implementations must perform the remote CAS and barrier under the SAME
// serializing authority, including delayed and already-in-flight pushes.
// Reinspection must independently query that authority and Git remote refs.
// A local check followed by direct Git push does NOT implement this interface.
// No implementation is supplied or registered in production by this module.
type IOriginalGitPublisherAuthority =
    abstract member PublishCas:
        OriginalGitPublisherCas
            -> Result<OriginalGitPublisherCasReceipt, OriginalGitPublisherFailure>
    abstract member AcquireExclusiveBarrier:
        OriginalGitPublisherFenceRequest
            -> Result<OriginalGitPublisherBarrierReceipt, OriginalGitPublisherFailure>
    abstract member ReinspectDurableBarrier:
        OriginalGitPublisherBarrierReceipt
            -> Result<OriginalGitPublisherBarrierReceipt, OriginalGitPublisherFailure>

module OriginalGitPublisherAdmission =
    let private sha256 = Regex("^[0-9a-f]{64}$", RegexOptions.Compiled)
    let private oid = Regex("^[0-9a-f]{40}([0-9a-f]{24})?$", RegexOptions.Compiled)
    let private safeRef = Regex("^refs/(heads|tags)/[A-Za-z0-9][A-Za-z0-9._/-]*$", RegexOptions.Compiled)
    let private projectLedgerRef = "refs/heads/jarvis-orchestration"
    let private accountLedgerRef = "refs/heads/jarvis-account-admission"

    let private valid (pattern: Regex) (value: string) =
        not (String.IsNullOrWhiteSpace value) && pattern.IsMatch value

    let private validGitRef (value: string) =
        valid safeRef value
        && not (value.Contains("..", StringComparison.Ordinal))
        && not (value.Contains("//", StringComparison.Ordinal))
        && not (value.Contains("@{", StringComparison.Ordinal))
        && not (value.EndsWith("/", StringComparison.Ordinal))
        && not (value.EndsWith(".", StringComparison.Ordinal))
        && (value.Split('/') |> Array.forall (fun part ->
            part <> "." && part <> ".."
            && not (part.StartsWith(".", StringComparison.Ordinal))
            && not (part.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))))

    // Shape validation is necessary but NEVER a positive remote admission
    // decision. It cannot establish that a stale clone lost push credentials.
    let validateTarget (target: OriginalGitPublisherTarget) =
        if isNull (box target) then
            Error(OriginalGitPublisherFailure.InvalidEnvelope "TargetMissing")
        elif target.AccountId = Guid.Empty || target.LedgerId = Guid.Empty
             || target.LedgerEpoch <= 0 then
            Error(OriginalGitPublisherFailure.InvalidEnvelope "CanonicalLedgerIdentityInvalid")
        elif not (valid sha256 target.CanonicalRepositoryId)
             || not (valid sha256 target.ApprovedRemoteFingerprintSha256) then
            Error(OriginalGitPublisherFailure.InvalidEnvelope "RemoteIdentityInvalid")
        elif not (validGitRef target.GitRef) then
            Error(OriginalGitPublisherFailure.InvalidEnvelope "GitRefInvalid")
        else
            match target.Purpose, target.ProjectId with
            | OriginalGitRefPurpose.AccountLedger, None
                when target.GitRef = accountLedgerRef -> Ok ()
            | OriginalGitRefPurpose.ProjectLedger, Some id
                when id <> Guid.Empty
                     && target.GitRef = projectLedgerRef -> Ok ()
            | OriginalGitRefPurpose.SourceEffect, Some id
                when id <> Guid.Empty
                     && target.GitRef <> projectLedgerRef
                     && target.GitRef <> accountLedgerRef -> Ok ()
            | _ -> Error(OriginalGitPublisherFailure.InvalidEnvelope "ScopeOrRefMismatch")

    let validateCas (operation: OriginalGitPublisherCas) =
        if isNull (box operation) then
            Error(OriginalGitPublisherFailure.InvalidEnvelope "OperationMissing")
        else
            match validateTarget operation.Target with
            | Error error -> Error error
            | Ok () ->
                if operation.Publisher.PublisherId = Guid.Empty
                   || operation.Publisher.DeviceId = Guid.Empty
                   || operation.Publisher.ProtocolVersion <= 0
                   || operation.Publisher.CredentialGeneration <= 0L then
                    Error(OriginalGitPublisherFailure.InvalidEnvelope "PublisherIdentityInvalid")
                elif operation.OperationId = Guid.Empty
                     || not (valid sha256 operation.RequestSha256) then
                    Error(OriginalGitPublisherFailure.InvalidEnvelope "OperationIdentityInvalid")
                elif operation.ExpectedRemoteHead.IsNone
                     && operation.ProposedRemoteHead.IsNone then
                    Error(OriginalGitPublisherFailure.InvalidEnvelope "EmptyRefChange")
                elif operation.ExpectedRemoteHead
                     |> Option.exists (valid oid >> not) then
                    Error(OriginalGitPublisherFailure.InvalidEnvelope "ExpectedHeadInvalid")
                elif operation.ProposedRemoteHead
                     |> Option.exists (valid oid >> not) then
                    Error(OriginalGitPublisherFailure.InvalidEnvelope "ProposedHeadInvalid")
                elif operation.Target.Purpose = OriginalGitRefPurpose.SourceEffect
                     && operation.SourceEffectOperationId.IsNone then
                    Error(OriginalGitPublisherFailure.InvalidEnvelope "SourceEffectJournalIdentityMissing")
                elif operation.SourceEffectOperationId = Some Guid.Empty then
                    Error(OriginalGitPublisherFailure.InvalidEnvelope "SourceEffectJournalIdentityInvalid")
                else Ok ()

    let validateFenceRequest (request: OriginalGitPublisherFenceRequest) =
        if isNull (box request) || request.FenceOperationId = Guid.Empty
           || request.OperatorDecisionId = Guid.Empty || request.AccountId = Guid.Empty then
            Error(OriginalGitPublisherFailure.InvalidEnvelope "FenceIdentityInvalid")
        elif isNull (box request.Targets) || request.Targets.IsEmpty then
            Error(OriginalGitPublisherFailure.InvalidEnvelope "FenceTargetsMissing")
        else
            match request.Targets |> List.tryPick (fun target ->
                match validateTarget target with
                | Error error -> Some error
                | Ok () when target.AccountId <> request.AccountId ->
                    Some(OriginalGitPublisherFailure.InvalidEnvelope "CrossAccountTarget")
                | Ok () -> None) with
            | Some error -> Error error
            | None ->
                let distinct =
                    request.Targets
                    |> List.map (fun target ->
                        target.CanonicalRepositoryId,
                        target.GitRef)
                    |> List.distinct
                if distinct.Length <> request.Targets.Length then
                    Error(OriginalGitPublisherFailure.InvalidEnvelope "DuplicateFenceTarget")
                elif not (request.Targets
                          |> List.exists (fun target ->
                              target.Purpose = OriginalGitRefPurpose.AccountLedger)) then
                    Error(OriginalGitPublisherFailure.InvalidEnvelope "AccountAuthorityMissing")
                elif not (request.Targets
                          |> List.exists (fun target ->
                              target.Purpose = OriginalGitRefPurpose.ProjectLedger)) then
                    Error(OriginalGitPublisherFailure.InvalidEnvelope "ProjectAuthorityMissing")
                else Ok ()

    // This comparison is only an input-consistency check. The *enforcing*
    // publisher must obtain this inventory independently from its authenticated
    // remote/ref authority (never the CAS caller), including credential/bypass
    // coverage, before it can issue a positive admission or fence receipt.
    // A syntactically valid source ref is not thereby an authorized ref.
    let validateTargetAgainstTrustedInventory
        (trustedInventory: OriginalGitPublisherTarget list)
        (target: OriginalGitPublisherTarget) =
        match validateTarget target with
        | Error error -> Error error
        | Ok () ->
            if isNull (box trustedInventory) || trustedInventory.IsEmpty then
                Error OriginalGitPublisherFailure.UntrustedAuthority
            else
                let matches =
                    trustedInventory
                    |> List.filter (fun approved ->
                        approved.CanonicalRepositoryId = target.CanonicalRepositoryId
                        && approved.GitRef = target.GitRef)
                match matches with
                | [ approved ] when approved = target -> Ok ()
                | [ _ ] ->
                    Error(OriginalGitPublisherFailure.InvalidEnvelope "RefClassificationOrIdentityMismatch")
                | [] -> Error OriginalGitPublisherFailure.UntrustedAuthority
                | _ ->
                    Error(OriginalGitPublisherFailure.InvalidEnvelope "ConflictingTrustedRefInventory")

    let validateCasAgainstTrustedInventory trustedInventory operation =
        match validateCas operation with
        | Error error -> Error error
        | Ok () ->
            validateTargetAgainstTrustedInventory trustedInventory operation.Target

    let validateFenceRequestAgainstTrustedInventory trustedInventory request =
        match validateFenceRequest request with
        | Error error -> Error error
        | Ok () ->
            request.Targets
            |> List.tryPick (fun target ->
                match validateTargetAgainstTrustedInventory trustedInventory target with
                | Error error -> Some error
                | Ok () -> None)
            |> function
                | Some error -> Error error
                | None -> Ok ()
