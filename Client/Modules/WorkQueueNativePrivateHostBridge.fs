module Client.WorkQueueNativePrivateHostBridge

// WQ11 production host gate. This is a description of capabilities in the
// checked-in host protocol, NOT a caller-supplied "bridge available" flag.
// No Work bearer is created, serialized, sent to a page, or admitted here.

[<RequireQualifiedAccess>]
type MissingPrivateHostInterface =
    | ProviderPrivateExecutionBootstrap
    | AuthenticatedProviderExecutionBinding
    | CredentialAuthenticatedWorkFirstConnect

type HostBridgeUnavailable =
    { Code: string
      Provider: string
      MissingInterfaces: MissingPrivateHostInterface list
      BrowserInterface: string
      WorkFirstConnectInterface: string
      IntegrationOwner: string
      Recovery: string }

[<RequireQualifiedAccess>]
type PrivateHostGate =
    | Disabled
    | HostBridgeUnavailable of HostBridgeUnavailable

module WorkQueueNativePrivateHostBridge =
    let private unavailable =
        { Code = "HostBridgeUnavailable"
          Provider = "ChatGPT"
          MissingInterfaces =
            [ MissingPrivateHostInterface.ProviderPrivateExecutionBootstrap
              MissingPrivateHostInterface.AuthenticatedProviderExecutionBinding
              MissingPrivateHostInterface.CredentialAuthenticatedWorkFirstConnect ]
          BrowserInterface =
            "browser-extension/background.js: chrome.userScripts.execute(USER_SCRIPT) " +
            "receives executeJs JSON args; only tab/window registration and a page script result, " +
            "not a provider-private execution bootstrap or provider-attested session"
          WorkFirstConnectInterface =
            "Client/Modules/WorkQueueNativeCredentialDispatchAdapter.fs: " +
            "firstConnectDisposable calls executeCredentialFixtureAt locally; " +
            "Server/McpTools.fs and Client/SignalR/Client.fs have no Work credential " +
            "first-connect transport bound to a verified provider execution"
          IntegrationOwner =
            "Provider-supported private bootstrap API and authenticated provider-execution " +
            "callback, then Jarvis authenticated Work credential relay"
          Recovery =
            "Keep the original WQ03 marker, WQ02 credential verifier and held account " +
            "reservation inert. Operator must establish and independently verify the " +
            "missing provider/host protocol before any live original send or first-connect." }

    // This is deliberately independent of user-provided tab IDs, agent labels,
    // session strings, browser-script results, or test fake-provider callbacks.
    // Even opting in to the feature does not fabricate a host capability.
    let inspectProduction enabled =
        if not enabled then PrivateHostGate.Disabled
        else PrivateHostGate.HostBridgeUnavailable unavailable
