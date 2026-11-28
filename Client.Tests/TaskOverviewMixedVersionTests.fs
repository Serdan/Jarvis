module Client.Tests.TaskOverviewMixedVersionTests

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open NUnit.Framework
open Common

[<TestFixture>]
type TaskOverviewMixedVersionTests() =
    [<Test>]
    member _.legacy_server_wire_action_is_rejected_with_upgrade_guidance() =
        let root =
            Path.Combine(
                Path.GetTempPath(),
                "jarvis-task-overview-legacy-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory(root) |> ignore

        try
            let runtime =
                Client.Runtime(root, Client.ConsoleTui.ConsoleTui(), Client.PartialTrust)

            // This is the pre-capability server wire shape: the old action and no
            // TaskOverview property. Removing the property models an older binary,
            // rather than serializing the new record with an explicit null.
            let legacyCommand =
                ReviewReadCommand
                    { ProjectName = "Jarvis"
                      Action = "ListProjectTasks"
                      TaskId = ""
                      RequestId = ""
                      ReviewerId = ""
                      DocumentPath = ""
                      TaskOverview = None }

            let payload = JsonSerializer.Serialize(legacyCommand)
            let wire = JsonNode.Parse(payload)

            let rec removeTaskOverview (node: JsonNode) =
                match node with
                | :? JsonObject as value ->
                    value.Remove("TaskOverview") |> ignore
                    let children = value |> Seq.map (fun pair -> pair.Value) |> Seq.toArray
                    for child in children do
                        if not (isNull child) then removeTaskOverview child
                | :? JsonArray as values ->
                    for child in values do
                        if not (isNull child) then removeTaskOverview child
                | _ -> ()

            removeTaskOverview wire
            let legacyWire = wire.ToJsonString()

            let response =
                Client.SignalR.Client.executeAuthenticatedCommandWithChallengeWake
                    runtime legacyWire "LegacyServer" None false (fun _ _ -> ())
                |> Async.AwaitTask
                |> Async.RunSynchronously

            let error =
                response.Error
                |> Option.map (sprintf "%A")
                |> Option.defaultValue ""

            Assert.That(response.Result.IsNone, Is.True)
            Assert.That(error, Does.Contain("legacy").IgnoreCase)
            Assert.That(error, Does.Contain("upgrade").IgnoreCase)
            Assert.That(error, Does.Contain("ListProjectTasks"))
        finally
            if Directory.Exists(root) then
                Directory.Delete(root, true)
