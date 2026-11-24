module ServerManualGrantTests

open System
open Common
open Server
open Server.Services
open NUnit.Framework

let private owner = "account:test-owner"
let private projectId = "123e4567-e89b-42d3-a456-426614174000"
let private deviceA = "device-A"
let private existingGrant (store: ManualGrantStore) project capabilities =
    match store.Request(owner, deviceA, "existing", project, "Initialize project setup", capabilities, Some projectId) with
    | Ok ticket -> ticket
    | Error reason -> failwith reason

let private prospectiveGrant (store: ManualGrantStore) project capabilities =
    match store.Request(owner, deviceA, "prospective", project, "Create project and configure work", capabilities, None) with
    | Ok ticket -> ticket
    | Error reason -> failwith reason

let private create project =
    CreateProjectCommand
        { Name = project; InitializeGit = true; CreateJarvisConfig = true
          CreateAgentInstructions = true; InitializeLedger = true
          RemoteUrl = "https://example.invalid/ledger.git"; AcknowledgeRemoteVisibility = true }

let private write project =
    WriteFileCommand
        { ProjectName = project; FilePath = "AGENTS.md"; Content = "guidance"
          FileWriteMode = FileWriteMode.Write; ExpectedHash = None; CreateParents = Some false }

let private accepted value =
    match value with
    | Ok _ -> true
    | Error _ -> false

[<Test>]
let ``manual grant credential is distinct canonical CSPRNG UUIDv4 and verifier is RAM-only`` () =
    let store = ManualGrantStore()
    let tokens =
        [ for _ in 1..128 ->
            (existingGrant store "Example" [ "Implementation" ]).Credential ]
    Assert.That(tokens.Length, Is.EqualTo(128))
    Assert.That(Set.ofList tokens |> Set.count, Is.EqualTo(128))
    Assert.That(tokens |> List.forall ManualGrantPolicy.canonicalUuidV4, Is.True)
    Assert.That(store.VerifierCount, Is.EqualTo(128))
    Assert.That(store.ToString(), Does.Not.Contain(tokens.Head))

[<Test>]
let ``pending credential cannot perform any project mutation`` () =
    let store = ManualGrantStore()
    let ticket = prospectiveGrant store "Example" [ "SchedulingAdmin"; "Implementation" ]
    Assert.That(store.Status(owner, ticket.Credential), Is.EqualTo(Some Pending))
    Assert.That(store.Authorize(owner, ticket.Credential, deviceA, create "Example", None) |> accepted, Is.False)
    Assert.That(store.Authorize(owner, ticket.Credential, deviceA, write "Example", None) |> accepted, Is.False)

[<Test>]
let ``only original account can approve a pending credential`` () =
    let store = ManualGrantStore()
    let ticket = existingGrant store "Example" [ "Implementation" ]
    Assert.That(store.Decide("another-account", ticket.Credential, true), Is.False)
    Assert.That(store.Authorize("another-account", ticket.Credential, deviceA, write "Example", None) |> accepted, Is.False)
    Assert.That(store.Status(owner, ticket.Credential), Is.EqualTo(Some Pending))
    Assert.That(store.Decide(owner, ticket.Credential, true), Is.True)
    Assert.That(store.Authorize(owner, ticket.Credential, deviceA, write "Example", Some projectId) |> accepted, Is.True)

[<Test>]
let ``denied manual grant never acquires mutation authority`` () =
    let store = ManualGrantStore()
    let ticket = existingGrant store "Example" [ "Implementation" ]
    Assert.That(store.Decide(owner, ticket.Credential, false), Is.True)
    Assert.That(store.Decide(owner, ticket.Credential, true), Is.False)
    Assert.That(store.Status(owner, ticket.Credential), Is.EqualTo(Some Denied))
    Assert.That(store.Authorize(owner, ticket.Credential, deviceA, write "Example", None) |> accepted, Is.False)

[<Test>]
let ``invalid UUID wrong project and unapproved operation family fail closed`` () =
    let store = ManualGrantStore()
    let ticket = existingGrant store "Example" [ "SchedulingAdmin" ]
    Assert.That(store.Decide(owner, ticket.Credential, true), Is.True)
    Assert.That(store.Authorize(owner, Guid.NewGuid().ToString("D"), deviceA, create "Example", None) |> accepted, Is.False)
    Assert.That(store.Authorize(owner, ticket.Credential.ToUpperInvariant(), deviceA, create "Example", None) |> accepted, Is.False)
    Assert.That(store.Authorize(owner, ticket.Credential, deviceA, create "Other", None) |> accepted, Is.False)
    Assert.That(store.Authorize(owner, ticket.Credential, deviceA, write "Example", None) |> accepted, Is.False)
    // Existing-project setup never permits new project creation, even with SchedulingAdmin.
    Assert.That(store.Authorize(owner, ticket.Credential, deviceA, create "Example", None) |> accepted, Is.False)

