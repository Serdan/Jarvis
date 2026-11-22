module ServerNamedClientSettingToolTests

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.SignalR
open Microsoft.Extensions.DependencyInjection
open System.Text.Json
open Common
open Common.SignalR
open NUnit.Framework
open Server
open Server.Services

let private parseJson (source: string) =
    use document = JsonDocument.Parse(source)
    document.RootElement.Clone()

let private revision = String.replicate 64 "a"

let private parse name operation value rev =
    McpToolHelpers.parseNamedSetting name operation (parseJson value) rev

[<Test>]
let allEightCanonicalSettingsHaveStrictTypedSetAndUnset () =
    let cases =
        [ "machine-name", "\"test-client\"", NamedClientSetting.MachineName
          "workspace-path", "\"/tmp/disposable-root\"", NamedClientSetting.WorkspacePath
          "trust-level", "\"none\"", NamedClientSetting.TrustLevel
          "allowed-environment-variables", "[\"SAFE_NAME\"]", NamedClientSetting.AllowedEnvironmentVariables
          "max-import-bytes", "10000", NamedClientSetting.MaxImportBytes
          "browser-port", "8971", NamedClientSetting.BrowserPort
          "native-health-enabled", "true", NamedClientSetting.NativeHealthEnabled
          "native-health-timeout-seconds", "120", NamedClientSetting.NativeHealthTimeoutSeconds ]
    for name, payload, expected in cases do
        match parse name "set" payload revision with
        | Ok cmd ->
            Assert.That(cmd.Setting, Is.EqualTo(expected), "Typed enum " + name)
            Assert.That(cmd.Value.IsSome, Is.True, "Typed value " + name)
            Assert.That(cmd.ExpectedRevision, Is.EqualTo(Some revision))
        | Error reason -> Assert.Fail($"Valid setting {name} rejected: {reason}")
        match parse name "unset" "null" revision with
        | Ok cmd ->
            Assert.That(cmd.Setting, Is.EqualTo(expected))
            Assert.That(cmd.Value.IsNone, Is.True)
        | Error reason -> Assert.Fail($"Valid unset {name} rejected: {reason}")

[<Test>]
let malformedTypesAndAliasesCannotReachClientCommand () =
    let invalid =
        [ "MachineName", "set", "\"foo\""
          "machine_name", "set", "\"foo\""
          "machine-name", "set", "true"
          "workspace-path", "set", "8"
          "trust-level", "set", "false"
          "allowed-environment-variables", "set", "\"SAFE_NAME\""
          "allowed-environment-variables", "set", "[0]"
          "max-import-bytes", "set", "2.5"
          "browser-port", "set", "\"8200\""
          "native-health-enabled", "set", "\"true\""
          "native-health-timeout-seconds", "set", "false"
          "machine-name", "unset", "\"wrong\""
          "machine-name", "SET", "\"other\""
          "machine-name", "set", "null" ]
    for name, operation, payload in invalid do
        Assert.That(parse name operation payload revision |> Result.isError, Is.True,
                    $"{name}/{operation}/{payload} must fail")

[<Test>]
let remoteWritesRequireExactSha256RevisionAndLengthBounds () =
    Assert.That(parse "machine-name" "set" "\"valid\"" "" |> Result.isError, Is.True)
    Assert.That(parse "machine-name" "set" "\"valid\"" "not-a-sha" |> Result.isError, Is.True)
    Assert.That(parse "machine-name" "set" "\"valid\"" revision |> Result.isOk, Is.True)
    let oversized = "\"" + String.replicate 4097 "x" + "\""
    Assert.That(parse "machine-name" "set" oversized revision |> Result.isError, Is.True)
    let manyNames = "[" + String.concat "," (List.replicate 129 "\"X\"") + "]"
    Assert.That(parse "allowed-environment-variables" "set" manyNames revision |> Result.isError, Is.True)
