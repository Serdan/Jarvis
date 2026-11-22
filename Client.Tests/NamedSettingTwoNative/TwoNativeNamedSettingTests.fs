module TwoNativeNamedSettingTests

open System
open System.Diagnostics
open System.IO
open System.Security.Claims
open System.Text.Json
open System.Threading.Tasks
open Client
open Common
open Common.SignalR
open ModelContextProtocol.Protocol
open NUnit.Framework
open Server
open Server.Services
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.SignalR
open Microsoft.Extensions.Logging.Abstractions

let private json (body: string) =
    use document = JsonDocument.Parse(body)
    document.RootElement.Clone()

type private NativeAgent(home: string) =
    let harness =
        Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "NativeSettingHost", "bin", "Release", "net11.0", "NativeSettingHost.dll"))
    let psi = ProcessStartInfo("dotnet")
    do
        Assert.That(File.Exists harness, Is.True, "Build the source-exact NativeSettingHost before testing.")
        psi.ArgumentList.Add(harness)
        psi.WorkingDirectory <- home
        psi.UseShellExecute <- false
        psi.RedirectStandardInput <- true
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.Environment["HOME"] <- home
        psi.Environment["XDG_CONFIG_HOME"] <- Path.Combine(home, "xdg")
        psi.Environment["DOTNET_CLI_HOME"] <- home
        psi.Environment["JARVIS_W25_DISPOSABLE_NATIVE_CLIENT"] <- "yes"
    let process = Process.Start(psi)
    member _.Exchange(payload: string) =
        Assert.That(process.HasExited, Is.False, "Native client exited before command.")
        process.StandardInput.WriteLine(payload)
        process.StandardInput.Flush()
        let line = process.StandardOutput.ReadLine()
        if isNull line then
            failwithf "Native client ended without response: %s" (process.StandardError.ReadToEnd())
        line
    member this.Command(payload: string) =
        JsonSerializer.Deserialize<AgentCommandResponse>(this.Exchange(payload))
    member this.Probe() = json (this.Exchange("PROBE"))
    interface IDisposable with
        member _.Dispose() =
            if not process.HasExited then
                try process.StandardInput.WriteLine("EXIT"); process.StandardInput.Flush()
                with _ -> ()
                if not (process.WaitForExit(2000)) then process.Kill(true)
            process.Dispose()

let private registration name =
    { DeviceId = name; DeviceName = name; ProtocolVersion = AgentProtocol.version
      ClientVersion = "disposable-source-exact"; SupportedCommands = ["UpdateNamedSetting"] }

let private principal (subject: string) =
    ClaimsPrincipal(ClaimsIdentity(
        [ Claim("sub", subject)
          Claim("scope", "workspace:write workspace:read") ], "test"))

let private firstText (result: CallToolResult) =
    result.Content
    |> Seq.choose (function :? TextContentBlock as block -> Some block.Text | _ -> None)
    |> String.concat "\n"