[<Test>]
let ``prospective grant binds only after exact canonical creation result`` () =
    let store = ManualGrantStore()
    let ticket = prospectiveGrant store "NamedProject" [ "SchedulingAdmin"; "Implementation" ]
    Assert.That(store.Decide(owner, ticket.Credential, true), Is.True)
    Assert.That(store.Authorize(owner, ticket.Credential, deviceA, write "NamedProject", None) |> accepted, Is.False)
    Assert.That((store.Authorize(owner, ticket.Credential, deviceA, create "NamedProject", None) = Ok true), Is.True)
    Assert.That(store.Status(owner, ticket.Credential), Is.EqualTo(Some Creating))
    Assert.That(store.Authorize(owner, ticket.Credential, deviceA, create "NamedProject", None) |> accepted, Is.False)
    Assert.That(store.FinishCreation(owner, ticket.Credential, Some ("NamedProject", projectId)), Is.True)
    Assert.That(store.Status(owner, ticket.Credential), Is.EqualTo(Some Created))
    Assert.That((store.Authorize(owner, ticket.Credential, deviceA, write "NamedProject", Some projectId) = Ok false), Is.True)
    Assert.That(store.Authorize(owner, ticket.Credential, deviceA, write "OtherProject", None) |> accepted, Is.False)
    Assert.That(store.Authorize(owner, ticket.Credential, deviceA, create "NamedProject", None) |> accepted, Is.False)

[<Test>]
let ``mismatched or unknown project creation receipt blocks further mutations`` () =
    let store = ManualGrantStore()
    let ticket = prospectiveGrant store "NamedProject" [ "SchedulingAdmin"; "Implementation" ]
    Assert.That(store.Decide(owner, ticket.Credential, true), Is.True)
    Assert.That((store.Authorize(owner, ticket.Credential, deviceA, create "NamedProject", None) = Ok true), Is.True)
    Assert.That(store.FinishCreation(owner, ticket.Credential, Some ("WrongProject", projectId)), Is.False)
    Assert.That(store.Status(owner, ticket.Credential), Is.EqualTo(Some CreationBlocked))
    Assert.That(store.Authorize(owner, ticket.Credential, deviceA, write "NamedProject", None) |> accepted, Is.False)

[<Test>]
let ``new server instance forgets manual approvals without changing scheduled Work`` () =
    let beforeRestart = ManualGrantStore()
    let ticket = existingGrant beforeRestart "Example" [ "Implementation" ]
    Assert.That(beforeRestart.Decide(owner, ticket.Credential, true), Is.True)
    Assert.That(beforeRestart.Authorize(owner, ticket.Credential, deviceA, write "Example", Some projectId) |> accepted, Is.True)
    let afterRestart = ManualGrantStore()
    Assert.That(afterRestart.VerifierCount, Is.Zero)
    Assert.That(afterRestart.Authorize(owner, ticket.Credential, deviceA, write "Example", None) |> accepted, Is.False)
    let newTicket = existingGrant afterRestart "Example" [ "Implementation" ]
    Assert.That(newTicket.Credential, Is.Not.EqualTo(ticket.Credential))

[<Test>]
let ``malformed scope names and capabilities never issue bearer`` () =
    let store = ManualGrantStore()
    for project in [ ""; "../Escape"; "Other::linked"; ".hidden"; "A/B" ] do
        Assert.That(store.Request(owner, deviceA, "prospective", project, "Purpose", [ "Implementation" ], None) |> accepted, Is.False)
    for capabilities in [ []; [ "Administrator" ]; [ "Implementation"; "Administrator" ] ] do
        Assert.That(store.Request(owner, deviceA, "existing", "Example", "Purpose", capabilities, Some projectId) |> accepted, Is.False)
    Assert.That(store.Request(owner, deviceA, "prospective", "Example", " ", [ "Implementation" ], None) |> accepted, Is.False)
    Assert.That(store.VerifierCount, Is.Zero)

