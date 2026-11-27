// Standalone focused fixture; not a registered-route or deployment test.
// Build the exact baseline: dotnet build Client/Client.fsproj -c Release
// Run: dotnet fsi --lib:Client/bin/Release/net11.0 Client.Tests/CompactMutationReceiptTests.fsx
#r "FsCodec.dll"
#r "FsCodec.SystemTextJson.dll"
#r "FsToolkit.ErrorHandling.dll"
#r "FsToolkit.ErrorHandling.TaskResult.dll"
#r "FSharp.UMX.dll"
#r "Kehlet.FSharp.IO.dll"
#r "Common.dll"
#r "JarvisClient.dll"
#load "../Client/Modules/CompactMutationReceipt.fs"

open System
open System.Text
open System.Text.Json
open System.Security.Cryptography
open Common
open Client.LedgerStore
open Client.OrchestrationApi.OperationResponse
open Client.CompactMutationReceipt

let check description condition =
    if not condition then failwith description
let eq description expected actual =
    check (sprintf "%s: expected %A; actual %A" description expected actual) (expected = actual)
let json (value: obj) = JsonSerializer.Serialize value
let sha (text: string) =
    SHA256.HashData(Encoding.UTF8.GetBytes text) |> Convert.ToHexString |> _.ToLowerInvariant()
let bytes (text: string) = Encoding.UTF8.GetByteCount text
let opId = "b5a9d88f-0c81-4dab-822e-cbd1a91a1941"
let head = String.replicate 40 "a"
let laterHead = String.replicate 40 "b"
let path n = sprintf "tasks/task-%03d/attempts/attempt.json" n
let effects n =
    Array.init n (fun i ->
        {| path = path i; entityKind = "Attempt"; entityVersion = 19 + i
           assignmentGeneration = 3; taskId = sprintf "task-%03d" i
           attemptId = sprintf "attempt-%03d" i; phase = "Started"
           specBlobOid = String.replicate 40 "c"; reviewRef = sprintf "refs/reviews/%03d" i
           contentSha256 = String.replicate 64 "d" |})
let operation kind n : LedgerOperation =
    let request = json (box {| operationId = opId; kind = kind
                               instructions = String.replicate 256 "source & evidence <quoted> " |})
    let result =
        json (box {| schemaVersion = 2; operationId = opId; sequence = 100L; kind = kind
                     agentStateChanges = [| "Worker 2" |]
                     changes = Array.init n (fun i ->
                         {| path = path i; beforeOid = String.replicate 40 "e"
                            contentSha256 = String.replicate 64 "d" |})
                     entityResults = effects n
                     futureActionCritical = {| publicationHead = laterHead; recovery = "exact" |} |})
    { SchemaVersion = 2; OperationId = opId; Sequence = 100L; Type = kind
      Actor = "Worker 2"; Path = path 0; BlobOid = ""; Paths = Array.init n path
      CanonicalRequestJson = request; RequestHash = sha request
      OriginalResultJson = result; OriginalResultHash = sha result }

let delivery : Client.ReviewWorkflow.NotificationDelivery =
    { Status = "Delivered"; NotificationId = Guid.Parse(opId).ToString("N"); AutoReply = "" }
let presented op currentHead replay notification =
    presentWithNotification notification
        { Operation = op; CurrentHead = currentHead; AlreadyApplied = replay }
    |> unbox<Presented>
let state = { ProjectName = "Jarvis"; AgentId = "Worker 2" }
let response payload : AgentCommandResponse =
    { Result = Some payload; Error = None; Instructions = []
      StateChangeEvidence = StateChangeEvidence.Confirmed [state]
      InboxActivation = None; PendingJob = None }
let terminal (value: obj) = response (json value) |> projectTerminal value
let resultOf (r: AgentCommandResponse) = r.Result |> Option.get
let projected value =
    tryProject (box value) |> Option.defaultWith (fun () -> failwith "not projected")
let assertOriginal op (receipt: PublicReceipt) =
    use original = JsonDocument.Parse(op.OriginalResultJson)
    check "exact original result semantics including future fields"
        (JsonElement.DeepEquals(original.RootElement, receipt.originalResult))
    eq "request hash" op.RequestHash receipt.operation.RequestHash
    eq "original result hash" op.OriginalResultHash receipt.operation.OriginalResultHash
    eq "exact changed paths" op.Paths receipt.operation.Paths
    eq "operation identity" op.OperationId receipt.operation.OperationId
    eq "sequence" op.Sequence receipt.operation.Sequence

let op = operation "NotifyAgent" 1
let originalOperationBytes = json (box op)
let first = presented op head false (Some delivery)
let compact = project first
assertOriginal op compact
eq "first head" head compact.head
check "first receipt is not replay" (not compact.alreadyApplied)
eq "notification disposition" (Some delivery) compact.notificationDelivery
let blocked = { delivery with Status = "Blocked"; NotificationId = null; AutoReply = "Do not send again" }
eq "blocked auto reply is exact" (Some blocked)
    ((presented op head false (Some blocked) |> project).notificationDelivery)
eq "canonical record unchanged" originalOperationBytes (json (box op))
use compactDoc = JsonDocument.Parse(json (box compact))
let header = compactDoc.RootElement.GetProperty("operation")
let has (name: string) (obj: JsonElement) =
    let mutable prop = Unchecked.defaultof<JsonElement>
    obj.TryGetProperty(name, &prop)
check "no duplicate canonical request" (not (has "CanonicalRequestJson" header))
check "no escaped original result duplicate" (not (has "OriginalResultJson" header))
eq "legacy result retained byte-for-byte in source" op.OriginalResultJson first.operation.OriginalResultJson

