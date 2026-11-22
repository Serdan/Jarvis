module ServerNamedClientSettingToolTests

open System
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
