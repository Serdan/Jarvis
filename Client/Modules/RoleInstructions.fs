module Client.RoleInstructions

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open FsToolkit.ErrorHandling
open Client.LedgerStore

// Role definitions and agent-specific instructions belong to the canonical
// project ledger, not to an assignment or a particular source checkout.
[<CLIMutable>]
type RoleDefinition =
    { RoleId: string
      Purpose: string
      Instructions: string
      Version: int
      UpdatedBy: string
      UpdatedSequence: int64 }

[<CLIMutable>]
type AgentInstructionConfig =
    { AgentId: string
      RoleIds: string list
      Instructions: string
      Version: int
      UpdatedBy: string
      UpdatedSequence: int64 }

[<CLIMutable>]
type RoleSnapshot =
    { SnapshotCommit: string
      BlobOid: string
      Role: RoleDefinition }

[<CLIMutable>]
type AgentInstructionSnapshot =
    { SnapshotCommit: string
      BlobOid: string
      Config: AgentInstructionConfig }

[<CLIMutable>]
type InstructionLayer =
    { Scope: string
      Identifier: string
      Version: int
      SourcePath: string
      BlobOid: string
      Sha256: string
      Content: string }

[<CLIMutable>]
type InstructionResolution =
    { SnapshotCommit: string
      RoleIds: string list
      AgentConfigVersion: int
      // Higher entries in this hierarchy retain priority over later entries.
      // Role documents have deterministic attachment order within their tier.
      Precedence: string list
      Documents: InstructionLayer list }

