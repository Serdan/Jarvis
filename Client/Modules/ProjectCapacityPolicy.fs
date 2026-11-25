module Client.ProjectCapacityPolicy

open System
open System.Text.Json
open Client.LedgerStore

// This component is not registered with the runtime and grants no admission.
// The schema-1 record is retained for compatibility with isolated legacy callers.
[<CLIMutable>]
type Policy =
    { Schema: int
      Project: Guid
      Ledger: Guid
      Epoch: int
      Revision: int
      Total: int
      Roles: Map<string, int> }

type OptionalPolicy =
    { Schema: int
      Project: Guid
      Ledger: Guid
      Epoch: int
      Revision: int
      Total: int option
      Roles: Map<string, int>
      AutomationEnabled: bool }

let private path = "records/project-capacity-policy/policy.json"
let private reject message = Error(InvalidBinding ("Project capacity policy: " + message))
let private validBinding (binding: LedgerBinding) =
    binding.ProjectId <> Guid.Empty && binding.LedgerId <> Guid.Empty && binding.Epoch > 0

let private validSnapshot (binding: LedgerBinding) (snapshot: LedgerSnapshot) =
    let matches value (raw: string) =
        match Guid.TryParse raw with
        | true, id -> id = value
        | _ -> false
    validBinding binding
    && matches binding.ProjectId snapshot.Header.ProjectId
    && matches binding.LedgerId snapshot.Header.LedgerId
    && binding.Epoch = snapshot.Header.Epoch

let private validRoleId (role: string) =
    not (String.IsNullOrEmpty role) && role.Length <= 80
    && (role |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || c = '_' || c = '-'))

let private validCaps total roles =
    Option.forall (fun n -> n > 0) total
    && not (isNull (box roles))
    && (roles |> Map.forall (fun role n -> validRoleId role && n > 0))

let private fieldsExactly expected (element: JsonElement) =
    element.ValueKind = JsonValueKind.Object
    && (element.EnumerateObject() |> Seq.map (fun p -> p.Name) |> Seq.sort |> Seq.toList)
       = List.sort expected

// RoleInstructions' role reader is private; use the canonical record at the
// same pinned snapshot, never its public fetch-based reader. This proves role
// identity only; assignment and dispatch authority remain separate gates.
let private requireRole binding snapshot role =
    if not (validRoleId role) then reject "invalid role ID"
    else
        LedgerStore.readAt binding snapshot ("records/roles/" + role + ".json")
        |> Result.bind (function
            | Some raw, Some _ ->
                try
                    use doc = JsonDocument.Parse raw
                    let root = doc.RootElement
                    let expected = ["schemaVersion"; "roleId"; "purpose"; "instructions"; "version"; "updatedBy"; "updatedSequence"]
                    let nonempty (name: string) = not (String.IsNullOrWhiteSpace(root.GetProperty(name).GetString()))
                    if fieldsExactly expected root
                       && root.GetProperty("schemaVersion").GetInt32() = 1
                       && root.GetProperty("roleId").GetString() = role
                       && root.GetProperty("version").GetInt32() > 0
                       && root.GetProperty("updatedSequence").GetInt64() > 0L
                       && root.GetProperty("updatedSequence").GetInt64() < snapshot.Header.NextSequence
                       && nonempty "purpose" && nonempty "instructions" && nonempty "updatedBy"
                    then Ok ()
                    else reject "invalid canonical role record"
                with _ -> reject "malformed canonical role record"
            | _ -> reject "unknown or inconsistent canonical role")

let private requireRoles binding snapshot roles =
    roles |> Map.fold (fun result role _ ->
        result |> Result.bind (fun () -> requireRole binding snapshot role)) (Ok ())

let private parsePolicy (binding: LedgerBinding) (raw: string) =
    try
        use doc = JsonDocument.Parse raw
        let root = doc.RootElement
        let schema = root.GetProperty("Schema").GetInt32()
        let fields = ["Schema"; "Project"; "Ledger"; "Epoch"; "Revision"; "Total"; "Roles"]
        let expected = if schema = 3 then "AutomationEnabled" :: fields else fields
        if not (fieldsExactly expected root) then
            reject "missing, duplicate, or unknown policy fields"
        else
            let totalElement = root.GetProperty("Total")
            let total =
                if totalElement.ValueKind = JsonValueKind.Null then None
                else Some(totalElement.GetInt32())
            let roleFields =
                root.GetProperty("Roles").EnumerateObject()
                |> Seq.map (fun p -> p.Name, p.Value.GetInt32())
                |> Seq.toList
            let roles = Map.ofList roleFields
            let p : OptionalPolicy =
                { Schema = schema
                  Project = root.GetProperty("Project").GetGuid()
                  Ledger = root.GetProperty("Ledger").GetGuid()
                  Epoch = root.GetProperty("Epoch").GetInt32()
                  Revision = root.GetProperty("Revision").GetInt32()
                  Total = total
                  Roles = roles
                  AutomationEnabled = schema = 3 && root.GetProperty("AutomationEnabled").GetBoolean() }
            if (schema <> 1 && schema <> 2 && schema <> 3)
               || (schema = 1 && (Option.isNone total || Map.isEmpty roles))
               || p.Project <> binding.ProjectId || p.Ledger <> binding.LedgerId || p.Epoch <> binding.Epoch
               || p.Revision <= 0 || roles.Count <> roleFields.Length || not (validCaps total roles)
            then reject "invalid or mismatched policy"
            else Ok p
    with _ -> reject "malformed policy"

