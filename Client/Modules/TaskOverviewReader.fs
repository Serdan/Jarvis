module Client.TaskOverviewReader

open System
open System.Security.Cryptography
open System.Text.Json
open FsToolkit.ErrorHandling
open Common
open Client.LedgerStore
open Client.ReviewWorkflow
open Client.TaskOverviewProjection

// System.Text.Json requires public properties and a public constructor for
// this CLIMutable record. Keep the signing/validation functions private,
// not the serialization contract.
[<CLIMutable>]
type CursorPayload =
    { ProjectId: Guid
      LedgerId: Guid
      Epoch: int
      SnapshotCommit: string
      QueryDigest: string
      Offset: int
      ExpiresAtUtc: DateTimeOffset }

// The cursor is an opaque continuation, not a credential. Bind it to the
// approved ledger, exact normalized query and verified snapshot, and expire it
// after 30 minutes. A client restart requires a fresh first page.
let private key = lazy (RandomNumberGenerator.GetBytes 32)
let private encodeBytes (bytes: byte[]) =
    Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
let private decodeBytes (value: string) =
    let normalized = value.Replace('-', '+').Replace('_', '/')
    Convert.FromBase64String(normalized.PadRight((normalized.Length + 3) / 4 * 4, '='))
let private encode (cursor: CursorPayload) =
    let data = JsonSerializer.SerializeToUtf8Bytes(cursor)
    encodeBytes data + "." + encodeBytes (HMACSHA256.HashData(key.Value, data))
let private decode (token: string) =
    let invalid = Error(InvalidBinding "Invalid or expired TaskOverview cursor; restart at the first page.")
    try
        if token.Length > 2048 then invalid
        else
            match token.Split('.') with
            | [| body; signature |] ->
                let data = decodeBytes body
                let supplied = decodeBytes signature
                let expected = HMACSHA256.HashData(key.Value, data)
                if supplied.Length <> expected.Length
                   || not (CryptographicOperations.FixedTimeEquals(supplied, expected)) then invalid
                else
                    let cursor = JsonSerializer.Deserialize<CursorPayload>(data)
                    if isNull (box cursor) then invalid else Ok cursor
            | _ -> invalid
    with _ -> invalid

let private optionalLabel (value: string) =
    if String.IsNullOrEmpty value then None else Some value

let read (binding: LedgerBinding) (request: TaskOverviewRequest) =
    result {
        if isNull (box request) then
            return! Error(InvalidBinding "TaskOverview request must be an object.")
        let labels = [ request.Lifecycle; request.Phase; request.Assignee; request.ReviewStatus ]
        if labels |> List.exists (fun x ->
            not (isNull x) && (x.Length > 256 || Seq.exists Char.IsControl x)) then
            return! Error(InvalidBinding "TaskOverview filters must be at most 256 characters without controls.")
        if request.SinceSequence < -1L || request.PageSize < 0 || request.PageSize > MaximumPageSize then
            return! Error(InvalidBinding "TaskOverview pageSize must be 0..100; sinceSequence must be -1 or nonnegative.")
        let! scope =
            match request.Scope with
            | null | "" | "pertinent" -> Ok Pertinent
            | "history" -> Ok History
            | _ -> Error(InvalidBinding "TaskOverview scope must be pertinent or history.")
        let normalize x = if isNull x then "" else x
        let normalized =
            { request with
                Scope = if scope = Pertinent then "pertinent" else "history"
                Lifecycle = normalize request.Lifecycle; Phase = normalize request.Phase
                Assignee = normalize request.Assignee; ReviewStatus = normalize request.ReviewStatus
                PageSize = if request.PageSize = 0 then DefaultPageSize else request.PageSize
                Cursor = "" }
        let query =
            { Scope = scope; Lifecycle = optionalLabel normalized.Lifecycle
              Phase = optionalLabel normalized.Phase; Assignee = optionalLabel normalized.Assignee
              ReviewStatus = optionalLabel normalized.ReviewStatus
              SinceSequence = if normalized.SinceSequence = -1L then None else Some normalized.SinceSequence }
        let digest = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(normalized)) |> Convert.ToHexString
        let now = DateTimeOffset.UtcNow
        let! snapshot, offset, expiry =
            if String.IsNullOrEmpty request.Cursor then
                LedgerStore.fetch binding |> Result.map (fun snapshot -> snapshot, 0, now.AddMinutes 30.0)
            else
                result {
                    let! token = decode request.Cursor
                    if token.ProjectId <> binding.ProjectId || token.LedgerId <> binding.LedgerId
                       || token.Epoch <> binding.Epoch || token.QueryDigest <> digest || token.Offset <= 0
                       || token.ExpiresAtUtc <= now || token.ExpiresAtUtc > now.AddMinutes 30.0 then
                        return! Error(InvalidBinding "TaskOverview cursor expired or has mismatched ledger/query; restart at the first page.")
                    let! pinned = LedgerStore.verifiedHistoricalSnapshot binding token.SnapshotCommit
                    return pinned, token.Offset, token.ExpiresAtUtc
                }
        let! overview = ReviewWorkflow.listProjectTasksAt binding snapshot
        let tasks : TaskItem list =
            overview.Tasks |> List.map (fun item -> { Task = item.Task; Attempt = item.Attempt })
        let projected = project snapshot.Commit query tasks overview.Reviews
        let next offset =
            encode { ProjectId = binding.ProjectId; LedgerId = binding.LedgerId; Epoch = binding.Epoch
                     SnapshotCommit = snapshot.Commit; QueryDigest = digest
                     Offset = offset; ExpiresAtUtc = expiry }
        return!
            selectPage next normalized.PageSize offset projected
            |> Result.mapError (function
                | InvalidPageSize -> InvalidBinding "Invalid TaskOverview page size."
                | InvalidOffset -> InvalidBinding "Invalid TaskOverview cursor offset; restart at the first page."
                | PageEnvelopeTooLarge -> InvalidBinding "TaskOverview page envelope exceeds its byte budget."
                | ItemTooLarge(kind, id) -> InvalidBinding $"TaskOverview {kind} {id} exceeds the page byte budget; use targeted detail.")
    }