[<Test; NonParallelizable>]
let authenticatedTwoNativeClientsKeepCASAndWorkspaceAndRegistrationIsolated () =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-w25-two-native-" + Guid.NewGuid().ToString("N"))
    let homeA = Path.Combine(root, "alpha")
    let homeB = Path.Combine(root, "beta")
    let a0 = Path.Combine(homeA, "projects-A0")
    let a1 = Path.Combine(homeA, "projects-A1")
    let b0 = Path.Combine(homeB, "projects-B0")
    for folder in [ homeA; homeB; a0; a1; b0 ] do Directory.CreateDirectory(folder) |> ignore
    Directory.CreateDirectory(Path.Combine(a0, "A0-only")) |> ignore
    Directory.CreateDirectory(Path.Combine(a1, "A1-only")) |> ignore
    Directory.CreateDirectory(Path.Combine(b0, "B0-only")) |> ignore
    let config home = Path.Combine(home, "xdg", "Jarvis", "config.json")
    let initialize home workspace machine =
        ClientConfig.applyAt (config home) None "workspace-path" (Some workspace)
        |> Result.defaultWith failwith |> ignore
        let before = ClientConfig.loadAt(config home) |> Result.defaultWith failwith
        ClientConfig.applyAt (config home) (Some before.Revision) "machine-name" (Some machine)
        |> Result.defaultWith failwith |> ignore
    try
        initialize homeA a0 "alpha-original"
        initialize homeB b0 "beta-original"
        use alpha = new NativeAgent(homeA)
        use beta = new NativeAgent(homeB)
        let users = UserService()
        let alice = "auth0|w25-native-alice"
        let bob = "auth0|w25-native-bob"
        let connA = "disposable-alpha-conn"
        let connB = "disposable-beta-conn"
        for name, connection in [ "alpha", connA; "beta", connB ] do
            users.TransportConnected(alice, connection)
            users.Register(alice, registration name, connection) |> ignore
        let generationA =
            match users.GetSelectedSession(alice, None, Some "alpha") with
            | ValueSome status -> status.Generation
            | _ -> failwith "alpha registration lost"
        let generationB =
            match users.GetSelectedSession(alice, None, Some "beta") with
            | ValueSome status -> status.Generation
            | _ -> failwith "beta registration lost"
        let tracker = ClientResponseTracker()
        let recipient owner connection generation (native: NativeAgent) =
            { new IClientService with
                member _.ReceiveMessage(_) = Task.CompletedTask
                member _.ReceiveManualGrantedCommand(_, _, _) = Task.CompletedTask
                member _.ReceiveManualGrantApproval(_, _, _) = Task.CompletedTask
                member _.ReceiveCommand(correlation, wire, _) =
                    let response = native.Command(wire)
                    tracker.Complete(correlation, owner, connection, generation, response) |> ignore
                    Task.CompletedTask
                member _.ReceiveSourceCommand(_, _, _, _) = Task.CompletedTask
                member _.InvalidateAgentStates(_, _) = Task.CompletedTask
                member _.RecordAgentActivity(_, _, _) = Task.CompletedTask
                member _.InvalidateAllAgentStates() = Task.CompletedTask }
        let alphaRecipient = recipient alice connA generationA alpha
        let betaRecipient = recipient alice connB generationB beta
        let clients =
            { new IHubClients<IClientService> with
                member _.Client(connection) =
                    if connection = connA then alphaRecipient else betaRecipient
                member _.All = alphaRecipient
                member _.AllExcept(_) = alphaRecipient
                member _.Clients(_) = alphaRecipient
                member _.Group(_) = alphaRecipient
                member _.GroupExcept(_, _) = alphaRecipient
                member _.Groups(_) = alphaRecipient
                member _.User(_) = alphaRecipient
                member _.Users(_) = alphaRecipient }
        let hub =
            { new IHubContext<HubService, IClientService> with
                member _.Clients = clients
                member _.Groups = Unchecked.defaultof<_> }
        let context = DefaultHttpContext()
        context.User <- principal alice
        let accessor = HttpContextAccessor()
        accessor.HttpContext <- context
        let service = ClientService(hub, users, tracker, accessor,
                                    NullLogger<ClientService>.Instance, new OptionalFeedbackStore(""))
        let invoke device setting operation payload revision =
            let request =
                JsonSerializer.Deserialize<CallToolRequestParams>(
                    sprintf """{"name":"UpdateNamedSetting","arguments":{"deviceId":"%s"}}""" device)
            RequestDeviceRouting.withMetadata request (fun () ->
                McpToolHelpers.updateNamedSetting service accessor setting operation (json payload) revision device)
            |> fun task -> task.GetAwaiter().GetResult()
        let expectOk (result: CallToolResult) =
            Assert.That(result.IsError, Is.Not.EqualTo(Nullable true), firstText result)
        let expectError (result: CallToolResult) =
            Assert.That(result.IsError, Is.EqualTo(Nullable true), "Expected authenticated refusal")
        let snapshot home = ClientConfig.loadAt(config home) |> Result.defaultWith failwith
        let alphaInitial = snapshot homeA
        let betaInitial = snapshot homeB
        expectOk (invoke "alpha" "machine-name" "read" "null" "")
        let betaOriginalRead = invoke "beta" "machine-name" "read" "null" ""
        expectOk betaOriginalRead
        let betaOriginalResponse = json (firstText betaOriginalRead)
        Assert.That(betaOriginalResponse.GetProperty("NewPersistedValue").GetString(),
                    Is.EqualTo("beta-original"))
        Assert.That(betaOriginalResponse.GetProperty("Revision").GetString(),
                    Is.EqualTo(betaInitial.Revision))
        // Positive opposite-device route: the real selected-client server tool
        // must mutate Beta with Beta's own CAS, not silently fall back to Alpha.
        let alphaBytesBeforeBeta = File.ReadAllBytes(config homeA)
        expectOk (invoke "beta" "machine-name" "set" "\"beta-updated\"" betaInitial.Revision)
        let betaNamed = snapshot homeB
        Assert.That(betaNamed.Stored.MachineName, Is.EqualTo(Some "beta-updated"))
        Assert.That(betaNamed.Revision, Is.Not.EqualTo(betaInitial.Revision))
        Assert.That((snapshot homeA).Revision, Is.EqualTo(alphaInitial.Revision))
        Assert.That(Convert.ToHexString(File.ReadAllBytes(config homeA)),
                    Is.EqualTo(Convert.ToHexString(alphaBytesBeforeBeta)))
        let betaReadBack = invoke "beta" "machine-name" "read" "null" ""
        expectOk betaReadBack
        let betaReadResponse = json (firstText betaReadBack)
        Assert.That(betaReadResponse.GetProperty("NewPersistedValue").GetString(),
                    Is.EqualTo("beta-updated"))
        Assert.That(betaReadResponse.GetProperty("Revision").GetString(),
                    Is.EqualTo(betaNamed.Revision))
        expectOk (invoke "alpha" "machine-name" "set" "\"alpha-updated\"" alphaInitial.Revision)
        let alphaNamed = snapshot homeA
        Assert.That(alphaNamed.Stored.MachineName, Is.EqualTo(Some "alpha-updated"))
        Assert.That((snapshot homeB).Revision, Is.EqualTo(betaNamed.Revision))
        expectError (invoke "alpha" "machine-name" "set" "\"CAS-stale\"" alphaInitial.Revision)
        Assert.That((snapshot homeA).Revision, Is.EqualTo(alphaNamed.Revision))
        expectOk (invoke "alpha" "workspace-path" "set" (JsonSerializer.Serialize a1) alphaNamed.Revision)
        let moved = snapshot homeA
        Assert.That(moved.Stored.WorkspacePath, Is.EqualTo(Some a1))
        Assert.That((alpha.Probe()).GetProperty("root").GetString(), Is.EqualTo(a0))
        Assert.That((beta.Probe()).GetProperty("root").GetString(), Is.EqualTo(b0))
        expectError (invoke "alpha" "trust-level" "set" "\"full\"" moved.Revision)
        expectError (invoke "alpha" "allowed-environment-variables" "set" "[\"SECRET\"]" moved.Revision)
        Assert.That((snapshot homeA).Revision, Is.EqualTo(moved.Revision))
        expectError (invoke "does-not-exist" "machine-name" "read" "null" "")
        context.User <- principal bob
        expectError (invoke "alpha" "machine-name" "read" "null" "")
        context.User <- principal alice
        expectOk (invoke "alpha" "machine-name" "unset" "null" moved.Revision)
        Assert.That((snapshot homeA).Stored.MachineName, Is.EqualTo(None))
        Assert.That((snapshot homeB).Stored.MachineName, Is.EqualTo(Some "beta-updated"))
        // A fresh native OS process consumes persisted workspace; the running
        // process correctly keeps old runtime until restart, without switching beta.
        use freshAlpha = new NativeAgent(homeA)
        let probe = freshAlpha.Probe()
        Assert.That(probe.GetProperty("root").GetString(), Is.EqualTo(a1))
        Assert.That(probe.GetProperty("projects").EnumerateArray()
                    |> Seq.exists (fun element -> element.GetString() = "A1-only"), Is.True)
        Assert.That((beta.Probe()).GetProperty("root").GetString(), Is.EqualTo(b0))
        // The registration remains unchanged until the native client observes
        // its persisted name and performs its authenticated re-registration.
        let beforeReconnect =
            match users.GetSelectedSession(alice, None, Some "alpha") with
            | ValueSome session -> session.DeviceName
            | _ -> failwith "alpha registration unexpectedly missing"
        Assert.That(beforeReconnect, Is.EqualTo("alpha"))
        let afterUnset = snapshot homeA
        expectOk (invoke "alpha" "machine-name" "set" "\"alpha-reregistered\"" afterUnset.Revision)
        use restartedForRegistration = new NativeAgent(homeA)
        let emittedName = restartedForRegistration.Probe().GetProperty("machine").GetString()
        Assert.That(emittedName, Is.EqualTo("alpha-reregistered"))
        users.Register(alice, { registration "alpha" with DeviceName = emittedName }, connA) |> ignore
        match users.GetSelectedSession(alice, None, Some "alpha") with
        | ValueSome session -> Assert.That(session.DeviceName, Is.EqualTo("alpha-reregistered"))
        | _ -> Assert.Fail("Re-registered native name was not visible on selected account")
        match users.GetSelectedSession(alice, None, Some "beta") with
        | ValueSome session -> Assert.That(session.DeviceName, Is.EqualTo("beta"))
        | _ -> Assert.Fail("Beta registration was lost during alpha re-registration")
        // A formerly valid Beta connection now becomes stale. An exact Beta
        // selection must fail closed, even with Alpha still connected and ready.
        let alphaBytesBeforeStaleBeta = File.ReadAllBytes(config homeA)
        let betaBytesBeforeStaleBeta = File.ReadAllBytes(config homeB)
        let alphaRevisionBeforeStaleBeta = (snapshot homeA).Revision
        users.Disconnect(connB, Some "disposable Beta transport ended")
        match users.GetSelectedSession(alice, None, Some "beta") with
        | ValueSome session ->
            Assert.That(session.Generation, Is.EqualTo(generationB))
            Assert.That(session.ConnectionId, Is.EqualTo(None))
        | _ -> Assert.Fail("Expected retained disconnected Beta registration")
        expectError (invoke "beta" "machine-name" "set" "\"must-not-fall-back\"" betaNamed.Revision)
        Assert.That(Convert.ToHexString(File.ReadAllBytes(config homeB)),
                    Is.EqualTo(Convert.ToHexString(betaBytesBeforeStaleBeta)))
        Assert.That((snapshot homeB).Revision, Is.EqualTo(betaNamed.Revision))
        Assert.That(Convert.ToHexString(File.ReadAllBytes(config homeA)),
                    Is.EqualTo(Convert.ToHexString(alphaBytesBeforeStaleBeta)))
        Assert.That((snapshot homeA).Revision, Is.EqualTo(alphaRevisionBeforeStaleBeta))
        expectOk (invoke "alpha" "machine-name" "read" "null" "")
    finally
        try Directory.Delete(root, true) with _ -> ()
