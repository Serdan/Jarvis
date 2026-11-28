module ChallengeRolePolicyTests

open NUnit.Framework
open Client.ChallengeRolePolicy

[<Literal>]
let project = "Jarvis"

let agent role =
    let principal = AgentRoles(project, [role])
    match principal with
    | AgentRoles(id, roles) when id = "Jarvis" && roles = [role] -> principal
    | _ -> failwith "Fixture agent principal is invalid"

let human () =
    let principal = TrustedOperator project
    match principal with
    | TrustedOperator id when id = "Jarvis" -> principal
    | _ -> failwith "Fixture human principal is invalid"

let owner role = OwnedBy(agent role)

let checkedConfig () =
    let config = jarvisDefault project
    if obj.ReferenceEquals(config, null) then failwith "Fixture config is null"
    if obj.ReferenceEquals(config.AgentRoles, null)
       || obj.ReferenceEquals(config.Edges, null)
       || obj.ReferenceEquals(config.DispatchRoles, null) then
        failwith "Fixture config sets are null"
    let ordinary = set ["Worker"; "Reviewer"; "Adjudicator"; "Integrator"; "Interpreter"]
    let expectedEdges = Set.add (Role "Lead", HumanOperator) (ordinary |> Set.map (fun role -> Role role, Role "Lead"))
    if config.ProjectId <> "Jarvis"
       || config.PrivilegedRoleId <> "Operator"
       || config.AgentRoles <> ordinary.Add "Lead"
       || config.Edges <> expectedEdges
       || config.DispatchRoles <> set ["Lead"] then
        failwith "Fixture config does not match the default graph"
    config

let admission actor owners = authorizeAdmission (Some (checkedConfig ())) actor owners

let expectDenied result =
    match result with
    | Error _ -> ()
    | Ok () -> Assert.Fail("Unexpected permission")

let expectAllowed result =
    match result with
    | Ok () -> ()
    | Error denial -> Assert.Fail(sprintf "Unexpected denial: %A" denial)

[<Test>]
let ``Default role graph grants exactly the configured edges`` () =
    let roles = ["Worker"; "Reviewer"; "Adjudicator"; "Integrator"; "Interpreter"; "Lead"]
    for source in roles do
        for target in roles do
            let expected = source <> "Lead" && target = "Lead"
            if (admission (agent source) [owner target] = Ok ()) <> expected then
                Assert.Fail($"{source} -> {target}: expected permission {expected}")
        if (admission (agent source) [OwnedBy(human ())] = Ok ()) <> (source = "Lead") then
            Assert.Fail($"{source} -> human: unexpected permission decision")
    expectAllowed (admission (agent "Lead") [OwnedBy(human ())])

[<Test>]
let ``Denial gives configured escalation route`` () =
    match admission (agent "Worker") [OwnedBy(human ())] with
    | Error (EdgeDenied(Role "Worker", HumanOperator, routes)) ->
        if routes <> set [Role "Lead"] then Assert.Fail(sprintf "Unexpected routes: %A" routes)
    | result -> Assert.Fail(sprintf "Unexpected decision: %A" result)

[<Test>]
let ``Explicit edges enable peers and Interpreter without name inference`` () =
    let config = checkedConfig ()
    let changed = { config with Edges = set [Role "Worker", Role "Reviewer"; Role "Interpreter", HumanOperator] }
    expectAllowed (authorizeAdmission (Some changed) (agent "Worker") [owner "Reviewer"])
    expectAllowed (authorizeAdmission (Some changed) (agent "Interpreter") [OwnedBy(human ())])
    expectDenied (authorizeAdmission (Some changed) (agent "Worker") [owner "Lead"])
    expectDenied (authorizeAdmission (Some changed) (agent "Lead") [OwnedBy(human ())])

[<Test>]
let ``Missing unknown mixed and legacy privileged membership fails closed`` () =
    let config = checkedConfig ()
    let invalid = [
        AgentRoles(project, [])
        AgentRoles(project, ["Lead"; "Worker"])
        AgentRoles(project, ["Lead"; "Lead"])
        AgentRoles(project, ["Lead"; "Unknown"])
        agent "lead"
        agent "Operator"
        agent "Lead "
        agent null
        AgentRoles(project, Unchecked.defaultof<string list>)
        AgentRoles("Other", ["Lead"])
    ]
    for principal in invalid do
        expectDenied (admission principal [OwnedBy(human ())])
        expectDenied (admission (agent "Worker") [OwnedBy principal])
        expectDenied (authorizeSubscription (Some config) principal)
    expectDenied (admission (agent "Lead") [OwnedBy(TrustedOperator "Other")])

