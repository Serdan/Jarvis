namespace Server

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json
open System.Text.RegularExpressions

// Default-off B local projection contract. These are *structural* records, not
// verified original-Git evidence, authorized transitions, or a restore source.
// C/E/G still require their own independently authenticated producer/receipts.
[<RequireQualifiedAccess>]
module internal WorkSqliteDomain =
    [<Literal>]
    let Schema = "WorkSqliteDomain/B1"

    [<Literal>]
    let MaxBodyBytes = 16384

    type Expectation =
        { AccountId: string
          ProjectId: string
          OriginalLedgerId: string
          RepositoryId: string
          SourceRef: string }

    exception private InvalidBody of string

    type private Shape =
        | Id | Uuid | Sha | Digest | Ref | Number | Positive
        | Flag | Ids | OptionalId | OptionalUuid | OptionalSha
        | Choice of string list

    let private hex = Regex("^[0-9a-f]{40}([0-9a-f]{24})?$", RegexOptions.CultureInvariant)
    let private sha256 = Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)
    let private safeId = Regex("^[A-Za-z0-9][A-Za-z0-9._:/-]{0,127}$", RegexOptions.CultureInvariant)
    let private safeRef = Regex("^refs/heads/[A-Za-z0-9][A-Za-z0-9._/-]*$", RegexOptions.CultureInvariant)

    let private reject reason = raise (InvalidBody reason)

    let private id text =
        not (isNull text) && safeId.IsMatch text &&
        not (text.Contains("..", StringComparison.Ordinal)) &&
        not (text.Contains("//", StringComparison.Ordinal)) &&
        not (text.EndsWith(".", StringComparison.Ordinal)) &&
        not (text.EndsWith("/", StringComparison.Ordinal))

    let private uuid text =
        let mutable parsed = Guid.Empty
        not (isNull text) && Guid.TryParseExact(text, "D", &parsed) &&
        parsed <> Guid.Empty && parsed.ToString("D") = text

    let private gitRef text =
        not (isNull text) && safeRef.IsMatch text &&
        not (text.Contains("..", StringComparison.Ordinal)) &&
        not (text.Contains("//", StringComparison.Ordinal)) &&
        not (text.EndsWith("/", StringComparison.Ordinal)) &&
        not (text.EndsWith(".lock", StringComparison.Ordinal)) &&
        not (text.Contains("@{", StringComparison.Ordinal))

    let private stringShape shape (s: string) =
        match shape with
        | Id | OptionalId -> id s
        | Uuid | OptionalUuid -> uuid s
        | Sha | OptionalSha -> not (isNull s) && hex.IsMatch s
        | Digest -> not (isNull s) && sha256.IsMatch s
        | Ref -> gitRef s
        | Choice choices -> List.contains s choices
        | _ -> false

    let private common =
        [ "schema", Choice [ Schema ]
          "accountId", Uuid
          "projectId", Uuid
          "ledgerId", Uuid
          "sourceSequence", Positive ]

    // Closed *local* B1 bodies. Legacy C1 F#-reflection snapshots and E/G
    // unprojected JSON deliberately do not pass: source owners must supply
    // independently reviewed lossless typed projections before production use.
    let private fields = function
        | WorkSqliteRecordKind.Project ->
            [ "coverage", Choice [ "StructuralOnly" ]
              "queueVersion", Number
              "stageIds", Ids; "roleIds", Ids; "launchIds", Ids
              "originalProjectHead", Sha ]
        | WorkSqliteRecordKind.Work ->
            [ "workId", Id; "queueId", Id; "proposalState", Choice [ "Draft"; "Proposed"; "Open" ]
              "roleId", Id; "leadPriority", Number ]
        | WorkSqliteRecordKind.Stage ->
            [ "state", Choice [ "Available"; "Blocked"; "Claimed"; "Completed"; "Cancelled" ]
              "queueId", Id
              "gate", Choice [ "Clear"; "CompositionHold"; "DependencyGatePending"
                               "IntegrationGateHold"; "ExternalEffectObservedPendingGate"; "SourceRecoveryBlocked" ]
              "proposalState", Choice [ "Draft"; "Proposed"; "Open" ]
              "decisionReceiptId", Uuid
              "availableSequence", Number
              "dependencies", Ids; "waivedDependencies", Ids
              "claimLaunch", OptionalId ]
        | WorkSqliteRecordKind.Launch ->
            [ "state", Choice [ "ReservedUnregistered"; "Registered"; "Running"; "Stopped"; "Completed" ]
              "roleId", Id; "credit", OptionalId; "primaryWorkId", OptionalId
              "everClaimed", Flag; "stopRequested", Flag
              "reservationOperationId", Uuid; "reservationAccountId", Uuid
              "reservationWorkId", Id; "reservationWorkGeneration", Number
              "claimOperationId", OptionalUuid; "claimWorkGeneration", Number ]
        | WorkSqliteRecordKind.Role ->
            [ "state", Choice [ "Active" ]; "queues", Ids
              "instructionsDigest", Digest; "evidenceReceiptId", Uuid ]
        | WorkSqliteRecordKind.Review ->
            [ "workId", Id; "frozenCommit", Sha
              "decision", Choice [ "Pending"; "Accepted"; "ChangesRequested" ]
              "decisionSequence", Number; "decisionReceiptId", OptionalUuid ]
        | WorkSqliteRecordKind.Integration ->
            [ "workId", Id; "sourceCommit", Sha; "integratedCommit", OptionalSha
              "status", Choice [ "Pending"; "Integrated"; "Held" ]
              "integrationReceiptId", OptionalUuid ]
        | WorkSqliteRecordKind.ExternalEffect ->
            [ "effectKind", Choice [ "Launch"; "OriginalGit"; "Recovery" ]
              "effectState", Choice [ "Prepared"; "Unknown"; "DefinitiveNoEffect"; "Observed" ]
              "operationId", Uuid; "prepareOperationId", Uuid; "uncertaintyOperationId", OptionalUuid
              "observedReceiptId", OptionalUuid
              "launchId", Id; "workId", Id; "roleId", Id
              "roleVersion", Number; "stageGeneration", Number; "stopGeneration", Number
              "repositoryId", Digest; "sourceRef", Ref; "sourceSha", Sha
              "sourceHead", Sha; "projectHead", Sha
              "reservationOperationId", Uuid ]

    let private get (body: JsonElement) (name: string) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if not (body.TryGetProperty(name, &value)) then reject "Missing closed domain field."
        value

    let private str body name =
        let value = get body name
        if value.ValueKind <> JsonValueKind.String then reject "Invalid domain field type."
        value.GetString()

    let private num body name =
        let value = get body name
        let mutable n = 0L
        if value.ValueKind <> JsonValueKind.Number || not (value.TryGetInt64(&n)) || n < 0L then
            reject "Invalid domain generation or version."
        n

    let private flag body name =
        match (get body name).ValueKind with
        | JsonValueKind.True -> true
        | JsonValueKind.False -> false
        | _ -> reject "Invalid domain boolean."

    let private optional body name =
        let value = get body name
        if value.ValueKind = JsonValueKind.Null then None
        elif value.ValueKind = JsonValueKind.String then Some(value.GetString())
        else reject "Invalid optional domain field."

    let private validateShape shape (value: JsonElement) =
        match shape with
        | Number | Positive ->
            let mutable n = 0L
            value.ValueKind = JsonValueKind.Number &&
            value.TryGetInt64(&n) && (if shape = Positive then n > 0L else n >= 0L)
        | Flag -> value.ValueKind = JsonValueKind.True || value.ValueKind = JsonValueKind.False
        | Ids ->
            if value.ValueKind <> JsonValueKind.Array then false
            else
                let items = value.EnumerateArray() |> Seq.toList
                items.Length <= 2048 &&
                (items |> List.forall (fun x -> x.ValueKind = JsonValueKind.String && id (x.GetString()))) &&
                (items |> List.map (fun x -> x.GetString())) =
                    (items |> List.map (fun x -> x.GetString()) |> List.distinct |> List.sortWith (fun a b -> StringComparer.Ordinal.Compare(a, b)))
        | OptionalId | OptionalUuid | OptionalSha when value.ValueKind = JsonValueKind.Null -> true
        | _ -> value.ValueKind = JsonValueKind.String && stringShape shape (value.GetString())

    let private checkProperties (kind: WorkSqliteRecordKind) (body: JsonElement) =
        if body.ValueKind <> JsonValueKind.Object then reject "A closed domain body must be an object."
        let expected = common @ fields kind
        let names = expected |> List.map fst |> Set.ofList
        let seen = HashSet<string>(StringComparer.Ordinal)
        for property in body.EnumerateObject() do
            if not (seen.Add property.Name) || not (names.Contains property.Name) then
                reject "Duplicate or unrecognized closed-domain field."
        if seen.Count <> expected.Length then reject "Incomplete closed-domain body."
        for name, shape in expected do
            if not (validateShape shape (get body name)) then
                reject ("Invalid closed-domain field: " + name)

    let private canonical (body: JsonElement) =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        writer.WriteStartObject()
        for p in body.EnumerateObject() |> Seq.sortWith (fun a b -> StringComparer.Ordinal.Compare(a.Name, b.Name)) do
            match p.Value.ValueKind with
            | JsonValueKind.String -> writer.WriteString(p.Name, p.Value.GetString())
            | JsonValueKind.Number -> writer.WriteNumber(p.Name, p.Value.GetInt64())
            | JsonValueKind.True -> writer.WriteBoolean(p.Name, true)
            | JsonValueKind.False -> writer.WriteBoolean(p.Name, false)
            | JsonValueKind.Null -> writer.WriteNull(p.Name)
            | JsonValueKind.Array ->
                writer.WriteStartArray(p.Name)
                for child in p.Value.EnumerateArray() do writer.WriteStringValue(child.GetString())
                writer.WriteEndArray()
            | _ -> reject "Nested untyped JSON cannot enter a closed body."
        writer.WriteEndObject()
        writer.Flush()
        Encoding.UTF8.GetString(stream.ToArray())

    let private checkSpecific (row: WorkSqliteRecordMutation) (body: JsonElement) =
        let value = row.Value
        let rowId = row.Key.EntityId
        match row.Key.Kind with
        | WorkSqliteRecordKind.Project ->
            if rowId <> "authority" then reject "Project B1 must have the authority row identity."
        | WorkSqliteRecordKind.Work ->
            if str body "workId" <> rowId then reject "Work ID differs from row identity."
        | WorkSqliteRecordKind.Stage ->
            let state = str body "state"
            let claim = optional body "claimLaunch"
            if (state = "Claimed") <> claim.IsSome ||
               (state = "Claimed" && value.ClaimGeneration = 0L) ||
               (state = "Available" && str body "proposalState" <> "Open") then
                reject "Claim and approval state does not match generation."
            let waived = get body "waivedDependencies"
                         |> fun x -> x.EnumerateArray() |> Seq.map (fun p -> p.GetString()) |> Set.ofSeq
            let dependencies = get body "dependencies"
                               |> fun x -> x.EnumerateArray() |> Seq.map (fun p -> p.GetString()) |> Set.ofSeq
            if not (Set.isSubset waived dependencies) then reject "Waiver does not correspond to a dependency."
        | WorkSqliteRecordKind.Launch ->
            let claimed = flag body "everClaimed"
            let primary = optional body "primaryWorkId"
            let credit = optional body "credit"
            if (claimed <> primary.IsSome) || (claimed && credit.IsSome) ||
               (claimed && (value.ClaimGeneration = 0L || (optional body "claimOperationId").IsNone)) ||
               (not claimed && (optional body "claimOperationId").IsSome) ||
               str body "reservationAccountId" <> str body "accountId" ||
               (flag body "stopRequested" && value.StopGeneration = 0L) then
                reject "Invalid launch claim, reservation or STOP state."
        | WorkSqliteRecordKind.Role ->
            if value.RoleVersion <= 0L then reject "Role version must be positive."
        | WorkSqliteRecordKind.Review ->
            let decisionSequence = num body "decisionSequence"
            let decisionReceipt = optional body "decisionReceiptId"
            let consistentDecision =
                match str body "decision" with
                | "Pending" -> decisionSequence = 0L && decisionReceipt.IsNone
                | "Accepted" | "ChangesRequested" -> decisionSequence > 0L && decisionReceipt.IsSome
                | _ -> false
            if value.ReviewSha <> Some(str body "frozenCommit") || not consistentDecision then
                reject "Unbound frozen review decision or stale sequence."
        | WorkSqliteRecordKind.Integration ->
            let integrated = str body "status" = "Integrated"
            if (integrated <> (optional body "integratedCommit").IsSome) ||
               (integrated <> (optional body "integrationReceiptId").IsSome) ||
               (integrated && value.IntegrationSha <> optional body "integratedCommit") ||
               (not integrated && value.IntegrationSha.IsSome) ||
               value.SourceSha <> Some(str body "sourceCommit") then
                reject "Unbound integration source or completion receipt."
        | WorkSqliteRecordKind.ExternalEffect ->
            let effect = str body "effectState"
            let matches =
                match effect, value.ExternalEffect with
                | "Prepared", WorkSqliteEffect.Prepared
                | "Unknown", WorkSqliteEffect.Unknown
                | "DefinitiveNoEffect", WorkSqliteEffect.DefinitiveNoEffect
                | "Observed", WorkSqliteEffect.Observed -> true
                | _ -> false
            if not matches ||
               (effect = "Unknown") <> (optional body "uncertaintyOperationId").IsSome ||
               ((effect = "Observed" || effect = "DefinitiveNoEffect") <>
                  (optional body "observedReceiptId").IsSome) ||
               (effect = "Prepared" && (optional body "observedReceiptId").IsSome) ||
               str body "sourceSha" <> (value.SourceSha |> Option.defaultValue "") ||
               num body "roleVersion" <> value.RoleVersion ||
               num body "stageGeneration" <> value.StageGeneration ||
               num body "stopGeneration" <> value.StopGeneration ||
               optional body "uncertaintyOperationId" = Some(str body "prepareOperationId") ||
               optional body "uncertaintyOperationId" = Some(str body "operationId") then
                reject "Inconsistent external effect identity, fence or receipt."

    /// Structural-only projection of a *caller-supplied* B1 record. Never
    /// returns Verified and never writes SQLite. All source/receipt expectations
    /// are merely equalities until an independently authenticated C/F/G owner
    /// supplies authoritative witnesses and an authorized transition.
    let normalizeStructure (expectation: Expectation)
                           (input: WorkSqliteProvenancedMutation)
                           : Result<string, WorkSqliteFailure> =
        let invalid reason = Error(WorkSqliteFailure.InvalidCommand reason)
        let row = input.Mutation
        let value = row.Value
        if not (uuid expectation.AccountId && uuid expectation.ProjectId &&
                uuid expectation.OriginalLedgerId &&
                not (isNull expectation.RepositoryId) &&
                sha256.IsMatch expectation.RepositoryId && gitRef expectation.SourceRef) then
            invalid "Malformed local domain scope expectation."
        elif row.Key.ProjectId <> expectation.ProjectId || not (id row.Key.EntityId) then
            invalid "Record project/row scope differs from expected local scope."
        elif [ value.GraphVersion; value.StageGeneration; value.ClaimGeneration
               value.StopGeneration; value.RoleVersion ] |> List.exists (fun x -> x < 0L) then
            invalid "Invalid domain generation."
        else
            match input.Source, value.SourceSha with
            | Some source, Some commit when
                source.RepositoryId = expectation.RepositoryId &&
                source.SourceRef = expectation.SourceRef &&
                source.SourceCommit = commit && hex.IsMatch commit &&
                source.VerificationReceiptId <> Guid.Empty &&
                not (isNull source.EvidenceSha256) &&
                sha256.IsMatch source.EvidenceSha256 ->
                if String.IsNullOrWhiteSpace value.BodyJson ||
                   Encoding.UTF8.GetByteCount(value.BodyJson) > MaxBodyBytes then
                    invalid "Missing or oversized closed-domain body."
                else
                    try
                        let options = JsonDocumentOptions(MaxDepth = 8, CommentHandling = JsonCommentHandling.Disallow,
                                                          AllowTrailingCommas = false)
                        use doc = JsonDocument.Parse(value.BodyJson, options)
                        let body = doc.RootElement
                        checkProperties row.Key.Kind body
                        if str body "accountId" <> expectation.AccountId ||
                           str body "projectId" <> expectation.ProjectId ||
                           str body "ledgerId" <> expectation.OriginalLedgerId then
                            reject "Body account/project/original ledger scope does not match local expectation."
                        if row.Key.Kind = WorkSqliteRecordKind.ExternalEffect &&
                           (str body "repositoryId" <> expectation.RepositoryId ||
                            str body "sourceRef" <> expectation.SourceRef) then
                            reject "External effect original repository/ref differs from local expectation."
                        checkSpecific row body
                        Ok(canonical body)
                    with
                    | InvalidBody reason -> invalid reason
                    | :? JsonException -> invalid "Invalid closed-domain JSON."
                    | :? ArgumentException -> invalid "Invalid closed-domain argument."
            | _ -> invalid "Missing original source commit/ref/receipt binding for local B1 structure."
