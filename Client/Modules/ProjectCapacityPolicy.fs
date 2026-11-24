module Client.ProjectCapacityPolicy

open System
open System.Text.Json
open Client.LedgerStore

// This component is not registered with the runtime and grants no admission.
[<CLIMutable>]
type Policy =
    { Schema: int
      Project: Guid
      Ledger: Guid
      Epoch: int
      Revision: int
      Total: int
      Roles: Map<string, int> }

let private path = "records/project-capacity-policy/policy.json"
let private reject message = Error(InvalidBinding ("Project capacity policy: " + message))
let private validRoles roles =
    not (isNull (box roles)) && not (Map.isEmpty roles)
    && (roles |> Map.forall (fun k v -> not (String.IsNullOrWhiteSpace k) && k = k.Trim() && v > 0))

let private valid (binding: LedgerBinding) (p: Policy) =
    not (isNull (box p)) && p.Schema = 1
    && binding.ProjectId <> Guid.Empty && binding.LedgerId <> Guid.Empty && binding.Epoch > 0
    && p.Project = binding.ProjectId && p.Ledger = binding.LedgerId && p.Epoch = binding.Epoch
    && p.Revision > 0 && p.Total > 0 && validRoles p.Roles

// Must be called inside the admission transaction callback, on its snapshot.
// Exact expected revision is mandatory; an absent role is unknown, not unlimited.
let readForRole (binding: LedgerBinding) snapshot expectedRevision role =
    if expectedRevision < 1 || String.IsNullOrWhiteSpace role || role <> role.Trim() then
        reject "invalid expected revision or role"
    else
        LedgerStore.readAt binding snapshot path
        |> Result.bind (function
            | Some raw, Some oid ->
                try
                    // Require exactly the known fields, once each; JsonSerializer otherwise
                    // accepts duplicate or omitted fields and silently defaults missing values.
                    use doc = JsonDocument.Parse(raw)
                    let root = doc.RootElement
                    let fields = root.EnumerateObject() |> Seq.map (fun p -> p.Name) |> Seq.toList
                    let expected = [ "Schema"; "Project"; "Ledger"; "Epoch"; "Revision"; "Total"; "Roles" ]
                    let roles = root.GetProperty("Roles")
                    let names = roles.EnumerateObject() |> Seq.map (fun p -> p.Name) |> Seq.toList
                    let p = JsonSerializer.Deserialize<Policy>(raw)
                    if List.sort fields <> List.sort expected || List.length names <> (names |> List.distinct |> List.length)
                       || not (valid binding p) || p.Revision <> expectedRevision || not (Map.containsKey role p.Roles)
                    then reject "invalid, stale, mismatched, or unknown policy"
                    else Ok(p, oid)
                with _ -> reject "malformed policy"
            | _ -> reject "policy absent or inconsistent")

// Revision zero means create-only. Existing records are validated before replacement.
// Operation ID and full request are bound by LedgerStore's authenticated CAS receipt.
let update (binding: LedgerBinding) actor operation expectedRevision total roles =
    if expectedRevision < 0 || expectedRevision = Int32.MaxValue || total < 1 || not (validRoles roles)
       || binding.ProjectId = Guid.Empty || binding.LedgerId = Guid.Empty || binding.Epoch < 1 then
        reject "invalid update"
    else
        let next = { Schema = 1; Project = binding.ProjectId; Ledger = binding.LedgerId
                     Epoch = binding.Epoch; Revision = expectedRevision + 1; Total = total; Roles = roles }
        let request = JsonSerializer.Serialize {| expectedRevision = expectedRevision; policy = next |}
        let intent = { OperationId = operation; Actor = actor; Kind = "ProjectCapacityPolicy.Update"; RequestJson = request }
        LedgerStore.transact binding intent (fun snapshot ->
            LedgerStore.readAt binding snapshot path
            |> Result.bind (fun (raw, oid) ->
                if expectedRevision = 0 then
                    match raw, oid with
                    | None, None -> Ok [{ Path = path; ExpectedBlobOid = None; Content = JsonSerializer.Serialize next }]
                    | _ -> reject "policy already exists"
                else
                    // Validate the old record and revision without assuming a role from
                    // the replacement exists in the predecessor.
                    match raw, oid with
                    | Some old, Some _ ->
                        try
                            use doc = JsonDocument.Parse(old)
                            let rs = doc.RootElement.GetProperty("Roles").EnumerateObject() |> Seq.map (fun x -> x.Name) |> Seq.toList
                            match rs with
                            | role :: _ ->
                                readForRole binding snapshot expectedRevision role
                                |> Result.map (fun (_, priorOid) ->
                                    [{ Path = path; ExpectedBlobOid = Some priorOid; Content = JsonSerializer.Serialize next }])
                            | [] -> reject "empty predecessor roles"
                        with _ -> reject "malformed predecessor"
                    | _ -> reject "predecessor missing"))