[<Test>]
let readFirstUseDiscoversRevisionWithoutRemovingWriteCas () =
    match parse "machine-name" "read" "null" "" with
    | Ok cmd ->
        Assert.That(cmd.Setting, Is.EqualTo(NamedClientSetting.MachineName))
        Assert.That(cmd.Value, Is.EqualTo(None))
        Assert.That(cmd.ExpectedRevision, Is.EqualTo(None))
    | Error reason -> Assert.Fail("First-use authenticated read must work: " + reason)
    Assert.That(parse "machine-name" "read" "\"injected\"" "" |> Result.isError, Is.True)
    Assert.That(parse "machine-name" "read" "null" revision |> Result.isError, Is.True)
    Assert.That(parse "machine-name" "set" "\"changed\"" "" |> Result.isError, Is.True)
    Assert.That(parse "machine-name" "unset" "null" "" |> Result.isError, Is.True)

[<Test>]
let allowlistElementsAreNotRetokenizedOrSilentlyDeduplicated () =
    let bad = [ "[\"SAFE_A;SAFE_B\"]"; "[\"SAFE_A,SAFE_B\"]"; "[\"\"]";
                "[\"SAFE_A\",\"SAFE_A\"]"; "[\"safe_a\",\"SAFE_A\"]";
                "[\"9BAD\"]"; "[\"BAD-NAME\"]"; "[\" SAFE_A\"]" ]
    for payload in bad do
        Assert.That(parse "allowed-environment-variables" "set" payload revision |> Result.isError,
                    Is.True, "Malformed typed array must be rejected: " + payload)
    match parse "allowed-environment-variables" "set" "[\"SAFE_A\",\"SAFE_B\"]" revision with
    | Ok cmd ->
        Assert.That(cmd.Value, Is.EqualTo(Some(NamedClientSettingValue.TextList [ "SAFE_A"; "SAFE_B" ])))
    | Error reason -> Assert.Fail("Valid two-element allowlist rejected: " + reason)


[<Test>]
let namedSettingProtocolAdvertisedAndRoundTripsAsTypedMessage () =
    let command =
        UpdateNamedSettingCommand
            { Setting = NamedClientSetting.BrowserPort
              Value = Some(NamedClientSettingValue.Integer 7654L)
              ExpectedRevision = Some revision }
    Assert.That(AgentProtocol.capabilities |> List.exists (fun c ->
        c.Name = "UpdateNamedSetting" && c.MutatesState &&
        c.Permissions = [ WorkspaceWrite ]), Is.True)
    Assert.That(AgentCommandInfo.projectName command, Is.EqualTo(None))
    Assert.That(AgentCommandInfo.protocolName command, Is.EqualTo("UpdateNamedSetting"))
    let payload = JsonSerializer.Serialize<AgentCommand>(command)
    match JsonSerializer.Deserialize<AgentCommand>(payload) with
    | UpdateNamedSettingCommand roundTrip -> 
        Assert.That(roundTrip.Setting, Is.EqualTo(NamedClientSetting.BrowserPort))
        Assert.That(roundTrip.Value, Is.EqualTo(Some(NamedClientSettingValue.Integer 7654L)))
        Assert.That(roundTrip.ExpectedRevision, Is.EqualTo(Some revision))
    | _ -> Assert.Fail("Typed settings message lost its identity over the JSON wire.")

[<Test>]
let deviceIdentityIsAccountScopedAndExplicitSelectionNeverFallsBack () =
    let users = UserService()
    let register owner device connection =
        users.TransportConnected(owner, connection)
        let registration: ClientRegistration =
            { DeviceId = device; DeviceName = device
              ProtocolVersion = AgentProtocol.version
              ClientVersion = "test"
              SupportedCommands = [ "UpdateNamedSetting" ] }
        users.Register(owner, registration, connection) |> ignore
    register "alice" "alpha" "alice-alpha"
    register "alice" "beta" "alice-beta"
    register "bob" "alpha" "bob-alpha"
    // Even if alpha is the global default, explicit beta MUST win.
    match users.GetSelectedSession("alice", None, Some "beta") with
    | ValueSome selected -> Assert.That(selected.DeviceId, Is.EqualTo("beta"))
    | _ -> Assert.Fail("Explicit beta device was not selected.")
    // The same device string is never authority over a different account.
    match users.GetSelectedSession("bob", None, Some "beta") with
    | ValueNone -> ()
    | _ -> Assert.Fail("Cross-account device selection must be rejected.")
    // Explicit missing ID cannot fall back to alpha.
    match users.GetSelectedSession("alice", None, Some "deleted-device") with
    | ValueNone -> ()
    | _ -> Assert.Fail("Missing explicit device must not fall back.")
    Assert.That(users.SelectConversationDevice("alice", "conversation-one", "beta"), Is.True)
    match users.GetSelectedSession("alice", Some "conversation-one", None) with
    | ValueSome selected -> Assert.That(selected.DeviceId, Is.EqualTo("beta"))
    | _ -> Assert.Fail("Conversation-specific selection was lost.")


