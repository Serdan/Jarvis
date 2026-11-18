namespace Server

open System
open System.Collections.Generic
open System.Text.Json
open System.Text.RegularExpressions

// Default-off negative-space ingress contract. The existing C1 source capture,
// C2 original publisher barrier, B v2 provenance, E dispatch and G recovery
// do NOT currently supply a trusted end-to-end source witness. A syntactically
// valid caller assertion cannot create one. This module must not construct or
// commit WorkSqliteTransactionCommand until a separately reviewed producer
// binds the authenticated original source and its complete account scope.
//
// These dispositions deliberately contain NO caller strings or raw JSON:
// untrusted bodies can include bearer credentials and memory-only manual grants.
// Keep original immutable evidence in the original source ledger, not in an
// unverified Work row, recovery record, backup, notification or log.
[<RequireQualifiedAccess>]
type WorkTrustedSourceIngressDisposition =
    | Rejected of reason: string
    | Incomplete of reason: string
    | UncertainEffect of reason: string

[<RequireQualifiedAccess>]
module internal WorkTrustedSourceIngress =
    exception private InvalidEnvelope of string

    // Version of the preflight CLAIM envelope, not B's physical SQLite schema.
    [<Literal>]
    let ClaimFormatVersion = 1

    [<Literal>]
    let MaxClaimBytes = 128 * 1024

    let private hex40or64 = Regex("^[0-9a-f]{40}([0-9a-f]{24})?$", RegexOptions.CultureInvariant)
    let private hex64 = Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)
    let private safeRef = Regex("^refs/heads/[A-Za-z0-9][A-Za-z0-9._/-]*$", RegexOptions.CultureInvariant)

    let private reject reason = raise (InvalidEnvelope reason)

    let private keys (expected: string list) (node: JsonElement) =
        if node.ValueKind <> JsonValueKind.Object then reject "Expected a JSON object."
        let allowed = Set.ofList expected
        for p in node.EnumerateObject() do
            if not (Set.contains p.Name allowed) then
                reject "Unknown claim field or unrecognized record schema."

    let private unsafeSystemField (name: string) =
        let key = name.Replace("_", "").Replace("-", "").ToLowerInvariant()
        key.Contains("bearer") || key.Contains("secret")
        || key.Contains("accesstoken") || key.Contains("refreshtoken")
        || key.Contains("manualgrant") || key.Contains("originalstages")
        || key = "authorization" || key = "credential"

    // Defense-in-depth only. Absence of suspicious key names is NOT proof that
    // a free-text value is secret-free. No unverified body passes to SQLite.
    let rec private examine (node: JsonElement) =
        match node.ValueKind with
        | JsonValueKind.Object ->
            let seen = HashSet<string>(StringComparer.Ordinal)
            for p in node.EnumerateObject() do
                if not (seen.Add p.Name) then reject "Duplicate JSON property."
                if unsafeSystemField p.Name then reject "Nonserializable system credential field."
                examine p.Value
        | JsonValueKind.Array ->
            for child in node.EnumerateArray() do examine child
        | _ -> ()

    let private field (parent: JsonElement) (name: string) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if not (parent.TryGetProperty(name, &value)) then
            reject "Required claim field is missing."
        value

    let private text (parent: JsonElement) (name: string) =
        let value = field parent name
        if value.ValueKind <> JsonValueKind.String then reject "Expected a string field."
        value.GetString()

    let private boundedId (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && value.Length <= 256 && value = value.Trim()
        && not (value |> Seq.exists Char.IsControl)

    let private guid (value: string) =
        let mutable parsed = Guid.Empty
        if not (Guid.TryParseExact(value, "D", &parsed))
           || parsed = Guid.Empty
           || parsed.ToString("D") <> value then
            reject "Canonical nonempty UUID required."
        parsed

    let private number (parent: JsonElement) name positive =
        let value = field parent name
        let mutable n = 0L
        if value.ValueKind <> JsonValueKind.Number
           || not (value.TryGetInt64(&n))
           || (if positive then n <= 0L else n < 0L) then
            reject "Invalid version, generation or source sequence."
        n

    let private kind = function
        | "project" -> WorkSqliteRecordKind.Project
        | "work" -> WorkSqliteRecordKind.Work
        | "stage" -> WorkSqliteRecordKind.Stage
        | "launch" -> WorkSqliteRecordKind.Launch
        | "role" -> WorkSqliteRecordKind.Role
        | "review" -> WorkSqliteRecordKind.Review
        | "integration" -> WorkSqliteRecordKind.Integration
        | "external-effect" -> WorkSqliteRecordKind.ExternalEffect
        | _ -> reject "Unknown Work record kind."

    let private source (accountId: string) (projectId: string)
                       (node: JsonElement) =
        keys [ "accountId"; "projectId"; "ledgerId"; "repositoryId"; "ref"
               "commit"; "operationId"; "sequence" ] node
        if text node "accountId" <> accountId
           || text node "projectId" <> projectId then
            reject "Claim source identity differs from record scope."
        guid (text node "accountId") |> ignore
        guid (text node "projectId") |> ignore
        guid (text node "ledgerId") |> ignore
        guid (text node "operationId") |> ignore
        number node "sequence" true |> ignore
        if not (hex64.IsMatch(text node "repositoryId")) then
            reject "Invalid canonical repository identifier."
        let gitRef = text node "ref"
        if not (safeRef.IsMatch gitRef)
           || gitRef.Split('/') |> Array.exists (fun piece -> piece = "." || piece = "..")
           || gitRef.Contains("//")
           || gitRef.EndsWith("/") then
            reject "Invalid original Git ref."
        let commit = field node "commit"
        if commit.ValueKind <> JsonValueKind.Null then
            if commit.ValueKind <> JsonValueKind.String
               || not (hex40or64.IsMatch(commit.GetString())) then
                reject "Invalid source Git commit."
        // Original authority must independently validate BOTH the remote and
        // the original receipt, not this envelope's source-shaped strings.

    let private versions (node: JsonElement) =
        let names = [ "row"; "graph"; "stage"; "claim"; "stop"; "role" ]
        keys names node
        for name in names do
            number node name (name = "row") |> ignore

    let private effect (recordKind: WorkSqliteRecordKind) (root: JsonElement) =
        let mutable state = Unchecked.defaultof<JsonElement>
        let mutable operation = Unchecked.defaultof<JsonElement>
        let hasState = root.TryGetProperty("effectState", &state)
        let hasOperation = root.TryGetProperty("effectOperationId", &operation)
        if recordKind <> WorkSqliteRecordKind.ExternalEffect then
            if hasState || hasOperation then reject "Unexpected external-effect fields."
            false
        else
            if not hasState || not hasOperation
               || state.ValueKind <> JsonValueKind.String then
                reject "External-effect state and operation are required."
            if operation.ValueKind <> JsonValueKind.String then
                reject "External-effect operation must be a UUID."
            guid (operation.GetString()) |> ignore
            match state.GetString() with
            | "prepared" | "unknown" -> true
            | "observed" | "definitive-no-effect" -> false
            | _ -> reject "Unsupported external-effect state."

    /// Parses an untrusted ingress proposal, *never* authorizes it.
    ///
    /// No caller can obtain a WorkSqliteRecordMutation, WorkSqliteTransactionCommand
    /// or durable receipt from this entrypoint. The source/receipt, B v2 physical
    /// schema and writer-exclusion provider are not integrated; complete proof
    /// must be added by their owners before ANY positive production constructor.
    let inspectUntrusted (json: string) : WorkTrustedSourceIngressDisposition =
        if String.IsNullOrWhiteSpace json
           || System.Text.Encoding.UTF8.GetByteCount(json) > MaxClaimBytes then
            WorkTrustedSourceIngressDisposition.Rejected "Empty or oversized ingress envelope."
        else
            try
                let options = JsonDocumentOptions(MaxDepth = 32,
                                                   AllowTrailingCommas = false,
                                                   CommentHandling = JsonCommentHandling.Disallow)
                use document = JsonDocument.Parse(json, options)
                let root = document.RootElement
                examine root
                keys [ "formatVersion"; "accountId"; "projectId"; "kind"; "entityId"
                       "source"; "versions"; "body"; "effectState"; "effectOperationId" ] root
                if number root "formatVersion" true <> int64 ClaimFormatVersion then
                    reject "Unsupported ingress envelope schema."
                let accountId = text root "accountId"
                let projectId = text root "projectId"
                guid accountId |> ignore
                guid projectId |> ignore
                if not (boundedId (text root "entityId")) then
                    reject "Invalid bounded entity identifier."
                let recordKind = kind (text root "kind")
                let body = field root "body"
                if body.ValueKind <> JsonValueKind.Object then
                    reject "Body must be an object; no opaque arbitrary JSON fallback."
                versions (field root "versions")
                let uncertainEffect = effect recordKind root
                let mutable original = Unchecked.defaultof<JsonElement>
                if root.TryGetProperty("source", &original) then
                    source accountId projectId original
                // This is a source and serialization boundary, not a permission
                // check based on string equality or the caller saying "verified".
                if uncertainEffect then
                    WorkTrustedSourceIngressDisposition.UncertainEffect
                        "Prepared/UNKNOWN one-shot effect requires observed original result and Recovery reconciliation."
                else
                    WorkTrustedSourceIngressDisposition.Incomplete
                        "Authenticated original publisher, B v2 provenance and closed producer-specific body witness are not bound."
            with
            | InvalidEnvelope reason -> WorkTrustedSourceIngressDisposition.Rejected reason
            | :? JsonException -> WorkTrustedSourceIngressDisposition.Rejected "Invalid JSON envelope."
            | :? ArgumentException -> WorkTrustedSourceIngressDisposition.Rejected "Invalid envelope argument."
