module Client.Tests.NotificationIndicatorTests

open System
open System.Text.Json
open Client.ReviewWorkflow
open NUnit.Framework

let private jsonOptions = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

let private notice recipient kind eventType acknowledged : AgentNotification =
    { NotificationId = Guid.NewGuid().ToString("N")
      FromAgentId = "Issuer"; FromProjectName = ""
      ToAgentId = recipient; TaskId = ""
      Message = "canonical event"; CreatedAtUtc = "2026-11-16T09:00:00Z"
      AcknowledgedAtUtc = acknowledged; Kind = kind; EventType = eventType }

let private record (value: AgentNotification) =
    let payload = JsonSerializer.Serialize(value, jsonOptions)
    "notifications/" + value.NotificationId + ".json",
    "{\"schemaVersion\":2," + payload.Substring(1)

let private counts items =
    match ReviewWorkflow.unreadNotificationCountsFromRecords (Map.ofList items) with
    | Ok result -> result
    | Error failure -> failwithf "Unexpected notification ledger error: %A" failure

[<Test>]
let zero_message_challenge_acknowledged_and_agent_scoped_counts () =
    let baseline = counts []
    Assert.That(baseline.IsEmpty, Is.True)
    let sent =
        [ notice "Owner" "message" "direct" null
          notice "Owner" "challenge" "assigned" null
          notice "Owner" "message" "direct" "2026-11-16T09:01:00Z"
          notice "Other" "message" "direct" null ]
    let current = sent |> List.map record |> counts
    Assert.That(current["Owner"]["message"], Is.EqualTo(1))
    Assert.That(current["Owner"]["challenge"], Is.EqualTo(1))
    Assert.That(current["Other"]["message"], Is.EqualTo(1))
    Assert.That(current["Other"].ContainsKey "challenge", Is.False)
    Assert.That(current.ContainsKey "No Messages", Is.False)
    let fullyRead = sent |> List.map (fun n -> { n with AcknowledgedAtUtc = "2026-11-16T09:02:00Z" })
    Assert.That((fullyRead |> List.map record |> counts).IsEmpty, Is.True)

[<Test>]
let old_schema_two_direct_messages_without_kind_or_event_remain_readable () =
    let old = notice "Owner" "message" "direct" null
    let path, data = record old
    let previous =
        data.Replace(",\"kind\":\"message\"", "")
            .Replace(",\"eventType\":\"direct\"", "")
    Assert.That(previous.Contains("\"kind\""), Is.False)
    let summary = counts [path, previous]
    Assert.That(summary["Owner"]["message"], Is.EqualTo(1))

[<Test>]
let malformed_kind_or_wrong_notification_identity_fails_closed () =
    let invalid = notice "Owner" "an invalid kind" "direct" null
    let path, data = record invalid
    match ReviewWorkflow.unreadNotificationCountsFromRecords (Map.ofList [path, data]) with
    | Error _ -> ()
    | Ok _ -> Assert.Fail("Malformed kind must not produce trusted counts.")
    let valid = notice "Owner" "message" "direct" null
    let _, payload = record valid
    match ReviewWorkflow.unreadNotificationCountsFromRecords
            (Map.ofList ["notifications/" + Guid.NewGuid().ToString("N") + ".json", payload]) with
    | Error _ -> ()
    | Ok _ -> Assert.Fail("Notification path and content UUID must agree.")
