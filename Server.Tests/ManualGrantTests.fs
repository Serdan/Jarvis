module ServerManualGrantTests

open System
open Common
open Server
open NUnit.Framework

let private owner = "account:test-owner"
let private projectId = "123e4567-e89b-42d3-a456-426614174000"
let private existingGrant (store: ManualGrantStore) project capabilities =
    match store.Request(owner, "existing", project, "Initialize project setup", capabilities) with
    | Ok ticket -> ticket
    | Error reason -> failwith reason

let private prospectiveGrant (store: ManualGrantStore) project capabilities =
    match store.Request(owner, "prospective", project, "Create project and configure work", capabilities) with
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
    Assert.That(store.Authorize(owner, ticket.Credential, create "Example", None) |> accepted, Is.False)
    Assert.That(store.Authorize(owner, ticket.Credential, write "Example", None) |> accepted, Is.False)

[<Test>]
let ``only original account can approve a pending credential`` () =
    let store = ManualGrantStore()
    let ticket = existingGrant store "Example" [ "Implementation" ]
    Assert.That(store.Decide("another-account", ticket.Credential, true), Is.False)
    Assert.That(store.Authorize("another-account", ticket.Credential, write "Example", None) |> accepted, Is.False)
    Assert.That(store.Status(owner, ticket.Credential), Is.EqualTo(Some Pending))
    Assert.That(store.Decide(owner, ticket.Credential, true), Is.True)
    Assert.That(store.Authorize(owner, ticket.Credential, write "Example", None) |> accepted, Is.True)

[<Test>]
let ``denied manual grant never acquires mutation authority`` () =
    let store = ManualGrantStore()
    let ticket = existingGrant store "Example" [ "Implementation" ]
    Assert.That(store.Decide(owner, ticket.Credential, false), Is.True)
    Assert.That(store.Decide(owner, ticket.Credential, true), Is.False)
    Assert.That(store.Status(owner, ticket.Credential), Is.EqualTo(Some Denied))
    Assert.That(store.Authorize(owner, ticket.Credential, write "Example", None) |> accepted, Is.False)

[<Test>]
let ``invalid UUID wrong project and unapproved operation family fail closed`` () =
    let store = ManualGrantStore()
    let ticket = existingGrant store "Example" [ "SchedulingAdmin" ]
    Assert.That(store.Decide(owner, ticket.Credential, true), Is.True)
    Assert.That(store.Authorize(owner, Guid.NewGuid().ToString("D"), create "Example", None) |> accepted, Is.False)
    Assert.That(store.Authorize(owner, ticket.Credential.ToUpperInvariant(), create "Example", None) |> accepted, Is.False)
    Assert.That(store.Authorize(owner, ticket.Credential, create "Other", None) |> accepted, Is.False)
    Assert.That(store.Authorize(owner, ticket.Credential, write "Example", None) |> accepted, Is.False)
    // Existing-project setup never permits new project creation, even with SchedulingAdmin.
    Assert.That(store.Authorize(owner, ticket.Credential, create "Example", None) |> accepted, Is.False)

[<Test>]
let ``prospective grant binds only after exact canonical creation result`` () =
    let store = ManualGrantStore()
    let ticket = prospectiveGrant store "NamedProject" [ "SchedulingAdmin"; "Implementation" ]
    Assert.That(store.Decide(owner, ticket.Credential, true), Is.True)
    Assert.That(store.Authorize(owner, ticket.Credential, write "NamedProject", None) |> accepted, Is.False)
    Assert.That((store.Authorize(owner, ticket.Credential, create "NamedProject", None) = Ok true), Is.True)
    Assert.That(store.Status(owner, ticket.Credential), Is.EqualTo(Some Creating))
    Assert.That(store.Authorize(owner, ticket.Credential, create "NamedProject", None) |> accepted, Is.False)
    Assert.That(store.FinishCreation(owner, ticket.Credential, Some ("NamedProject", projectId)), Is.True)
    Assert.That(store.Status(owner, ticket.Credential), Is.EqualTo(Some Created))
    Assert.That((store.Authorize(owner, ticket.Credential, write "NamedProject", Some projectId) = Ok false), Is.True)
    Assert.That(store.Authorize(owner, ticket.Credential, write "OtherProject", None) |> accepted, Is.False)
    Assert.That(store.Authorize(owner, ticket.Credential, create "NamedProject", None) |> accepted, Is.False)

