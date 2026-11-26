module Client.ProviderLaunchAdapters

open System
open System.Collections.Generic
open System.IO
open System.Text.Json
open System.Text.Encodings.Web
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Client.ProviderLaunch
open Client.ProviderMcpProvisioning

let chatGptProvider = "chatgpt-browser"
let codexProvider = "codex-cli"

let private facts request values = values |> List.map (transport request)
let private unknown request = facts request [OutcomeUnknown]

/// One instance per durable original effect, not a reusable dispatch queue.
/// The existing journal/fence is the restart-safe exclusion mechanism; this
/// latch only prevents duplicate entry to the transport in the same instance.
type private OneEffect<'a>() =
    let gate = obj()
    let mutable current : (LaunchRequest * 'a) option = None

    member _.Get request =
        lock gate (fun () ->
            match current with
            | Some (original, value) when original = request -> Some value
            | _ -> None)

    member _.GetOrCreate(request, create: unit -> 'a) =
        lock gate (fun () ->
            match current with
            | Some (original, value) when original = request -> Some value
            | Some _ -> None
            | None ->
                let value = create()
                current <- Some (request, value)
                Some value)

type BrowserBinding =
    { Profile: InferenceProfile
      McpProfile: string
      McpScope: Scope
      ProvisioningEvidence: Evidence option
      ProjectContextId: string
      WindowRegistration: string
      Url: string
      Alias: string
      Active: bool
      CloseAfterExecution: bool
      ScriptSource: string
      BridgeTimeoutMs: int
      ScriptTimeoutMs: int option
      StartUrl: string option
      ModelOverride: string option }

type BrowserInvocation =
    { Binding: BrowserBinding
      ArgumentsJson: string }

let private jsonOptions =
    JsonSerializerOptions(Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

let private nonblank (s: string) = not (String.IsNullOrWhiteSpace s)

let prepareBrowser (binding: BrowserBinding) request =
    match validateRequest request with
    | Error reason -> Error reason
    | Ok () when request.Profile <> binding.Profile || request.Profile.Provider <> chatGptProvider
                 || request.McpProfile <> binding.McpProfile
                 || binding.McpScope.Id <> binding.McpProfile ->
        Error "ChatGPT context does not provision the requested profile"
    | Ok () when (Option.isSome request.Profile.Model || Option.isSome request.Profile.ReasoningEffort)
                    <> Option.isSome binding.ModelOverride
                 || not (binding.ModelOverride |> Option.forall nonblank) ->
        Error "ChatGPT model/effort profile requires an explicit existing model override mapping"
    | Ok () when not (nonblank binding.ProjectContextId)
                 || not (nonblank binding.WindowRegistration) || not (nonblank binding.Url)
                 || not (nonblank binding.ScriptSource)
                 || binding.BridgeTimeoutMs < 1 || binding.BridgeTimeoutMs > 30000 ->
        Error "Invalid browser binding"
    | Ok () when validate request (Browser(binding.ProjectContextId, binding.WindowRegistration, binding.Url, binding.StartUrl)) binding.McpScope binding.ProvisioningEvidence |> Result.isError ->
        Error "ChatGPT effective MCP context not verified"
    | Ok () ->
        let arguments = Dictionary<string, obj>()
        arguments.Add("prompt", box request.Bootstrap.Prompt)
        binding.ScriptTimeoutMs |> Option.iter (fun value -> arguments.Add("timeoutMs", box value))
        binding.StartUrl |> Option.iter (fun value -> arguments.Add("startUrl", box value))
        binding.ModelOverride |> Option.iter (fun value -> arguments.Add("modelOverride", box value))
        let serialized = JsonSerializer.Serialize(arguments, jsonOptions)
        if Client.BrowserArguments.fits serialized then
            Ok { Binding = binding; ArgumentsJson = serialized }
        else Error "Browser launch argument budget exceeded"

/// Classify only the existing bridge's typed ChatGPT result. No generic
/// bridge success, registration ID, cleanup result or URL proves submission.
let classifyBrowser (result: Microsoft.FSharp.Core.Result<string, string * string>) =
    let readString name (element: JsonElement) =
        match element.TryGetProperty(name: string) with
        | true, value when value.ValueKind = JsonValueKind.String -> value.GetString()
        | _ -> ""
    match result with
    | Ok json ->
        try
            use doc = JsonDocument.Parse(json)
            match readString "status" doc.RootElement with
            | "submitted" -> Acknowledged
            | "notSubmitted" -> DefinitiveNoSend
            | _ -> OutcomeUnknown
        with _ -> OutcomeUnknown
    | Error (_, reason) when not (isNull reason)
                             && (reason.StartsWith("NotSubmitted;", StringComparison.Ordinal)
                                 || reason.StartsWith("UiIncompatible:", StringComparison.Ordinal)) ->
        DefinitiveNoSend
    | Error _ -> OutcomeUnknown

/// The port is the ordinary BrowserBridge.runInNewTab path, after existing
/// permission/admission gates. Unsupported cancellation does not close tabs
/// or claim that provider execution has stopped.
let browserAdapter binding (run: BrowserInvocation -> Task<Microsoft.FSharp.Core.Result<string, string * string>>) =
    let effect = OneEffect<Task<TransportObservation list>>()
    { Provider = chatGptProvider
      Launch = fun request ->
          let execute () = task {
              match prepareBrowser binding request with
              | Error _ -> return facts request [DefinitiveNoSend]
              | Ok invocation ->
                  try
                      let! result = run invocation
                      return facts request [classifyBrowser result]
                  with _ -> return unknown request
          }
          match effect.GetOrCreate(request, execute) with
          | Some value -> value
          | None -> Task.FromResult(unknown request)
      Observe = fun request ->
          match effect.Get request with
          | Some value when value.IsCompletedSuccessfully -> value
          | _ -> Task.FromResult(unknown request)
      Cancel = fun request -> Task.FromResult(facts request [UnsupportedCapability]) }

let browserAdapterForBridge (bridge: Client.BrowserBridge.Service) binding =
    browserAdapter binding (fun invocation ->
        let b = invocation.Binding
        Client.BrowserBridge.runInNewTab bridge b.WindowRegistration b.Url b.Alias b.Active
            b.ScriptSource invocation.ArgumentsJson b.BridgeTimeoutMs b.CloseAfterExecution)

type CodexBinding =
    { Profile: InferenceProfile
      McpProfile: string
      Executable: string
      WorkingDirectory: string
      McpServerName: string
      McpServerUrl: Uri
      McpScope: Scope
      ProvisioningEvidence: Evidence option
      Timeout: TimeSpan }

let private cliToken (value: string) =
    nonblank value && Regex.IsMatch(value, "\\A[A-Za-z0-9_.-]{1,120}\\z")

let private serverName (value: string) =
    nonblank value && Regex.IsMatch(value, "\\A[A-Za-z0-9_-]{1,80}\\z")

let private safeEndpoint (uri: Uri) =
    not (isNull uri) && uri.IsAbsoluteUri && uri.UserInfo = ""
    && uri.Query = "" && uri.Fragment = ""
    && (uri.Scheme = Uri.UriSchemeHttps || (uri.Scheme = Uri.UriSchemeHttp && uri.IsLoopback))

/// An explicit configured local executable, no PATH discovery, shell,
/// vendor history, credentials on argv or consent/sandbox overrides.
let prepareCodex (binding: CodexBinding) request : Microsoft.FSharp.Core.Result<Client.BoundedProcess.Command, string> =
    match validateRequest request with
    | Error reason -> Error reason
    | Ok () when request.Profile <> binding.Profile || request.Profile.Provider <> codexProvider
                 || request.McpProfile <> binding.McpProfile
                 || binding.McpScope.Id <> binding.McpProfile
                 || binding.McpScope.SurfaceName <> binding.McpServerName ->
        Error "Codex binding does not match the requested profile"
    | Ok () when not (nonblank binding.Executable) || not (Path.IsPathFullyQualified binding.Executable)
                 || not (nonblank binding.WorkingDirectory) || not (Path.IsPathFullyQualified binding.WorkingDirectory)
                 || not (serverName binding.McpServerName) || not (safeEndpoint binding.McpServerUrl)
                 || binding.Timeout <= TimeSpan.Zero
                 || not (request.Profile.Model |> Option.forall cliToken)
                 || not (request.Profile.ReasoningEffort |> Option.forall cliToken) ->
        Error "Invalid Codex binding"
    | Ok () when not (safeEndpoint binding.McpScope.Endpoint)
                 || binding.McpScope.Endpoint.AbsoluteUri <> binding.McpServerUrl.AbsoluteUri
                 || (validate request (Codex(binding.Executable, binding.WorkingDirectory)) binding.McpScope binding.ProvisioningEvidence |> Result.isError) ->
        Error "Codex effective MCP configuration not verified"
    | Ok () ->
        let prefix = "mcp_servers." + binding.McpServerName
        let quoted (value: string) = JsonSerializer.Serialize(value)
        let args =
            [ yield "exec"
              yield "--json"
              yield "-c"
              yield prefix + ".url=" + quoted binding.McpServerUrl.AbsoluteUri
              yield "-c"
              yield prefix + ".enabled=true"
              yield "-c"
              yield prefix + ".required=true"
              yield "-c"
              yield prefix + ".enabled_tools=" + JsonSerializer.Serialize(Set.toArray binding.McpScope.Tools)
              match request.Profile.Model with
              | Some model -> yield "--model"; yield model
              | None -> ()
              match request.Profile.ReasoningEffort with
              | Some effort -> yield "-c"; yield "model_reasoning_effort=" + quoted effort
              | None -> ()
              yield "-" ]
        Ok { Executable = binding.Executable
             Args = args
             WorkingDirectory = binding.WorkingDirectory
             Timeout = binding.Timeout
             MaxOutputBytes = Some 262144
             StandardInput = Client.BoundedProcess.TextInput request.Bootstrap.Prompt
             ExtraEnvironment = []
             ConfigureStart = Some (fun start ->
                 Client.ProcessEnvironment.apply start
                 // This default-off slice never chooses metered execution.
                 // Existing authenticated CLI configuration must be checked
                 // separately at live validation; do not inject API keys.
                 start.Environment.Remove("OPENAI_API_KEY") |> ignore
                 start.Environment.Remove("CODEX_API_KEY") |> ignore) }

/// A local process exit is not Work completion. A complete JSONL turn-start
/// event is at most a transport acknowledgement; stdout/stderr are not stored
/// in observations. Missing, malformed or truncated evidence stays unknown.
let classifyCodex (outcome: Client.BoundedProcess.Outcome) =
    let lines = outcome.StdOut.Split('\n')
    let completeLines = if outcome.StdOutTruncated then lines |> Array.take (lines.Length - 1) else lines
    let acknowledged =
        completeLines
        |> Array.exists (fun line ->
            try
                use doc = JsonDocument.Parse(line)
                match doc.RootElement.TryGetProperty("type") with
                | true, value when value.ValueKind = JsonValueKind.String -> value.GetString() = "turn.started"
                | _ -> false
            with _ -> false)
    [ yield if acknowledged then Acknowledged else OutcomeUnknown
      if not outcome.TimedOut then yield LocalProcessExited outcome.ExitCode ]

type private NativeSession =
    { RequestCancellation: (unit -> bool) option
      Completion: Task<TransportObservation list> }

/// A configured process adapter using the existing bounded process runner.
/// Wiring it to a real executable is deliberately not part of this slice:
/// the host must first approve the exact command, verify existing authenticated
/// account/profile and scope, and durably claim the original effect.
let codexAdapter binding
    (run: CancellationToken -> Client.BoundedProcess.Command -> Client.Result<Client.BoundedProcess.Outcome>) =
    let effect = OneEffect<NativeSession>()
    let create request =
        match prepareCodex binding request with
        | Error _ ->
            { RequestCancellation = None
              Completion = Task.FromResult(facts request [DefinitiveNoSend]) }
        | Ok command ->
            let cancel = new CancellationTokenSource()
            let cancellationGate = obj()
            let mutable disposed = false
            let requestCancellation () =
                lock cancellationGate (fun () ->
                    if disposed then false
                    else cancel.Cancel(); true)
            let completion =
                Task.Run<TransportObservation list>(Func<TransportObservation list>(fun () ->
                    try
                        try
                            match run cancel.Token command with
                            | Ok outcome -> facts request (classifyCodex outcome)
                            | Error _ -> unknown request
                        with _ -> unknown request
                    finally
                        lock cancellationGate (fun () ->
                            disposed <- true
                            cancel.Dispose())))
            { RequestCancellation = Some requestCancellation; Completion = completion }
    let observe request =
        match effect.Get request with
        | Some session when session.Completion.IsCompletedSuccessfully -> session.Completion
        | _ -> Task.FromResult(unknown request)
    { Provider = codexProvider
      Launch = fun request ->
          match effect.GetOrCreate(request, fun () -> create request) with
          | Some session when session.Completion.IsCompletedSuccessfully -> session.Completion
          | _ -> Task.FromResult(unknown request)
      Observe = observe
      Cancel = fun request ->
          match effect.Get request with
          | Some session when session.Completion.IsCompletedSuccessfully -> session.Completion
          | Some session ->
              match session.RequestCancellation with
              | Some cancel ->
                  if cancel() then Task.FromResult(facts request [CancellationRequested; OutcomeUnknown])
                  else observe request
              | None -> Task.FromResult(unknown request)
          | None -> Task.FromResult(unknown request) }