// Read the current policy at the caller's already verified snapshot. No fetch
// or expected revision is needed; only genuine record absence is disabled.
let readCurrentOptional (binding: LedgerBinding) snapshot =
    if not (validSnapshot binding snapshot) then
        reject "invalid snapshot binding"
    else
        LedgerStore.readAt binding snapshot path
        |> Result.bind (function
            | None, None ->
                Ok ({ Schema = 2; Project = binding.ProjectId; Ledger = binding.LedgerId
                      Epoch = binding.Epoch; Revision = 0; Total = None; Roles = Map.empty
                      AutomationEnabled = false }, None)
            | Some raw, Some oid ->
                parsePolicy binding raw
                |> Result.bind (fun p ->
                    requireRoles binding snapshot p.Roles
                    |> Result.map (fun () -> p, Some oid))
            | _ -> reject "inconsistent policy record")

let private readPolicy binding snapshot expectedRevision =
    if expectedRevision < 0 then reject "invalid expected revision"
    else
        readCurrentOptional binding snapshot
        |> Result.bind (fun (policy, oid) ->
            if policy.Revision <> expectedRevision then reject "stale policy revision"
            else Ok (policy, oid))

// Exact expected revision, including zero for absence, is mandatory. The
// caller must use the same authenticated snapshot on every CAS attempt.
let readOptionalForRole binding snapshot expectedRevision role =
    readPolicy binding snapshot expectedRevision
    |> Result.bind (fun (p, oid) ->
        if p.Schema = 1 && not (Map.containsKey role p.Roles) then
            reject "unlisted legacy role"
        else
            requireRole binding snapshot role |> Result.map (fun () -> p, oid))

let private legacy (p: OptionalPolicy) total : Policy =
    { Schema = 1; Project = p.Project; Ledger = p.Ledger; Epoch = p.Epoch
      Revision = p.Revision; Total = total; Roles = p.Roles }

// Legacy consumers cannot silently reinterpret optional policy as mandatory.
let readForRole binding snapshot expectedRevision role =
    readOptionalForRole binding snapshot expectedRevision role
    |> Result.bind (fun (p, oid) ->
        match p.Schema, p.Total, oid with
        | 1, Some total, Some blob -> Ok(legacy p total, blob)
        | _ -> reject "schema-1 configured policy required")

let private updateCore schema automationEnabled (binding: LedgerBinding) actor operation expectedRevision total roles =
    if expectedRevision < 0 || expectedRevision = Int32.MaxValue || not (validBinding binding) then
        reject "invalid update revision or binding"
    elif not (validCaps total roles) then
        reject "invalid update caps"
    elif schema = 1 && (Option.isNone total || Map.isEmpty roles) then
        reject "schema-1 update requires both dimensions"
    else
        let next : OptionalPolicy =
            { Schema = schema; Project = binding.ProjectId; Ledger = binding.LedgerId
              Epoch = binding.Epoch; Revision = expectedRevision + 1; Total = total; Roles = roles
              AutomationEnabled = automationEnabled }
        let optionalTotal = match total with Some n -> Nullable n | None -> Nullable()
        let automationRecord enabled =
            {| Schema = 3; Project = next.Project; Ledger = next.Ledger
               Epoch = next.Epoch; Revision = next.Revision; Total = optionalTotal
               Roles = next.Roles; AutomationEnabled = enabled |}
        let content, request =
            if schema = 1 then
                // Preserve the original schema-1 request encoding for replay.
                let record = legacy next (Option.get total)
                JsonSerializer.Serialize record,
                JsonSerializer.Serialize {| expectedRevision = expectedRevision; policy = record |}
            elif schema = 3 then
                let record = automationRecord automationEnabled
                JsonSerializer.Serialize record,
                JsonSerializer.Serialize {| expectedRevision = expectedRevision; policy = record |}
            else
                let record =
                    {| Schema = next.Schema; Project = next.Project; Ledger = next.Ledger
                       Epoch = next.Epoch; Revision = next.Revision
                       Total = optionalTotal; Roles = next.Roles |}
                JsonSerializer.Serialize record,
                JsonSerializer.Serialize {| expectedRevision = expectedRevision; policy = record |}
        let intent = { OperationId = operation; Actor = actor; Kind = "ProjectCapacityPolicy.Update"; RequestJson = request }
        LedgerStore.transact binding intent (fun snapshot ->
            readPolicy binding snapshot expectedRevision
            |> Result.bind (fun (previous, oid) ->
                if schema = 1 && previous.Revision > 0 && previous.Schema <> 1 then
                    reject "cannot downgrade optional policy"
                else
                    requireRoles binding snapshot roles
                    |> Result.map (fun () ->
                        // A capacity-only writer cannot implicitly enable or disable Automation.
                        // Preserve the legacy request encoding for exact operation replay.
                        let updated =
                            if schema = 2 && previous.Schema = 3 then
                                JsonSerializer.Serialize(automationRecord previous.AutomationEnabled)
                            else content
                        [{ Path = path; ExpectedBlobOid = oid; Content = updated }])))

// Explicitly replacing with disabled caps advances the revision; never delete.
let updateOptional binding actor operation expectedRevision total roles =
    updateCore 2 false binding actor operation expectedRevision total roles

// One revision and one CAS for the selected project's switch and capacity settings.
let updateAutomation binding actor operation expectedRevision enabled total roles =
    updateCore 3 enabled binding actor operation expectedRevision total roles

let update binding actor operation expectedRevision total roles =
    updateCore 1 false binding actor operation expectedRevision (Some total) roles