[<Test>]
let ``mismatched or unknown project creation receipt blocks further mutations`` () =
    let store = ManualGrantStore()
    let ticket = prospectiveGrant store "NamedProject" [ "SchedulingAdmin"; "Implementation" ]
    Assert.That(store.Decide(owner, ticket.Credential, true), Is.True)
    Assert.That((store.Authorize(owner, ticket.Credential, create "NamedProject", None) = Ok true), Is.True)
    Assert.That(store.FinishCreation(owner, ticket.Credential, Some ("WrongProject", projectId)), Is.False)
    Assert.That(store.Status(owner, ticket.Credential), Is.EqualTo(Some CreationBlocked))
    Assert.That(store.Authorize(owner, ticket.Credential, write "NamedProject", None) |> accepted, Is.False)

[<Test>]
let ``new server instance forgets manual approvals without changing scheduled Work`` () =
    let beforeRestart = ManualGrantStore()
    let ticket = existingGrant beforeRestart "Example" [ "Implementation" ]
    Assert.That(beforeRestart.Decide(owner, ticket.Credential, true), Is.True)
    Assert.That(beforeRestart.Authorize(owner, ticket.Credential, write "Example", None) |> accepted, Is.True)
    let afterRestart = ManualGrantStore()
    Assert.That(afterRestart.VerifierCount, Is.Zero)
    Assert.That(afterRestart.Authorize(owner, ticket.Credential, write "Example", None) |> accepted, Is.False)
    let newTicket = existingGrant afterRestart "Example" [ "Implementation" ]
    Assert.That(newTicket.Credential, Is.Not.EqualTo(ticket.Credential))

[<Test>]
let ``malformed scope names and capabilities never issue bearer`` () =
    let store = ManualGrantStore()
    for project in [ ""; "../Escape"; "Other::linked"; ".hidden"; "A/B" ] do
        Assert.That(store.Request(owner, "prospective", project, "Purpose", [ "Implementation" ]) |> accepted, Is.False)
    for capabilities in [ []; [ "Administrator" ]; [ "Implementation"; "Administrator" ] ] do
        Assert.That(store.Request(owner, "existing", "Example", "Purpose", capabilities) |> accepted, Is.False)
    Assert.That(store.Request(owner, "prospective", "Example", " ", [ "Implementation" ]) |> accepted, Is.False)
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
    Assert.That(store.Authorize(owner, ticket.Credential, ListProjectsCommand, None) |> accepted, Is.False)

[<Test>]
let ``manual grants apply to registered worktrees only inside their named project`` () =
    let store = ManualGrantStore()
    let ticket = existingGrant store "Example" [ "Implementation" ]
    Assert.That(store.Decide(owner, ticket.Credential, true), Is.True)
    Assert.That((store.Authorize(owner, ticket.Credential, write "Example::linked", None) = Ok false), Is.True)
    Assert.That(store.Authorize(owner, ticket.Credential, write "Other::linked", None) |> accepted, Is.False)

[<Test>]
let ``manual project identity rejects missing or substituted canonical ledger proof`` () =
    let store = ManualGrantStore()
    let ticket = prospectiveGrant store "NamedProject" [ "SchedulingAdmin"; "Implementation" ]
    Assert.That(store.Decide(owner, ticket.Credential, true), Is.True)
    Assert.That((store.Authorize(owner, ticket.Credential, create "NamedProject", None) = Ok true), Is.True)
    Assert.That(store.FinishCreation(owner, ticket.Credential, Some ("NamedProject", projectId)), Is.True)
    Assert.That(store.Authorize(owner, ticket.Credential, write "NamedProject", None) |> accepted, Is.False)
    Assert.That(store.Authorize(owner, ticket.Credential, write "NamedProject", Some "123e4567-e89b-42d3-a456-426614174001") |> accepted, Is.False)
    Assert.That((store.Authorize(owner, ticket.Credential, write "NamedProject", Some projectId) = Ok false), Is.True)

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
