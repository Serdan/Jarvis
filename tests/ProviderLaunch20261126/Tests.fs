module ProviderLaunch20261126.Tests

open System
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open Client.ProviderLaunch
open Client.ProviderLaunchAdapters

let private eq expected actual =
    Assert.That((actual = expected), Is.True, sprintf "Expected %A; actual %A" expected actual)

let private profile id provider billing tags =
    { Id = id; Provider = provider; Model = None; ReasoningEffort = None
      McpProfiles = set ["worker"]; Capabilities = set ["tools"]; Tags = set tags; Billing = billing }

let private browser = profile "browser-default" chatGptProvider Included []
let private codex = { profile "native" codexProvider Included ["code"] with
                          Model = Some "configured-model"; ReasoningEffort = Some "high" }
let private metered = profile "paid" codexProvider Metered ["code"]
let private available (p: InferenceProfile) : AvailableProfile = { Profile = p; Available = true; Admitted = true }

let private requirements =
    { RequiredProfile = None; RequiredProvider = None; McpProfile = "worker"
      RequiredCapabilities = set ["tools"]; PreferredTags = Set.empty }

let private request (p: InferenceProfile) : LaunchRequest =
    let prompt = "Your agent ID is Worker 12.\nYour project is Jarvis.\nCall agent_inbox."
    { Identity =
          { ProjectId = Guid.NewGuid(); LaunchId = Guid.NewGuid(); EffectId = Guid.NewGuid()
            Target = NamedAssignment("Worker 12", "task", "attempt", 3L) }
      Profile = p; McpProfile = "worker"
      Bootstrap =
          { SnapshotCommit = String.replicate 40 "a"; TemplateVersion = 7
            TemplateBlobOid = String.replicate 40 "b"; PromptSha256 = promptDigest prompt
            Prompt = prompt } }

let private state (r: LaunchRequest) observations canonical =
    reconcile r.Identity r.Profile.Id observations canonical

let private canonical (r: LaunchRequest) fact : CanonicalObservation =
    { Identity = r.Identity; ProfileId = r.Profile.Id; Fact = fact }

[<Test>]
let ``profile selection respects hard eligibility soft tags and the existing default`` () =
    let profiles = List.map available [metered; codex; browser]
    eq (Ok browser) (selectProfile browser.Id requirements profiles)
    eq (Ok codex) (selectProfile browser.Id { requirements with PreferredTags = set ["code"] } profiles)
    let noNative = [{ available codex with Available = false }; available browser; available metered]
    eq (Ok browser) (selectProfile browser.Id { requirements with PreferredTags = set ["code"] } noNative)
    let hard = { requirements with RequiredProvider = Some codexProvider }
    eq true (selectProfile browser.Id hard noNative |> Result.isError)
    eq true (selectProfile browser.Id { requirements with RequiredProfile = Some metered.Id } profiles |> Result.isError)
    eq true (selectProfile browser.Id { requirements with McpProfile = "reviewer" } profiles |> Result.isError)
    eq true (selectProfile browser.Id requirements [available browser; available browser] |> Result.isError)
    eq true (selectProfile browser.Id requirements [{ available browser with Admitted = false }] |> Result.isError)

let rec private permutations = function
    | [] -> [[]]
    | values ->
        values
        |> List.mapi (fun i value ->
            let remainder = values |> List.indexed |> List.choose (fun (j, v) -> if i = j then None else Some v)
            permutations remainder |> List.map (fun rest -> value :: rest))
        |> List.concat

[<Test>]
let ``original effect evidence is idempotent order independent and generation fenced`` () =
    let r = request browser
    let observations =
        [OutcomeUnknown; Acknowledged; LocalProcessExited 0; CancellationRequested]
        |> List.map (transport r)
    for order in permutations observations do
        eq (Ok Submitted) (state r (order @ order) [])
        eq (Ok VerifiedCallHome) (state r order [canonical r CallHomeVerified; canonical r CallHomeVerified])
        let c = [canonical r (ExecutionTerminal "yielded"); canonical r CallHomeVerified]
        eq (Ok (Terminal "yielded")) (state r order (List.rev c @ c))
    eq (Ok Uncertain) (state r [transport r (LocalProcessExited 0)] [])
    eq (Ok NotSubmitted) (state r [transport r OutcomeUnknown; transport r DefinitiveNoSend] [])
    eq (Ok ConflictingEvidence) (state r [transport r DefinitiveNoSend; transport r Acknowledged] [])
    eq (Ok ConflictingEvidence) (state r [transport r DefinitiveNoSend] [canonical r CallHomeVerified])
    eq (Ok ConflictingEvidence) (state r [] [canonical r (ExecutionTerminal "completed"); canonical r (ExecutionTerminal "yielded")])
    let stale = { r with Identity = { r.Identity with Target = NamedAssignment("Worker 12", "task", "attempt", 2L) } }
    eq true (state r [transport stale Acknowledged] [] |> Result.isError)
    eq true (state r [] [canonical stale CallHomeVerified] |> Result.isError)
    let wrongProfile = { r with Profile = codex }
    eq true (state r [transport wrongProfile Acknowledged] [] |> Result.isError)