// Replay / lost response: the canonical writer determines matching UUID and
// current head. This fixture does not reimplement the journal or its CAS.
let replay = presented op laterHead true (Some delivery) |> project
assertOriginal op replay
check "replay flag retained" replay.alreadyApplied
eq "replay current head is not original head" laterHead replay.head
eq "replay original result unchanged" (compact.originalResult.GetRawText()) (replay.originalResult.GetRawText())

let originalResponse = response (json (box first))
let compactResponse = projectTerminal (box first) originalResponse
eq "activity retained" originalResponse.StateChangeEvidence compactResponse.StateChangeEvidence
eq "exact typed payload" (json (box compact)) (resultOf compactResponse)
eq "mismatched dispatched receipt does not overwrite original"
    (resultOf originalResponse) (projectTerminal (box replay) originalResponse |> resultOf)

// Collision and uncertain response must remain errors, never be made success.
let collision =
    { originalResponse with Result = None
                            Error = Some(LedgerFailure { Code = "OperationIdReused"; Message = "UUID collision"
                                                         OperationId = Some opId; VerifiedLedgerHead = Some head }) }
eq "collision unchanged" collision (projectTerminal (box first) collision)
let activation : AgentInboxActivation =
    { ProjectName = "Jarvis"; AgentId = "Worker 2"; ActivatedByThisCall = true
      BeforeState = Some "Ready"; AfterState = Some "Working"; CommittedHead = Some head
      OperationId = Some "c48d42af-8607-43e3-8441-d4b59372a79c" }
let lost =
    { collision with Error = Some(UncertainOutcome { OperationId = Some opId; Message = "Response lost" })
                     StateChangeEvidence = StateChangeEvidence.Unknown [state]
                     InboxActivation = Some activation
                     Instructions = [OperationalControl.ActivationUnverified "partial evidence"] }
let projectedLost = projectTerminal (box first) lost
eq "lost response and activation-before-failure unchanged" lost projectedLost
eq "uncertainty and partial known state serialize unchanged" (json (box lost)) (json (box projectedLost))
let unknownEmpty = { lost with StateChangeEvidence = StateChangeEvidence.Unknown []; InboxActivation = None }
eq "unknown-empty differs from confirmed empty" "unknown" (StateChangeEvidence.status unknownEmpty.StateChangeEvidence)
eq "unknown-empty untouched" unknownEmpty (projectTerminal (box first) unknownEmpty)
let pending =
    { originalResponse with StateChangeEvidence = StateChangeEvidence.Pending
                            PendingJob = Some { JobId = "fixture"; Kind = JobKind.ToolOperation; Status = "Running"
                                                ProjectName = Some "Jarvis"; DeviceId = None; AgentId = "Worker 2"
                                                OperationId = Some opId; CommandName = "NotifyAgent"
                                                StartedAt = DateTimeOffset.UnixEpoch } }
eq "pending unchanged" pending (projectTerminal (box first) pending)

let created : ChallengeCreatedPresented =
    { challengeId = "challenge"; conditionIds = ["condition-1"; "condition-2"]; receipt = first }
let projectedCreated = projected created |> unbox<PublicChallengeCreated>
eq "challenge wrapper ID" created.challengeId projectedCreated.challengeId
eq "condition IDs" created.conditionIds projectedCreated.conditionIds
assertOriginal op projectedCreated.receipt

// Exact publication and journal acknowledgments are distinct existing result
// shapes. Never project them by recognizing arbitrary matching field names.
let publication : IntegrationPublicationReceipt =
    { PreviousHead = head; PublishedCommit = laterHead; ObservedHead = laterHead
      OperationId = opId; Mechanism = "NonForcePush" }
let reconcile =
    {| operationId = opId; disposition = "Applied"; alreadyApplied = true
       verifiedLedgerHead = laterHead; requestHash = op.RequestHash |}
let ack =
    {| operationId = opId; verifiedLedgerHead = laterHead; originalResult = op.OriginalResultJson
       acknowledged = true; alreadyAcknowledged = false |}
let status =
    {| snapshotCommit = laterHead; operationId = opId; disposition = "Applied"
       originatingCommit = Some head; originalResult = Some first.originalResult; operation = Some op |}
for exact in [box publication; box reconcile; box ack; box status] do
    check "unrelated exact receipt not eligible" (tryProject exact |> Option.isNone)
    let before = response (json exact)
    eq "exact publication/reconciliation/status receipt unchanged" before (projectTerminal exact before)

// Synthetic high-history measurements; producer and nested serialization
// measured independently. This is not a live wire/registered-route benchmark.
let measure count =
    let operation = operation "AssignTask" count
    let legacy = presented operation head false None |> box
    let reduced = projected legacy
    let oldPayload, newPayload = json legacy, json reduced
    let oldNested = json (box {| ToolResponse = response oldPayload; Status = "ToolCompleted" |})
    let newNested = json (box {| ToolResponse = response newPayload; Status = "ToolCompleted" |})
    check "raw payload strictly smaller" (bytes newPayload < bytes oldPayload)
    check "nested payload strictly smaller" (bytes newNested < bytes oldNested)
    assertOriginal operation (unbox<PublicReceipt> reduced)
    printfn "entities=%d producer=%d->%d nested=%d->%d" count
        (bytes oldPayload) (bytes newPayload) (bytes oldNested) (bytes newNested)
for n in [1; 220; 1000] do measure n
printfn "PASS: standalone receipt projection; canonical writer, compatibility and registered-route integration NOT exercised."