module RoleInstructions =
    let private options =
        JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                              PropertyNameCaseInsensitive = true)
    let private utf8 = Encoding.UTF8
    let private roleIdPattern = Regex("^[A-Za-z0-9_-]{1,80}$", RegexOptions.Compiled)
    let private isRoleId (value: string) =
        not (isNull value) && roleIdPattern.IsMatch value
    let private isAgentId (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && utf8.GetByteCount value <= 120
        && not (value |> Seq.exists Char.IsControl)
    // A body is serialized once in the ledger and again in inbox/explicit
    // instruction responses. Bounding the escaped JSON size (not merely UTF-8
    // source bytes) keeps 16 roles plus one agent override below the 640 KiB
    // explicit instruction page budget even for control-heavy text.
    let private maxEscapedBodyBytes = 16 * 1024
    let private validBody allowEmpty (value: string) =
        not (isNull value)
        && (allowEmpty || not (String.IsNullOrWhiteSpace value))
        && utf8.GetByteCount value <= 32768
        && utf8.GetByteCount(JsonSerializer.Serialize(value, options)) <= maxEscapedBodyBytes
    let private validPurpose (value: string) =
        not (String.IsNullOrWhiteSpace value) && utf8.GetByteCount value <= 4096
    let private validRoles (values: string list) =
        not (isNull (box values))
        && values.Length <= 16
        && (values |> List.forall isRoleId)
        && ((values |> List.distinct).Length = values.Length)
    let private rolePath id = $"records/roles/{id}.json"
    let private agentKey (id: string) =
        Convert.ToBase64String(utf8.GetBytes id).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    let private agentPath id = $"agents/{agentKey id}.json"
    let private configPath id = $"records/agent-instructions/{agentKey id}.json"
    let private json value = JsonSerializer.Serialize(value, options)
    let private entityJson value =
        let value = json value
        "{\"schemaVersion\":1," + value.Substring(1)
    let private hash (value: string) =
        SHA256.HashData(utf8.GetBytes value)
        |> Convert.ToHexString |> fun value -> "sha256:" + value.ToLowerInvariant()
    let private intent operationId actor kind payload : LedgerIntent =
        { OperationId = operationId
          Actor = actor
          Kind = kind
          RequestJson = json payload }
    let private change path before content : LedgerFileChange =
        { Path = path; ExpectedBlobOid = before; Content = content }
    let private invalid (path: string) reason =
        LedgerIntegrityError $"Invalid ledger instruction entity {path}: {reason}"

    let private readEntity<'t> (isValid: 't -> bool) binding snapshot path =
        result {
            let! contents, oid = LedgerStore.readAt binding snapshot path
            match contents, oid with
            | None, None -> return None
            | Some raw, Some blob ->
                let parsed =
                    try
                        use doc = JsonDocument.Parse raw
                        let root = doc.RootElement
                        if root.ValueKind <> JsonValueKind.Object then
                            Error(invalid path "Expected JSON object.")
                        else
                            let seen = HashSet<string>(StringComparer.OrdinalIgnoreCase)
                            let unique = root.EnumerateObject() |> Seq.forall (fun item -> seen.Add item.Name)
                            let mutable schema = Unchecked.defaultof<JsonElement>
                            let validSchema =
                                root.TryGetProperty("schemaVersion", &schema)
                                && schema.ValueKind = JsonValueKind.Number
                                && schema.GetInt32() = 1
                            let required =
                                typeof<'t>.GetProperties()
                                |> Array.map (fun prop -> options.PropertyNamingPolicy.ConvertName(prop.Name))
                                |> Array.forall (fun prop ->
                                    let mutable value = Unchecked.defaultof<JsonElement>
                                    root.TryGetProperty(prop, &value))
                            if not unique || not validSchema || not required then
                                Error(invalid path "Missing, duplicate or unsupported record fields.")
                            else
                                let value = JsonSerializer.Deserialize<'t>(raw, options)
                                if isNull (box value) || not (isValid value) then
                                    Error(invalid path "Invalid version, identity or content.")
                                else Ok value
                    with ex -> Error(invalid path ex.Message)
                let! value = parsed
                return Some(value, blob)
            | _ -> return! Error(invalid path "Inconsistent blob reference.")
        }

    let private validRole (role: RoleDefinition) =
        isRoleId role.RoleId
        && validPurpose role.Purpose
        && validBody false role.Instructions
        && role.Version >= 1
        && not (String.IsNullOrWhiteSpace role.UpdatedBy)
        && role.UpdatedSequence > 0L

    let private validConfig (config: AgentInstructionConfig) =
        isAgentId config.AgentId
        && validRoles config.RoleIds
        && validBody true config.Instructions
        && config.Version >= 1
        && not (String.IsNullOrWhiteSpace config.UpdatedBy)
        && config.UpdatedSequence > 0L

    let private readRoleAt binding snapshot roleId =
        result {
            let! current = readEntity validRole binding snapshot (rolePath roleId)
            match current with
            | Some(role, blob) when role.RoleId = roleId -> return Some(role, blob)
            | Some _ -> return! Error(invalid (rolePath roleId) "Role ID differs from its ledger path.")
            | None -> return None
        }

    let private readConfigAt binding snapshot agentId =
        result {
            let! current = readEntity validConfig binding snapshot (configPath agentId)
            match current with
            | Some(config, blob) when config.AgentId = agentId -> return Some(config, blob)
            | Some _ -> return! Error(invalid (configPath agentId) "Agent ID differs from its ledger path.")
            | None -> return None
        }

    let private requireRegisteredAgent binding snapshot agentId =
        result {
            let! content, _ = LedgerStore.readAt binding snapshot (agentPath agentId)
            match content with
            | None -> return! Error(StaleEntityVersion $"Agent {agentId} is not registered.")
            | Some raw ->
                try
                    use doc = JsonDocument.Parse raw
                    let id = doc.RootElement.GetProperty("agentId").GetString()
                    if id <> agentId then
                        return! Error(invalid (agentPath agentId) "Agent identity mismatch.")
                    return ()
                with ex ->
                    return! Error(invalid (agentPath agentId) ex.Message)
        }

    let private ensureRole binding snapshot roleId =
        result {
            let! current = readRoleAt binding snapshot roleId
            match current with
            | Some(role, oid) -> return role, oid
            | None -> return! Error(StaleEntityVersion $"Missing referenced role: {roleId}")
        }

    let private priorConfig binding snapshot agentId expectedVersion =
        result {
            let! existing = readConfigAt binding snapshot agentId
            let version = existing |> Option.map (fun (value, _) -> value.Version) |> Option.defaultValue 0
            if version <> expectedVersion then
                return! Error(StaleEntityVersion $"Agent instruction version conflict for {agentId}: expected {expectedVersion}, current {version}.")
            let roles = existing |> Option.map (fun (value, _) -> value.RoleIds) |> Option.defaultValue []
            let body = existing |> Option.map (fun (value, _) -> value.Instructions) |> Option.defaultValue ""
            let oid = existing |> Option.map snd
            return roles, body, oid
        }

    let private editRole binding actor operationId roleId expectedVersion purpose instructions =
        if not (isRoleId roleId) || expectedVersion < 0 || not (validPurpose purpose)
           || not (validBody false instructions) || String.IsNullOrWhiteSpace actor then
            Error(InvalidBinding "Invalid role ID, version, purpose or instructions.")
        else
            let kind = if expectedVersion = 0 then "CreateRole" else "UpdateRole"
            LedgerStore.transact binding
                (intent operationId actor kind
                    {| roleId = roleId; expectedVersion = expectedVersion
                       purpose = purpose; instructionsSha256 = hash instructions |})
                (fun snapshot -> result {
                    let! existing = readRoleAt binding snapshot roleId
                    let version = existing |> Option.map (fun (value, _) -> value.Version) |> Option.defaultValue 0
                    if version <> expectedVersion then
                        return! Error(StaleEntityVersion $"Role version conflict for {roleId}: expected {expectedVersion}, current {version}.")
                    let next =
                        { RoleId = roleId; Purpose = purpose; Instructions = instructions
                          Version = expectedVersion + 1; UpdatedBy = actor
                          UpdatedSequence = snapshot.Header.NextSequence }
                    return [ change (rolePath roleId) (existing |> Option.map snd) (entityJson next) ]
                })

    let createRole binding actor operationId roleId purpose instructions =
        editRole binding actor operationId roleId 0 purpose instructions

    let updateRole binding actor operationId roleId expectedVersion purpose instructions =
        if expectedVersion < 1 then Error(InvalidBinding "An existing role version is required.")
        else editRole binding actor operationId roleId expectedVersion purpose instructions

    let attachRole binding actor operationId agentId roleId expectedVersion =
        if not (isAgentId agentId) || not (isRoleId roleId) || expectedVersion < 0
           || String.IsNullOrWhiteSpace actor then
            Error(InvalidBinding "Invalid role attachment arguments.")
        else
            LedgerStore.transact binding
                (intent operationId actor "AttachRole"
                    {| agentId = agentId; roleId = roleId; expectedVersion = expectedVersion |})
                (fun snapshot -> result {
                    do! requireRegisteredAgent binding snapshot agentId
                    let! _ = ensureRole binding snapshot roleId
                    let! roles, body, oid = priorConfig binding snapshot agentId expectedVersion
                    if List.contains roleId roles then
                        return! Error(StaleEntityVersion $"Role {roleId} is already attached to {agentId}.")
                    if roles.Length >= 16 then
                        return! Error(InvalidBinding "An agent cannot have more than 16 roles.")
                    let next =
                        { AgentId = agentId; RoleIds = roles @ [roleId]; Instructions = body
                          Version = expectedVersion + 1; UpdatedBy = actor
                          UpdatedSequence = snapshot.Header.NextSequence }
                    return [ change (configPath agentId) oid (entityJson next) ]
                })

    let detachRole binding actor operationId agentId roleId expectedVersion =
        if not (isAgentId agentId) || not (isRoleId roleId) || expectedVersion < 1
           || String.IsNullOrWhiteSpace actor then
            Error(InvalidBinding "Invalid role detachment arguments.")
        else
            LedgerStore.transact binding
                (intent operationId actor "DetachRole"
                    {| agentId = agentId; roleId = roleId; expectedVersion = expectedVersion |})
                (fun snapshot -> result {
                    do! requireRegisteredAgent binding snapshot agentId
                    let! roles, body, oid = priorConfig binding snapshot agentId expectedVersion
                    if not (List.contains roleId roles) then
                        return! Error(StaleEntityVersion $"Role {roleId} is not attached to {agentId}.")
                    let next =
                        { AgentId = agentId; RoleIds = roles |> List.filter ((<>) roleId)
                          Instructions = body; Version = expectedVersion + 1
                          UpdatedBy = actor; UpdatedSequence = snapshot.Header.NextSequence }
                    return [ change (configPath agentId) oid (entityJson next) ]
                })

    let setAgentInstructions binding actor operationId agentId expectedVersion instructions =
        if not (isAgentId agentId) || expectedVersion < 0
           || not (validBody true instructions) || String.IsNullOrWhiteSpace actor then
            Error(InvalidBinding "Invalid agent instruction update.")
        else
            LedgerStore.transact binding
                (intent operationId actor "SetAgentInstructions"
                    {| agentId = agentId; expectedVersion = expectedVersion
                       instructionsSha256 = hash instructions |})
                (fun snapshot -> result {
                    do! requireRegisteredAgent binding snapshot agentId
                    let! roles, _, oid = priorConfig binding snapshot agentId expectedVersion
                    let next =
                        { AgentId = agentId; RoleIds = roles; Instructions = instructions
                          Version = expectedVersion + 1; UpdatedBy = actor
                          UpdatedSequence = snapshot.Header.NextSequence }
                    return [ change (configPath agentId) oid (entityJson next) ]
                })

    let getRole binding roleId =
        if not (isRoleId roleId) then Error(InvalidBinding "Invalid role ID.")
        else result {
            let! snapshot = LedgerStore.fetch binding
            let! role, oid = ensureRole binding snapshot roleId
            return { SnapshotCommit = snapshot.Commit; BlobOid = oid; Role = role }
        }

    let listRoles binding =
        result {
            let! snapshot = LedgerStore.fetch binding
            let! paths = LedgerStore.listAt binding snapshot "records/roles"
            let mutable found = []
            for path in paths do
                if path.StartsWith("records/roles/", StringComparison.Ordinal)
                   && path.EndsWith(".json", StringComparison.Ordinal) then
                    let id = path.Substring("records/roles/".Length)
                                    .Substring(0, path.Length - "records/roles/".Length - ".json".Length)
                    if not (isRoleId id) then
                        return! Error(invalid path "Invalid role path.")
                    let! role, blob = ensureRole binding snapshot id
                    found <- { SnapshotCommit = snapshot.Commit; BlobOid = blob; Role = role } :: found
            return found |> List.sortBy (fun entry -> entry.Role.RoleId)
        }

    let getAgentConfig binding agentId =
        if not (isAgentId agentId) then Error(InvalidBinding "Invalid agent ID.")
        else result {
            let! snapshot = LedgerStore.fetch binding
            do! requireRegisteredAgent binding snapshot agentId
            let! current = readConfigAt binding snapshot agentId
            return
                current |> Option.map (fun (config, blob) ->
                    { SnapshotCommit = snapshot.Commit; BlobOid = blob; Config = config })
        }

    let resolveAtKnownAgent binding snapshot agentId =
        if not (isAgentId agentId) then Error(InvalidBinding "Invalid agent ID.")
        else result {
            let! current = readConfigAt binding snapshot agentId
            let roles =
                current |> Option.map (fun (config, _) -> config.RoleIds) |> Option.defaultValue []
            let mutable layers = []
            for roleId in roles do
                let! role, blob = ensureRole binding snapshot roleId
                layers <-
                    { Scope = "role"; Identifier = roleId; Version = role.Version
                      SourcePath = rolePath roleId; BlobOid = blob
                      Sha256 = hash role.Instructions; Content = role.Instructions } :: layers
            match current with
            | Some(config, blob) when not (String.IsNullOrWhiteSpace config.Instructions) ->
                layers <-
                    { Scope = "agent"; Identifier = agentId; Version = config.Version
                      SourcePath = configPath agentId; BlobOid = blob
                      Sha256 = hash config.Instructions; Content = config.Instructions } :: layers
            | _ -> ()
            return
                { SnapshotCommit = snapshot.Commit; RoleIds = roles
                  AgentConfigVersion =
                      current |> Option.map (fun (config, _) -> config.Version) |> Option.defaultValue 0
                  Precedence = [ "global"; "workspace"; "project/directory"; "role (attachment order)"
                                 "agent"; "assignment" ]
                  Documents = List.rev layers }
        }

    let resolveAt binding snapshot agentId =
        result {
            do! requireRegisteredAgent binding snapshot agentId
            return! resolveAtKnownAgent binding snapshot agentId
        }

    let resolve binding agentId =
        result {
            let! snapshot = LedgerStore.fetch binding
            return! resolveAt binding snapshot agentId
        }
