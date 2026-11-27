module Client.ChallengeRolePolicy

type Endpoint =
    | Role of string
    | HumanOperator

type Principal =
    | AgentRoles of projectId: string * roleIds: string list
    | TrustedOperator of projectId: string

type Owner =
    | OwnedBy of Principal
    | Unassigned

type Policy =
    { ProjectId: string
      PrivilegedRoleId: string
      AgentRoles: Set<string>
      Edges: Set<Endpoint * Endpoint>
      DispatchRoles: Set<string> }

type Denial =
    | MissingPolicy
    | InvalidPolicy
    | WrongProject
    | MissingOrMixedRoles
    | UnknownRole of string
    | PrivilegedRoleAttachment
    | InvalidTargets
    | DispatchDenied of Endpoint
    | EdgeDenied of source: Endpoint * target: Endpoint * allowedRoutes: Set<Endpoint>
    | HumanResponseRequired

let jarvisDefault projectId =
    let ordinary = set ["Worker"; "Reviewer"; "Adjudicator"; "Integrator"; "Health"; "Manager"; "Interpreter"]
    { ProjectId = projectId
      PrivilegedRoleId = "Operator"
      AgentRoles = ordinary.Add "Lead"
      Edges = Set.add (Role "Lead", HumanOperator) (ordinary |> Set.map (fun role -> Role role, Role "Lead"))
      DispatchRoles = set ["Lead"] }

// Composition supplies authenticated principals and policy from the same
// verified snapshot. This value-level decision does not authenticate callers
// or authorize configuration mutations.
let private nullRef value = obj.ReferenceEquals(value, null)

let private validId (value: string) =
    not (System.String.IsNullOrWhiteSpace value) && value = value.Trim()

let private validEndpoint (policy: Policy) endpoint =
    if nullRef endpoint then false
    else
        match endpoint with
        | HumanOperator -> true
        | Role role -> policy.AgentRoles.Contains role

let private validPolicy (policy: Policy) =
    not (nullRef policy)
    && validId policy.ProjectId
    && validId policy.PrivilegedRoleId
    && not (nullRef policy.AgentRoles)
    && not (nullRef policy.Edges)
    && not (nullRef policy.DispatchRoles)
    && Set.forall validId policy.AgentRoles
    && not (policy.AgentRoles.Contains policy.PrivilegedRoleId)
    && Set.isSubset policy.DispatchRoles policy.AgentRoles
    && (policy.Edges |> Set.forall (fun edge ->
        not (nullRef edge)
        && validEndpoint policy (fst edge)
        && validEndpoint policy (snd edge)))

let private withPolicy policy action =
    match policy with
    | None -> Error MissingPolicy
    | Some config when validPolicy config -> action config
    | Some _ -> Error InvalidPolicy

let private endpointOf (policy: Policy) principal =
    if nullRef principal then Error MissingOrMixedRoles
    else
        match principal with
        | TrustedOperator projectId ->
            if projectId = policy.ProjectId then Ok HumanOperator else Error WrongProject
        | AgentRoles (projectId, roles) ->
            if projectId <> policy.ProjectId then Error WrongProject
            elif nullRef roles then Error MissingOrMixedRoles
            else
                match roles with
                | [role] when role = policy.PrivilegedRoleId -> Error PrivilegedRoleAttachment
                | [role] when policy.AgentRoles.Contains role -> Ok (Role role)
                | [role] -> Error (UnknownRole role)
                | _ -> Error MissingOrMixedRoles

let private checkEdge (policy: Policy) source target =
    if policy.Edges.Contains (source, target) then Ok ()
    else
        let routes =
            policy.Edges
            |> Seq.choose (fun (fromRole, toRole) -> if fromRole = source then Some toRole else None)
            |> Set.ofSeq
        Error (EdgeDenied(source, target, routes))

let private checkTargets policy sources owners =
    let rec loop remaining =
        match remaining with
        | [] -> Ok ()
        | owner :: rest ->
            if nullRef owner then Error InvalidTargets
            else
                match owner with
                | Unassigned -> loop rest
                | OwnedBy principal ->
                    endpointOf policy principal
                    |> Result.bind (fun target ->
                        sources
                        |> List.fold (fun result source ->
                            result |> Result.bind (fun () -> checkEdge policy source target)) (Ok ()))
                    |> Result.bind (fun () -> loop rest)
    if nullRef owners || List.isEmpty owners then Error InvalidTargets
    else loop owners

let private dispatchEndpoint policy principal =
    endpointOf policy principal
    |> Result.bind (fun endpoint ->
        match endpoint with
        | Role role when policy.DispatchRoles.Contains role -> Ok endpoint
        | _ -> Error (DispatchDenied endpoint))

let authorizeAdmission policy actor owners =
    withPolicy policy (fun config ->
        endpointOf config actor
        |> Result.bind (fun source -> checkTargets config [source] owners))

let authorizeSubscription policy actor =
    withPolicy policy (fun config -> dispatchEndpoint config actor |> Result.map ignore)

// Both the subscribed dispatcher and the original issuer must authorize
// every concrete target; dispatch cannot confer missing escalation rights.
let authorizeDispatch policy actor issuer owners =
    withPolicy policy (fun config ->
        dispatchEndpoint config actor
        |> Result.bind (fun source ->
            endpointOf config issuer
            |> Result.bind (fun original -> checkTargets config [source; original] owners)))

let authorizeHumanResponse projectId principal =
    if not (validId projectId) then Error WrongProject
    elif nullRef principal then Error HumanResponseRequired
    else
        match principal with
        | TrustedOperator actual when actual = projectId -> Ok ()
        | TrustedOperator _ -> Error WrongProject
        | AgentRoles _ -> Error HumanResponseRequired