[<Test>]
let ``Policy absence invalid endpoints and reserved role fail closed`` () =
    let config = checkedConfig ()
    expectDenied (authorizeAdmission None (agent "Lead") [OwnedBy(human ())])
    for malformed in [
        { config with Edges = Set.add (Role "Unknown", HumanOperator) config.Edges }
        { config with AgentRoles = Set.add "Operator" config.AgentRoles }
        { config with DispatchRoles = set ["Unknown"] }
        { config with ProjectId = "" }
        { config with Edges = Unchecked.defaultof<_> }
    ] do
        expectDenied (authorizeAdmission (Some malformed) (agent "Lead") [OwnedBy(human ())])
        expectDenied (authorizeSubscription (Some malformed) (agent "Lead"))

[<Test>]
let ``All targets including suggestions must pass with no partial decision`` () =
    expectAllowed (admission (agent "Worker") [Unassigned; owner "Lead"])
    expectDenied (admission (agent "Worker") [owner "Lead"; OwnedBy(human ())])
    expectDenied (admission (agent "Worker") [Unassigned; OwnedBy(human ())])
    expectDenied (admission (agent "Lead") [])
    expectDenied (admission (agent "Lead") Unchecked.defaultof<_>)

[<Test>]
let ``Dispatch subscription is explicit and cannot launder the issuer or actor edge`` () =
    let config = checkedConfig ()
    expectAllowed (authorizeSubscription (Some config) (agent "Lead"))
    expectDenied (authorizeSubscription (Some config) (agent "Worker"))
    expectDenied (authorizeDispatch (Some config) (agent "Lead") (agent "Worker") [OwnedBy(human ())])
    expectDenied (authorizeDispatch (Some config) (agent "Worker") (agent "Lead") [OwnedBy(human ())])
    let changed = { config with Edges = config.Edges.Add(Role "Lead", Role "Lead").Add(Role "Worker", Role "Reviewer").Add(Role "Lead", Role "Reviewer") }
    expectAllowed (authorizeDispatch (Some changed) (agent "Lead") (agent "Worker") [owner "Reviewer"])
    expectAllowed (authorizeDispatch (Some changed) (agent "Lead") (agent "Worker") [owner "Lead"])
    expectDenied (authorizeDispatch (Some { changed with DispatchRoles = Set.empty }) (agent "Lead") (agent "Worker") [owner "Reviewer"])
    expectDenied (authorizeDispatch (Some changed) (agent "Lead") (agent "Worker") [owner "Reviewer"; OwnedBy(human ())])

[<Test>]
let ``Human-only response is separate from fresh escalation admission`` () =
    expectAllowed (authorizeHumanResponse project (human ()))
    expectDenied (authorizeHumanResponse project (agent "Operator"))
    expectDenied (authorizeHumanResponse project (agent "Lead"))
    expectDenied (authorizeHumanResponse project (TrustedOperator "Other"))
    expectDenied (authorizeHumanResponse "" (TrustedOperator ""))

[<TestCase("Health")>]
[<TestCase("Manager")>]
let ``Retired roles are unknown throughout default policy`` role =
    let config = Some (jarvisDefault project)
    let retired = agent role
    let lead = agent "Lead"
    let worker = agent "Worker"
    let expectUnknown result =
        match result with
        | Error (UnknownRole actual) when actual = role -> ()
        | decision -> Assert.Fail(sprintf "Expected unknown role %s, got %A" role decision)
    expectUnknown (authorizeAdmission config retired [owner "Lead"])
    expectUnknown (authorizeAdmission config retired [Unassigned])
    expectUnknown (authorizeAdmission config worker [OwnedBy retired])
    expectUnknown (authorizeSubscription config retired)
    expectUnknown (authorizeDispatch config retired worker [Unassigned])
    expectUnknown (authorizeDispatch config lead retired [Unassigned])
    expectUnknown (authorizeDispatch config lead worker [OwnedBy retired])
    expectDenied (authorizeAdmission config (AgentRoles(project, ["Lead"; role])) [OwnedBy(human ())])
    expectDenied (authorizeHumanResponse project retired)

[<TestCase("Health")>]
[<TestCase("Manager")>]
let ``Explicit configuration can recognize a nondefault role without granting human authority`` role =
    let config = checkedConfig ()
    let configured =
        { config with
            AgentRoles = config.AgentRoles.Add role
            Edges = config.Edges.Add(Role role, Role "Lead") }
    expectAllowed (authorizeAdmission (Some configured) (agent role) [owner "Lead"])
    expectDenied (authorizeAdmission (Some configured) (agent role) [OwnedBy(human ())])
    expectDenied (authorizeHumanResponse project (agent role))