[<Test>]
let ``work targets do not need an agent and canonical prompt bytes are immutable`` () =
    let r = request browser
    let work = { r with Identity = { r.Identity with Target = QueueClaim("work", 1L) } }
    eq (Ok ()) (validateRequest work)
    let modified = { r with Bootstrap = { r.Bootstrap with Prompt = r.Bootstrap.Prompt + "!" } }
    eq true (validateRequest modified |> Result.isError)

let private browserBinding =
    { Profile = browser; McpProfile = "worker"; ExistingContextProvisioned = true
      WindowRegistration = "registered"; Url = "https://chatgpt.com/"
      Alias = ""; Active = false; CloseAfterExecution = true; ScriptSource = "// existing script"
      BridgeTimeoutMs = 30000; ScriptTimeoutMs = None; StartUrl = None; ModelOverride = None }

[<Test>]
let ``browser reuses exact bridge envelope and acknowledges independently of URL`` () = task {
    let r = request browser
    let mutable invocations = 0
    let mutable arguments = ""
    let adapter =
        browserAdapter browserBinding (fun invocation ->
            invocations <- invocations + 1
            eq browserBinding invocation.Binding
            arguments <- invocation.ArgumentsJson
            Task.FromResult(Ok """{"status":"submitted","cleanup":"cleanupFailed","scriptResult":{"conversationUrl":null}}"""))
    let! first = adapter.Launch r
    let! duplicate = adapter.Launch r
    eq 1 invocations
    eq first duplicate
    eq (Client.BrowserArguments.serializeMinimalLaunchPrompt r.Bootstrap.Prompt) arguments
    eq (Ok Submitted) (state r first [])
    let! cancel = adapter.Cancel r
    eq [transport r UnsupportedCapability] cancel
    let! observation = adapter.Observe r
    eq first observation
    let restart = browserAdapter browserBinding (fun _ -> failwith "Observe must not submit after restart")
    let! restarted = restart.Observe r
    eq (Ok Uncertain) (state r restarted [])
    let foreign = { r with Identity = { r.Identity with Target = QueueClaim("another", 4L) } }
    let! changed = adapter.Launch foreign
    eq [transport foreign OutcomeUnknown] changed
    eq 1 invocations
}

[<Test>]
let ``browser classifier preserves definitive failures and refuses generic success or uncertain send`` () =
    eq Acknowledged (classifyBrowser (Ok """{"status":"submitted"}"""))
    eq DefinitiveNoSend (classifyBrowser (Ok """{"status":"notSubmitted"}"""))
    eq DefinitiveNoSend (classifyBrowser (Error("tab", "NotSubmitted;cleanupFailed")))
    eq DefinitiveNoSend (classifyBrowser (Error("tab", "UiIncompatible:composer:missing")))
    eq OutcomeUnknown (classifyBrowser (Ok """{"status":"completed","registrationId":"tab"}"""))
    eq OutcomeUnknown (classifyBrowser (Error("", "OutcomeUnknown:acknowledgement")))
    eq OutcomeUnknown (classifyBrowser (Ok "invalid"))
    eq OutcomeUnknown (classifyBrowser (Error("", "transport disconnected")))
    eq true (prepareBrowser { browserBinding with ExistingContextProvisioned = false } (request browser) |> Result.isError)

