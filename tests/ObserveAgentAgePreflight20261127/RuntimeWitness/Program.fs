module ObserveAgentRuntimeWitness

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open Client.LedgerStore
open Client.ReviewWorkflow
open Common

let succeed = function
    | Ok value -> value
    | Error error -> failwithf "Unexpected result: %A" error

let require message condition =
    if not condition then failwith message

let equal name expected actual =
    if expected <> actual then failwithf "%s: expected %A, got %A" name expected actual

// Disposable local bare Git fixture only. No live project, provider or browser.
let git dir args =
    let info = ProcessStartInfo("git", WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true)
    for arg in args do info.ArgumentList.Add arg
    use proc = Process.Start info
    let stdout = proc.StandardOutput.ReadToEndAsync()
    let stderr = proc.StandardError.ReadToEndAsync()
    proc.WaitForExit()
    if proc.ExitCode <> 0 then failwithf "Git fixture failure: %s" stderr.Result
    stdout.Result.Trim()

let run () =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-observe-age-preflight-" + Guid.NewGuid().ToString("N"))
    let bare = Path.Combine(root, "remote.git")
    let checkout = Path.Combine(root, "fixture")
    Directory.CreateDirectory(root) |> ignore
    let configPath = Client.OrchestrationApi.LedgerBindingConfig.pathForRoot checkout
    try
        git root ["init"; "--bare"; bare] |> ignore
        git root ["clone"; bare; checkout] |> ignore
        git checkout ["config"; "user.name"; "Observe fixture"] |> ignore
        git checkout ["config"; "user.email"; "fixture@example.test"] |> ignore
        File.WriteAllText(Path.Combine(checkout, "source.txt"), "fixture")
        git checkout ["add"; "source.txt"] |> ignore
        git checkout ["commit"; "-m"; "fixture base"] |> ignore
        git checkout ["push"; "-u"; "origin"; "HEAD"] |> ignore
        let binding : LedgerBinding =
            { RepositoryPath = checkout; RemoteName = "origin"; ApprovedFetchUrl = bare; ApprovedPushUrl = bare
              ProjectId = Guid.NewGuid(); LedgerId = Guid.NewGuid(); Epoch = 1 }
        LedgerStore.initialize binding true |> succeed |> ignore
        for id in ["Observer"; "Target"; "Parked"] do
            ReviewWorkflow.registerAgent binding (Guid.NewGuid()) id |> succeed |> ignore
        let taskId = "observe-age-fixture"
        ReviewWorkflow.createTask binding (Guid.NewGuid()) taskId "Fixture task" |> succeed |> ignore
        ReviewWorkflow.assignTask binding (Guid.NewGuid()) taskId (Guid.NewGuid()) "Target" |> succeed |> ignore
        let secret = "PRIVATE-NOTIFICATION-" + Guid.NewGuid().ToString("N")
        ReviewWorkflow.notifyAgent binding (Guid.NewGuid()) "Observer" "" "Target" taskId secret
        |> succeed |> ignore
        let head () = (LedgerStore.fetch binding |> succeed).Commit
        let before = head()
        let rt1 = Client.Runtime(root)
        let rt2 = Client.Runtime(root)
        Client.OrchestrationApi.LedgerBindingConfig.bindExisting "fixture" rt1 binding.ProjectId binding.LedgerId
            binding.Epoch binding.RemoteName bare bare before true |> succeed |> ignore
        let inbox id = ReviewWorkflow.getAgentInbox binding id |> succeed
        let ready = (inbox "Target").Agent
        equal "fixture ready" "Ready" ready.State
        equal "fixture private notification count" 1 (inbox "Target").PendingNotifications.Length
        let oldSeen = DateTimeOffset.UtcNow.AddSeconds(-601.)
        rt1.Tui.RecordAgentSeenAt("fixture", "Target", oldSeen)
        let read (rt: Client.Runtime) target expectedState expectedSeen expectedCount =
            let command = ObserveAgentCommand { ProjectName = "fixture"; ObserverAgentId = "Observer"; TargetAgentId = target }
            let request = JsonSerializer.Serialize<AgentCommand>(command)
            let _, result, response =
                Client.SignalR.Client.processIncomingCommand rt request "Observer"
                |> Async.AwaitTask |> Async.RunSynchronously
            let payload = succeed result
            require "no canonical state-change metadata" response.AgentStateChanges.IsEmpty
            require "no response instructions" response.Instructions.IsEmpty
            require "no activation receipt" response.InboxActivation.IsNone
            require "no private notification body" (not (payload.Contains(secret, StringComparison.Ordinal)))
            use document = JsonDocument.Parse(payload)
            let value = document.RootElement
            equal "target state" expectedState (value.GetProperty("state").GetString())
            equal "exact verified commit" (head()) (value.GetProperty("snapshotCommit").GetString())
            equal "source provenance" "VerifiedGitLedger" (value.GetProperty("canonicalStateSource").GetString())
            equal "local evidence is noncanonical" false (value.GetProperty("lastSeenIsCanonical").GetBoolean())
            equal "notification count only" expectedCount (value.GetProperty("pendingNotificationCount").GetInt32())
            let fields = value.EnumerateObject() |> Seq.map (fun p -> p.Name) |> Set.ofSeq
            for key in ["lastSeenAgeSeconds"; "lastSeenAgeStatus"; "lastSeenAgeClock"; "pendingNotifications"; "activeSessionId"; "associatedSessionIds"; "instructionLayers"] do
                require ("baseline excludes " + key) (not (fields.Contains key))
            let observed = DateTimeOffset.Parse(value.GetProperty("observedAtUtc").GetString(), Globalization.CultureInfo.InvariantCulture)
            match expectedSeen with
            | None ->
                equal "unknown provenance" "Unknown" (value.GetProperty("lastSeenProvenance").GetString())
                // Option values are serialized as null by the actual response serializer.
                equal "unknown timestamp" JsonValueKind.Null (value.GetProperty("lastSeenAtUtc").ValueKind)
            | Some (timestamp: DateTimeOffset) ->
                equal "local cache provenance" "LocalClientActivityCache" (value.GetProperty("lastSeenProvenance").GetString())
                equal "exact raw timestamp" timestamp (DateTimeOffset.Parse(value.GetProperty("lastSeenAtUtc").GetString(), Globalization.CultureInfo.InvariantCulture))
                require "stale local observation remains diagnostic" (observed >= timestamp)
            equal "observer activity unchanged" None (rt.Tui.GetAgentLastSeen("fixture", "Observer"))
            equal "subject activity unchanged" expectedSeen (rt.Tui.GetAgentLastSeen("fixture", target))
        // Same canonical state with differing client-local evidence; observation must
        // neither promote Ready nor move timestamps nor disclose private bodies.
        read rt1 "Target" "Ready" (Some oldSeen) 1
        read rt2 "Target" "Ready" None 1
        read rt1 "Parked" "Parked" None 0
        equal "ready canonical state unchanged" ready (inbox "Target").Agent
        equal "ledger unchanged after observation" before (head())
        equal "notification unconsumed" 1 (inbox "Target").PendingNotifications.Length
        printfn "PASS: actual registered ObserveAgent response has no derived fields; cache provenance, state, ledger and private data preserved"
    finally
        if File.Exists configPath then File.Delete configPath
        if Directory.Exists root then Directory.Delete(root, true)

[<EntryPoint>]
let main _ =
    try
        run()
        0
    with ex ->
        eprintfn "%O" ex
        1