[<Test>]
let authenticatedNamedSettingRoutingTargetsOnlyExactConnectedDevice () =
    // In-process production ClientService/typed SignalR dispatch, with disposable
    // account sessions. No operator client or persisted user settings are touched.
    let users = UserService()
    let tracker = ClientResponseTracker()
    let arrivals = ResizeArray<string * string>()
    let responseFor device : AgentCommandResponse =
        { Result = Some(sprintf """{"device":"%s","revision":"%s"}""" device revision)
          Error = None
          Instructions = []
          AgentStateChanges = []
          InboxActivation = None
          PendingJob = None }
    let endpoint device connection =
        { new IClientService with
            member _.ReceiveMessage _ = Task.CompletedTask
            member _.ReceiveCommand(correlation, json, _actor) =
                match JsonSerializer.Deserialize<AgentCommand>(json) with
                | UpdateNamedSettingCommand cmd ->
                    arrivals.Add((device, NamedClientSettingWire.canonicalName cmd.Setting))
                    match users.GetSessionByDevice("alice", device) with
                    | ValueSome active ->
                        Assert.That(tracker.Complete(correlation, "alice", connection,
                                                     active.Generation, responseFor device), Is.True)
                    | ValueNone -> Assert.Fail("Test session disappeared.")
                | _ -> Assert.Fail("Wrong command crossed named-setting transport.")
                Task.CompletedTask
            member _.ReceiveSourceCommand(_, _, _, _) = Task.CompletedTask
            member _.ReceiveManualGrantedCommand(_, _, _) = Task.CompletedTask
            member _.ReceiveManualGrantApproval(_, _, _) = Task.CompletedTask
            member _.InvalidateAgentStates(_, _) = Task.CompletedTask
            member _.InvalidateAllAgentStates() = Task.CompletedTask
            member _.RecordAgentActivity(_, _, _) = Task.CompletedTask }
    let endpoints =
        dict [ "alice-alpha", endpoint "alpha" "alice-alpha"
               "alice-beta", endpoint "beta" "alice-beta" ]
    let clients =
        { new IHubClients<IClientService> with
            member _.All = Unchecked.defaultof<_>
            member _.AllExcept _ = Unchecked.defaultof<_>
            member _.Client id = endpoints[id]
            member _.Clients _ = Unchecked.defaultof<_>
            member _.Group _ = Unchecked.defaultof<_>
            member _.GroupExcept(_, _) = Unchecked.defaultof<_>
            member _.Groups _ = Unchecked.defaultof<_>
            member _.User _ = Unchecked.defaultof<_>
            member _.Users _ = Unchecked.defaultof<_> }
    let groups =
        { new IGroupManager with
            member _.AddToGroupAsync(_, _, _) = Task.CompletedTask
            member _.RemoveFromGroupAsync(_, _, _) = Task.CompletedTask }
    let hub =
        { new IHubContext<HubService, IClientService> with
            member _.Clients = clients
            member _.Groups = groups }
    for device, connection in [ "alpha", "alice-alpha"; "beta", "alice-beta" ] do
        users.TransportConnected("alice", connection)
        users.Register("alice",
            { DeviceId = device; DeviceName = device; ProtocolVersion = AgentProtocol.version
              ClientVersion = "disposable"; SupportedCommands = [ "UpdateNamedSetting" ] },
            connection) |> ignore
    let fixture = IO.Path.Combine(IO.Path.GetTempPath(), "jarvis-w25-auth-route-" + Guid.NewGuid().ToString("N"))
    IO.Directory.CreateDirectory(fixture) |> ignore
    try
        let feedback = OptionalFeedbackStore(IO.Path.Combine(fixture, "optional.db"))
        let options =
            { Auth0Domain = "fixture.example"; Audience = "https://fixture.example"
              OpenAIAppsChallenge = ""; EnableOperatorConversations = false }
        let services = Microsoft.Extensions.DependencyInjection.ServiceCollection()
        services.AddSingleton<UserService>(users) |> ignore
        services.AddSingleton<OptionalFeedbackStore>(feedback) |> ignore
        services.AddSingleton<Microsoft.Extensions.Options.IOptions<JarvisOptions>>(
            Microsoft.Extensions.Options.Options.Create(options)) |> ignore
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance) |> ignore
        use provider = services.BuildServiceProvider()
        let context = Microsoft.AspNetCore.Http.DefaultHttpContext()
        context.RequestServices <- provider
        let accessor = Microsoft.AspNetCore.Http.HttpContextAccessor()
        accessor.HttpContext <- context
        let client =
            ClientService(hub, users, tracker, accessor,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ClientService>.Instance, feedback)
        let principal user scope =
            System.Security.Claims.ClaimsPrincipal(
                System.Security.Claims.ClaimsIdentity(
                    [ System.Security.Claims.Claim("sub", user)
                      System.Security.Claims.Claim("scope", scope) ], "disposable-test"))
        let invoke user scope target =
            task {
                context.User <- principal user scope
                let arguments = System.Collections.Generic.Dictionary<string, JsonElement>()
                arguments["deviceId"] <- JsonSerializer.SerializeToElement(target)
                let request =
                    ModelContextProtocol.Protocol.CallToolRequestParams(
                        Name = "UpdateNamedSetting", Arguments = arguments)
                return!
                    RequestDeviceRouting.withMetadata request (fun () ->
                        McpToolHelpers.updateNamedSetting client accessor "machine-name" "read"
                            (Unchecked.defaultof<JsonElement>) "" target)
            }
        let count() = arrivals.Count
        let first = invoke "alice" "workspace:write" "beta" |> Async.AwaitTask |> Async.RunSynchronously
        Assert.That(first.IsError, Is.Not.EqualTo(true))
        Assert.That(arrivals |> Seq.toList, Is.EqualTo(box [("beta", "machine-name")]),
                    "Explicit beta must win even with alpha as account default.")
        let before = count()
        let missing = invoke "alice" "workspace:write" "retired" |> Async.AwaitTask |> Async.RunSynchronously
        Assert.That(missing.IsError, Is.EqualTo(true))
        Assert.That(count(), Is.EqualTo(before), "Missing target must not fall back.")
        users.Disconnect("alice-beta", Some "disposable offline simulation")
        let disconnected = invoke "alice" "workspace:write" "beta" |> Async.AwaitTask |> Async.RunSynchronously
        Assert.That(disconnected.IsError, Is.EqualTo(true), "Disconnected beta must fail closed.")
        Assert.That(count(), Is.EqualTo(before), "Disconnected beta must not fall back to alpha.")
        let otherAccount = invoke "bob" "workspace:write" "alpha" |> Async.AwaitTask |> Async.RunSynchronously
        Assert.That(otherAccount.IsError, Is.EqualTo(true))
        Assert.That(count(), Is.EqualTo(before), "Foreign account must not reach alpha.")
        let insufficient = invoke "alice" "workspace:read" "alpha" |> Async.AwaitTask |> Async.RunSynchronously
        Assert.That(insufficient.IsError, Is.EqualTo(true))
        Assert.That(count(), Is.EqualTo(before), "Unscoped caller must not reach alpha.")
        let second = invoke "alice" "workspace:write" "alpha" |> Async.AwaitTask |> Async.RunSynchronously
        Assert.That(second.IsError, Is.Not.EqualTo(true))
        Assert.That(arrivals |> Seq.toList,
                    Is.EqualTo(box [("beta", "machine-name"); ("alpha", "machine-name")]))
    finally
        try IO.Directory.Delete(fixture, true) with _ -> ()