let private withFake script (action: string -> string -> Task<unit>) = task {
    if not (OperatingSystem.IsLinux()) then Assert.Ignore("Hermetic executable fixture is Linux-specific")
    let dir = Path.Combine(Path.GetTempPath(), "jarvis-provider-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    try
        let executable = Path.Combine(dir, "fake-codex")
        File.WriteAllText(executable, "#!/bin/sh\nset -eu\n" + script)
        File.SetUnixFileMode(executable, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
        do! action dir executable
    finally
        Directory.Delete(dir, true)
}

let private codexBinding dir exe =
    { Profile = codex; McpProfile = "worker"; Executable = exe; WorkingDirectory = dir
      McpServerName = "JarvisWorker"; McpServerUrl = Uri("https://example.invalid/mcp/worker")
      Timeout = TimeSpan.FromSeconds 15. }

let private eventually predicate = task {
    let deadline = DateTime.UtcNow.AddSeconds 8.
    while not (predicate()) && DateTime.UtcNow < deadline do
        do! Task.Delay 20
    Assert.That(predicate(), Is.True, "fake executable did not reach expected state")
}

[<Test>]
let ``Codex fake executable receives exact scoped MCP argv and canonical stdin without vendor history`` () =
    let script = """printf '%s\n' "$@" > argv
cat > stdin
printf 'launch\n' >> launches
printf '%s\n' '{"type":"turn.started"}' '{"type":"turn.completed"}'
"""
    withFake script (fun dir exe -> task {
        let r = request codex
        let b = codexBinding dir exe
        let completed = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let adapter =
            codexAdapter b (fun token cmd ->
                try Client.BoundedProcess.runWithCancellation token cmd
                finally completed.TrySetResult(()) |> ignore)
        let! _ = adapter.Launch r
        let! _ = adapter.Launch r
        do! completed.Task.WaitAsync(TimeSpan.FromSeconds 10.)
        let mutable observations : TransportObservation list = []
        let deadline = DateTime.UtcNow.AddSeconds 8.
        while not (observations |> List.exists (fun o -> o.Fact = Acknowledged)) && DateTime.UtcNow < deadline do
            let! observed = adapter.Observe r
            observations <- observed
            do! Task.Delay 20
        eq (Ok Submitted) (state r observations [])
        eq true (observations |> List.exists (fun o -> o.Fact = LocalProcessExited 0))
        eq r.Bootstrap.Prompt (File.ReadAllText(Path.Combine(dir, "stdin")))
        eq [|"launch"|] (File.ReadAllLines(Path.Combine(dir, "launches")))
        let args = File.ReadAllLines(Path.Combine(dir, "argv")) |> Array.toList
        eq ["exec"; "--json"; "-c"; "mcp_servers.JarvisWorker.url=\"https://example.invalid/mcp/worker\"";
            "-c"; "mcp_servers.JarvisWorker.enabled=true"; "-c"; "mcp_servers.JarvisWorker.required=true";
            "--model"; "configured-model"; "-c"; "model_reasoning_effort=\"high\""; "-"] args
        eq true (prepareCodex b { r with McpProfile = "reviewer" } |> Result.isError)
        let fresh = codexAdapter b (fun _ _ -> failwith "Restart observation must never execute")
        let! afterRestart = fresh.Observe r
        eq (Ok Uncertain) (state r afterRestart [])
        eq (Ok VerifiedCallHome) (state r afterRestart [canonical r CallHomeVerified])
    })

[<Test>]
let ``Codex cancellation uses bounded process cancellation and never implies terminal Work`` () =
    let script = """cat > stdin
touch started
sleep 10
touch incorrectly-survived
"""
    withFake script (fun dir exe -> task {
        let r = request codex
        let completed = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let adapter =
            codexAdapter (codexBinding dir exe) (fun token cmd ->
                try Client.BoundedProcess.runWithCancellation token cmd
                finally completed.TrySetResult(()) |> ignore)
        let! _ = adapter.Launch r
        do! eventually (fun () -> File.Exists(Path.Combine(dir, "started")))
        let! cancel = adapter.Cancel r
        eq true (cancel |> List.exists (fun o -> o.Fact = CancellationRequested))
        do! completed.Task.WaitAsync(TimeSpan.FromSeconds 5.)
        let! after = adapter.Observe r
        eq (Ok Uncertain) (state r (cancel @ after) [])
        eq false (File.Exists(Path.Combine(dir, "incorrectly-survived")))
    })

[<Test>]
let ``Codex wrong profile fails before process effect and missing acknowledgement remains unknown`` () = task {
    let r = request codex
    let b = codexBinding "/tmp" "/tmp/never-executed"
    let adapter = codexAdapter b (fun _ _ -> failwith "Wrong profile must never run")
    let invalid = { r with McpProfile = "reviewer" }
    let! observations = adapter.Launch invalid
    eq (Ok NotSubmitted) (state invalid observations [])
    let output : Client.BoundedProcess.Outcome =
        { ExitCode = 0; TimedOut = false; StdOut = "{\"type\":\"thread.started\"}\n"
          StdErr = "private"; StdOutTruncated = false; StdErrTruncated = false }
    eq [OutcomeUnknown; LocalProcessExited 0] (classifyCodex output)
}