[<Test>]
let ``manual grant delegates only compact Work operation families`` () =
    let allowed = AgentCommandInfo.manualGrantFamily (write "Example")
    Assert.That(allowed, Is.EqualTo(Some "Implementation"))
    Assert.That(AgentCommandInfo.manualGrantFamily (create "Example"), Is.EqualTo(Some "SchedulingAdmin"))
    Assert.That(AgentCommandInfo.manualGrantFamily ListProjectsCommand, Is.EqualTo(None))
    // No grant can impersonate a scheduled Work launch; it owns no Work ID.
    let store = ManualGrantStore()
    let ticket = existingGrant store "Example" [ "Implementation"; "SchedulingAdmin" ]
    Assert.That(store.Decide(owner, ticket.Credential, true), Is.True)
    Assert.That(store.Authorize(owner, ticket.Credential, deviceA, ListProjectsCommand, None) |> accepted, Is.False)

[<Test>]
let ``manual grants apply to registered worktrees only inside their named project`` () =
    let store = ManualGrantStore()
    let ticket = existingGrant store "Example" [ "Implementation" ]
    Assert.That(store.Decide(owner, ticket.Credential, true), Is.True)
    Assert.That((store.Authorize(owner, ticket.Credential, deviceA, write "Example::linked", Some projectId) = Ok false), Is.True)
    Assert.That(store.Authorize(owner, ticket.Credential, deviceA, write "Other::linked", None) |> accepted, Is.False)

[<Test>]
let ``manual project identity rejects missing or substituted canonical ledger proof`` () =
    let store = ManualGrantStore()
    let ticket = prospectiveGrant store "NamedProject" [ "SchedulingAdmin"; "Implementation" ]
    Assert.That(store.Decide(owner, ticket.Credential, true), Is.True)
    Assert.That((store.Authorize(owner, ticket.Credential, deviceA, create "NamedProject", None) = Ok true), Is.True)
    Assert.That(store.FinishCreation(owner, ticket.Credential, Some ("NamedProject", projectId)), Is.True)
    Assert.That(store.Authorize(owner, ticket.Credential, deviceA, write "NamedProject", None) |> accepted, Is.False)
    Assert.That(store.Authorize(owner, ticket.Credential, deviceA, write "NamedProject", Some "123e4567-e89b-42d3-a456-426614174001") |> accepted, Is.False)
    Assert.That((store.Authorize(owner, ticket.Credential, deviceA, write "NamedProject", Some projectId) = Ok false), Is.True)

[<Test>]
let ``public manual grant API has explicit bearer and no model approval endpoint`` () =
    let methods = typeof<JarvisMcpTools>.GetMethods()
    let names = methods |> Array.map _.Name |> Set.ofArray
    Assert.That(names.Contains "RequestGrant", Is.True)
    Assert.That(names.Contains "ApproveGrant", Is.False)
    Assert.That(names.Contains "DenyGrant", Is.False)
    for name in [ "CreateProject"; "CreateRole"; "CreateTask"; "WriteFile"; "RunCommand"; "SubmitReview"; "ImportFile" ] do
        let method = methods |> Array.find (fun item -> item.Name = name)
        let credential = method.GetParameters() |> Array.find (fun arg -> arg.Name = "manualGrantCredential")
        Assert.That(credential.IsOptional, Is.True, $"Manual credential must be opt-in on {name}.")
    let published = McpToolHelpers.mcpListCommandsResult.Commands
    Assert.That(published |> List.exists (fun cap -> cap.Name = "RequestGrant"), Is.True)

