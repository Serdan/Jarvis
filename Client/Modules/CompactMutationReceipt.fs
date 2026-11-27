module Client.CompactMutationReceipt

open System.Text.Json
open Common
open Client.OrchestrationApi.OperationResponse

// Candidate public presentation only. Do not replace Presented internally:
// wake callbacks and exact receipt readers require its canonical operation.
// GetOperationStatus remains the authoritative, exact audit lookup by UUID.
type OperationHeader =
    { SchemaVersion: int
      OperationId: string
      Sequence: int64
      Type: string
      Actor: string
      Path: string
      BlobOid: string
      Paths: string array
      RequestHash: string
      OriginalResultHash: string }

type PublicReceipt =
    { operation: OperationHeader
      // Verified current head; on replay this is NOT necessarily origin.
      head: string
      alreadyApplied: bool
      // Preserve all action-critical data, including unknown future fields,
      // without a second escaped copy. No result-schema redaction.
      originalResult: JsonElement
      notificationDelivery: Client.ReviewWorkflow.NotificationDelivery option }

type PublicChallengeCreated =
    { challengeId: string
      conditionIds: string list
      receipt: PublicReceipt }

let project (value: Presented) : PublicReceipt =
    let op = value.operation
    { operation =
        { SchemaVersion = op.SchemaVersion
          OperationId = op.OperationId
          Sequence = op.Sequence
          Type = op.Type
          Actor = op.Actor
          Path = op.Path
          BlobOid = op.BlobOid
          Paths = if isNull op.Paths then null else Array.copy op.Paths
          RequestHash = op.RequestHash
          OriginalResultHash = op.OriginalResultHash }
      head = value.head
      alreadyApplied = value.alreadyApplied
      originalResult = value.originalResult.Clone()
      notificationDelivery = value.notificationDelivery }

/// Only these two existing, typed result shapes are eligible. Other mutation
/// receipts (including publication, journal acknowledgments and recovery),
/// exact reads and arbitrary result shapes must use their original serializer.
/// Internal consumers must receive the original value before this projection.
let tryProject (value: obj) : obj option =
    match value with
    | :? Presented as receipt -> Some(box (project receipt))
    | :? ChallengeCreatedPresented as created ->
        Some(box ({ challengeId = created.challengeId
                    conditionIds = created.conditionIds
                    receipt = project created.receipt } : PublicChallengeCreated))
    | _ -> None

/// Pure adapter for a terminal response when its typed dispatched value is
/// still available. Guard exact correspondence, not structural guessing.
/// No change to errors, pending jobs, activation or state-change evidence;
/// notably an uncertain result is never turned into a successful receipt.
/// An optimized negotiated producer can use tryProject before serialization
/// instead; do not serialize the full result again on that hot path.
let projectTerminal (dispatchedValue: obj) (response: AgentCommandResponse) =
    match response.Result, response.Error, response.PendingJob with
    | Some original, None, None ->
        match tryProject dispatchedValue with
        | Some projected when JsonSerializer.Serialize(dispatchedValue) = original ->
            { response with Result = Some(JsonSerializer.Serialize projected) }
        | _ -> response
    | _ -> response