[<Test>]
let ``manual grant has exact two-device same-name project authority and a positive local route`` () =
    let users = UserService()
    let register device =
        let connection = "connection-" + device
        users.TransportConnected(owner, connection)
        users.Register(owner,
            { DeviceId = device; DeviceName = device; ProtocolVersion = AgentProtocol.version
              ClientVersion = "test"; SupportedCommands = [ "LedgerRead"; "WriteFile" ] },
            connection) |> ignore
    register deviceA
    register "device-B"
    let select conversation overrideDevice =
        match users.GetSelectedSession(owner, conversation, overrideDevice) with
        | ValueSome session -> session.DeviceId
        | ValueNone -> failwith "Expected an authenticated registered client"
    let projectB = "123e4567-e89b-42d3-a456-426614174001"
    let health id : AgentCommandResponse =
        { Result = Some (sprintf """{"projectId":"%s"}""" id)
          Error = None; PendingJob = None; Instructions = []
          StateChangeEvidence = StateChangeEvidence.Confirmed []; InboxActivation = None }
    let idA = ManualGrantPolicy.verifiedHealthProjectId (health projectId)
    let idB = ManualGrantPolicy.verifiedHealthProjectId (health projectB)
    Assert.That(idA, Is.EqualTo(Some projectId))
    Assert.That(idB, Is.EqualTo(Some projectB))
    Assert.That(users.SelectConversationDevice(owner, "grant-conversation", deviceA), Is.True)
    let approvedDevice = select (Some "grant-conversation") None
    let store = ManualGrantStore()
    let ticket =
        match store.Request(owner, approvedDevice, "existing", "Example",
                            "Update existing Example", [ "Implementation" ], idA) with
        | Ok ticket -> ticket
        | Error reason -> failwith reason
    Assert.That(store.Decide(owner, ticket.Credential, true), Is.True)
    Assert.That(store.BoundDeviceId(owner, ticket.Credential), Is.EqualTo(Some deviceA))
    Assert.That(store.ExpectedCanonicalProjectId(owner, ticket.Credential), Is.EqualTo(idA))
    // Identically named project on device B has a different immutable ledger ID.
    Assert.That(store.Authorize(owner, ticket.Credential, select None (Some "device-B"),
                                write "Example", idB) |> accepted, Is.False)
    // Even the correct identity string cannot move an approval to another client.
    Assert.That(store.Authorize(owner, ticket.Credential, select None (Some "device-B"),
                                write "Example", idA) |> accepted, Is.False)
    // A changed conversation default must not silently redirect the grant.
    Assert.That(users.SelectConversationDevice(owner, "grant-conversation", "device-B"), Is.True)
    Assert.That(store.Authorize(owner, ticket.Credential,
                                select (Some "grant-conversation") None,
                                write "Example", idB) |> accepted, Is.False)
    // Replacement of the project instance on A must also fail closed.
    Assert.That(store.Authorize(owner, ticket.Credential, select None (Some deviceA),
                                write "Example", idB) |> accepted, Is.False)
    Assert.That((store.Authorize(owner, ticket.Credential, select None (Some deviceA),
                                 write "Example", idA) = Ok false), Is.True)

[<Test>]
let ``prospective grant stays pinned to original device through canonical creation`` () =
    let store = ManualGrantStore()
    let ticket = prospectiveGrant store "NamedProject" [ "SchedulingAdmin"; "Implementation" ]
    Assert.That(store.Decide(owner, ticket.Credential, true), Is.True)
    Assert.That(store.Authorize(owner, ticket.Credential, "device-B",
                                create "NamedProject", None) |> accepted, Is.False)
    Assert.That((store.Authorize(owner, ticket.Credential, deviceA,
                                 create "NamedProject", None) = Ok true), Is.True)
    Assert.That(store.FinishCreation(owner, ticket.Credential, Some ("NamedProject", projectId)), Is.True)
    Assert.That(store.Authorize(owner, ticket.Credential, "device-B",
                                write "NamedProject", Some projectId) |> accepted, Is.False)
    Assert.That(store.Authorize(owner, ticket.Credential, deviceA,
                                write "NamedProject", Some "123e4567-e89b-42d3-a456-426614174001")
                |> accepted, Is.False)
    Assert.That((store.Authorize(owner, ticket.Credential, deviceA,
                                 write "NamedProject", Some projectId) = Ok false), Is.True)

[<Test>]
let ``existing grants require verified ledger identity before issuing bearer`` () =
    let store = ManualGrantStore()
    let request id =
        store.Request(owner, deviceA, "existing", "Example", "Setup",
                      [ "Implementation" ], id)
    Assert.That(request None |> accepted, Is.False)
    Assert.That(request (Some "unverified") |> accepted, Is.False)
    Assert.That(store.Request(owner, "", "prospective", "Example", "Setup",
                              [ "Implementation" ], None) |> accepted, Is.False)
    Assert.That(store.VerifierCount, Is.Zero)
    let missing : AgentCommandResponse =
        { Result = Some "{}"; Error = None; PendingJob = None
          Instructions = []; StateChangeEvidence = StateChangeEvidence.Confirmed []; InboxActivation = None }
    Assert.That(ManualGrantPolicy.verifiedHealthProjectId missing, Is.EqualTo(None))
    Assert.That(ManualGrantPolicy.verifiedHealthProjectId
                    { missing with Result = Some (sprintf """{"projectId":"%s"}""" projectId)
                                   PendingJob = Some Unchecked.defaultof<_> }, Is.EqualTo(None))
