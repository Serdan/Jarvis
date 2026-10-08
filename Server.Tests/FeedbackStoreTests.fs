module FeedbackStoreTests

open System
open System.IO
open System.Security.Claims
open Microsoft.Data.Sqlite
open Microsoft.AspNetCore.Http
open NUnit.Framework
open FsUnitTyped
open Server
open Server.Services

let private withStore test =
    let directory =
        Path.Combine(Path.GetTempPath(), "jarvis-feedback-tests-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory(directory) |> ignore
    let store = FeedbackStore(Path.Combine(directory, "jarvis.db"))

    try
        test store
    finally
        Directory.Delete(directory, true)

let private feedbackInput project tool category severity summary details workaround =
    { ProjectName = project
      ToolName = tool
      Category = category
      Severity = severity
      Summary = summary
      Details = details
      Workaround = workaround
      OperationId = None }

let private authenticatedAccessor userId =
    let context = DefaultHttpContext()
    let identity =
        ClaimsIdentity(
            [ Claim("sub", userId)
              Claim("scope", Auth.WorkspaceRead) ],
            "test"
        )

    context.User <- ClaimsPrincipal(identity)
    let accessor = HttpContextAccessor()
    accessor.HttpContext <- context
    accessor

[<Test>]
let ``device MCP tools preserve user isolation and explicit selection`` () =
    let users = UserService()
    let registration id : Common.SignalR.ClientRegistration =
        { DeviceId = id
          DeviceName = id
          ProtocolVersion = Common.AgentProtocol.version
          ClientVersion = "1.0.0"
          SupportedCommands = [ "ListProjects" ] }
    users.TransportConnected("alice", "home-connection")
    users.Register("alice", registration "home", "home-connection") |> ignore
    users.TransportConnected("alice", "work-connection")
    users.Register("alice", registration "work", "work-connection") |> ignore

    let bobResult = McpToolHelpers.selectDevice users (authenticatedAccessor "bob") "home"
                    |> Async.AwaitTask |> Async.RunSynchronously
    bobResult.IsError |> shouldEqual (Nullable true)
    let invalid = McpToolHelpers.selectDevice users (authenticatedAccessor "alice") "unknown"
                  |> Async.AwaitTask |> Async.RunSynchronously
    invalid.IsError |> shouldEqual (Nullable true)
    let selected = McpToolHelpers.selectDevice users (authenticatedAccessor "alice") "work"
                   |> Async.AwaitTask |> Async.RunSynchronously
    selected.IsError |> shouldEqual (Nullable false)
    users.GetDefaultDeviceId("alice") |> shouldEqual (Some "work")
    let listed = McpToolHelpers.listDevices users (authenticatedAccessor "alice")
                 |> Async.AwaitTask |> Async.RunSynchronously
    listed.IsError |> shouldEqual (Nullable false)

[<Test>]
let ``existing feedback database migrates without losing records`` () =
    let directory = Path.Combine(Path.GetTempPath(), "jarvis-feedback-legacy-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(directory) |> ignore
    let databasePath = Path.Combine(directory, "legacy.db")
    try
        use connection = new SqliteConnection($"Data Source={databasePath}")
        connection.Open()
        use setup = connection.CreateCommand()
        setup.CommandText <-
            """CREATE TABLE feedback (
                 id TEXT PRIMARY KEY, created_at TEXT NOT NULL, user_id TEXT NOT NULL,
                 project_name TEXT NULL, tool_name TEXT NULL, category TEXT NOT NULL,
                 severity TEXT NOT NULL, summary TEXT NOT NULL, details TEXT NULL,
                 workaround TEXT NULL, operation_id TEXT NULL);
               INSERT INTO feedback (id, created_at, user_id, category, severity, summary)
               VALUES ('old-1', '2026-01-01T00:00:00.0000000+00:00', 'alice', 'Other', 'Info', 'Legacy');"""
        setup.ExecuteNonQuery() |> ignore
        connection.Close()
        use store = new FeedbackStore(databasePath)
        let migrated = store.GetFeedback("alice", "old-1").Value
        migrated.Summary |> shouldEqual "Legacy"
        migrated.Status |> shouldEqual "Open"
        migrated.ResolvedAt.HasValue |> shouldEqual false
        store.UpdateFeedback("alice", "old-1", "WontFix", None, Some "Not planned").Value.Status
        |> shouldEqual "WontFix"
    finally
        Directory.Delete(directory, true)

[<Test>]
let ``feedback resolution updates persist and remain user scoped`` () =
    withStore (fun store ->
        let created =
            store.AddFeedback("alice", feedbackInput None None "ToolFailure" "Friction" "Problem" None None, None)
        let original = store.GetFeedback("alice", created.Id).Value
        original.Status |> shouldEqual "Open"
        original.ResolvedAt.HasValue |> shouldEqual false
        store.GetFeedback("bob", created.Id) |> shouldEqual None
        store.UpdateFeedback("bob", created.Id, "Resolved", Some "abc123", Some "fixed") |> shouldEqual None

        let resolved = store.UpdateFeedback("alice", created.Id, "Resolved", Some "abc123", Some "fixed").Value
        resolved.Status |> shouldEqual "Resolved"
        resolved.ResolvedAt.HasValue |> shouldEqual true
        resolved.ResolutionCommit |> shouldEqual "abc123"
        resolved.ResolutionNotes |> shouldEqual "fixed"
        let persisted = store.GetFeedback("alice", created.Id).Value
        persisted.ResolutionCommit |> shouldEqual "abc123"
        let listed = store.ListFeedback("alice", None, None, None, None, 10)
        listed[0].Status |> shouldEqual "Resolved"

        let reopened = store.UpdateFeedback("alice", created.Id, "Open", None, None).Value
        reopened.ResolvedAt.HasValue |> shouldEqual false
        reopened.ResolutionCommit |> shouldEqual ""
        reopened.ResolutionNotes |> shouldEqual ""
    )

[<Test>]
let ``feedback update helper validates state and enforces user identity`` () =
    withStore (fun store ->
        let created = store.AddFeedback("alice", feedbackInput None None "Other" "Info" "Issue" None None, None)
        let alice = authenticatedAccessor "alice"
        let invalid = McpToolHelpers.updateFeedback store alice created.Id "Invalid" "" "" |> Async.AwaitTask |> Async.RunSynchronously
        invalid.IsError |> shouldEqual (Nullable true)
        let bob = authenticatedAccessor "bob"
        let denied = McpToolHelpers.updateFeedback store bob created.Id "Resolved" "abc" "" |> Async.AwaitTask |> Async.RunSynchronously
        denied.IsError |> shouldEqual (Nullable true)
        let alice = authenticatedAccessor "alice"
        let updated = McpToolHelpers.updateFeedback store alice created.Id "resolved" "abc" "fixed" |> Async.AwaitTask |> Async.RunSynchronously
        updated.IsError |> shouldEqual (Nullable false)
        store.GetFeedback("alice", created.Id).Value.Status |> shouldEqual "Resolved"
    )

[<Test>]
let ``telemetry expires while feedback survives and loses pruned operation link`` () =
    withStore (fun store ->
        let operation = store.BeginOperation("user-1", "RunCommand", None, "server", None, None)
        store.CompleteOperation(operation.Id, false, Some "Failure", 1L)
        store.AddFeedback(
            "user-1",
            feedbackInput None None "ToolFailure" "Info" "Keep this report" None None,
            Some operation.Id
        ) |> ignore
        store.AddConnectionEvent(
            "user-1", "TransportConnected", "connection-1",
            None, None, None, None, None, None, None, None
        )

        store.PruneTelemetry(DateTimeOffset.UtcNow.AddDays(31.0))
        store.ListConnectionEvents("user-1", 10).Length |> shouldEqual 0
        store.ResolveOperation("user-1", Some operation.Id, None, None)
        |> Result.isError |> shouldEqual true
        let feedback = store.ListFeedback("user-1", None, None, None, None, 10)
        feedback.Length |> shouldEqual 1
        feedback[0].Summary |> shouldEqual "Keep this report"
        feedback[0].OperationId |> shouldEqual ""
    )

[<Test>]
let ``feedback store links recent operation and aggregates it`` () =
    withStore (fun store ->
        let operation =
            store.BeginOperation(
                "user-1",
                "PatchFile",
                Some "Project1",
                "server-1",
                Some "client-1",
                Some "3.0"
            )

        store.CompleteOperation(operation.Id, false, Some "ValidationFailed", 42L)

        let linked =
            store.ResolveOperation("user-1", None, Some "PatchFile", Some "Project1")

        linked |> shouldEqual (Ok(Some operation.Id))

        let created =
            store.AddFeedback(
                "user-1",
                feedbackInput
                    (Some "Project1")
                    (Some "PatchFile")
                    "ToolFailure"
                    "Friction"
                    "Patch failed on synchronized edit"
                    None
                    (Some "Used a Python script"),
                Some operation.Id
            )

        created.OperationId |> shouldEqual operation.Id

        let entries =
            store.ListFeedback(
                "user-1",
                Some "Project1",
                Some "PatchFile",
                Some "ToolFailure",
                Some "Friction",
                20
            )

        entries.Length |> shouldEqual 1
        entries[0].OperationId |> shouldEqual operation.Id
        entries[0].Summary |> shouldEqual "Patch failed on synchronized edit"

        let summary = store.GetSummary("user-1", Some "Project1", None)
        summary.Total |> shouldEqual 1
        summary.WithWorkaround |> shouldEqual 1
        summary.LinkedToFailedOperation |> shouldEqual 1
        summary.ByTool |> Array.exactlyOne |> fun item ->
            item.Name |> shouldEqual "PatchFile"
            item.Count |> shouldEqual 1
    )

[<Test>]
let ``feedback cannot link another users operation`` () =
    withStore (fun store ->
        let operation =
            store.BeginOperation("user-1", "RunCommand", None, "server-1", None, None)

        store.CompleteOperation(operation.Id, true, None, 5L)

        store.ResolveOperation("user-2", Some operation.Id, None, None)
        |> shouldEqual (Error "operationId does not identify an operation belonging to the authenticated user.")
    )

[<Test>]
let ``feedback validation canonicalizes category and severity`` () =
    FeedbackValidation.category "ergonomics" |> shouldEqual (Ok "Ergonomics")
    FeedbackValidation.severity "friction" |> shouldEqual (Ok "Friction")
    FeedbackValidation.category "nonsense" |> Result.isError |> shouldEqual true

[<Test>]
let ``feedback MCP helper stores without a registered local client`` () =
    withStore (fun store ->
        let accessor = authenticatedAccessor "user-feedback"

        let result =
            McpToolHelpers.submitFeedback
                store
                accessor
                "Project1"
                "PatchFile"
                "Ergonomics"
                "Friction"
                "Multi-file synchronized edit was awkward"
                ""
                "Used a Python script"
                ""
            |> Async.AwaitTask
            |> Async.RunSynchronously

        result.IsError |> shouldEqual (Nullable false)

        let entries =
            store.ListFeedback("user-feedback", Some "Project1", Some "PatchFile", None, None, 10)

        entries.Length |> shouldEqual 1
        entries[0].Workaround |> shouldEqual "Used a Python script"
    )


[<Test>]
let ``feedback MCP helper aggregates independent validation errors`` () =
    withStore (fun store ->
        let accessor = authenticatedAccessor "user-validation"

        let result =
            McpToolHelpers.submitFeedback
                store
                accessor
                ""
                "Feedback"
                "ErrorHandling"
                "Medium"
                "Test"
                ""
                ""
                ""
            |> Async.AwaitTask
            |> Async.RunSynchronously

        result.IsError |> shouldEqual (Nullable true)
        let text = result.Content[0] :?> ModelContextProtocol.Protocol.TextContentBlock
        text.Text.Contains("category must be one of", StringComparison.Ordinal) |> shouldEqual true
        text.Text.Contains("severity must be one of", StringComparison.Ordinal) |> shouldEqual true)

[<Test>]
let ``feedback list enforces user isolation`` () =
    withStore (fun store ->
        store.AddFeedback(
            "user-1",
            feedbackInput None None "Positive" "Info" "Useful behavior" None None,
            None
        )
        |> ignore

        store.ListFeedback("user-2", None, None, None, None, 10)
        |> Array.length
        |> shouldEqual 0
    )

[<Test>]
let ``connection diagnostics persist disconnect metadata and isolate users`` () =
    withStore (fun store ->
        store.AddConnectionEvent(
            "user-1",
            "TransportConnected",
            "connection-1",
            None, None, None, None, None, None, None, None
        )

        store.AddConnectionEvent(
            "user-1",
            "TransportDisconnected",
            "connection-1",
            Some "device-1",
            Some "Serdan-Home",
            Some 9L,
            Some "1.2.3",
            Some "3.0",
            Some 60000L,
            Some "System.TimeoutException",
            Some "Client hasn't sent a message/ping within the configured ClientTimeoutInterval."
        )

        let events = store.ListConnectionEvents("user-1", 10)
        events.Length |> shouldEqual 2
        events[0].EventType |> shouldEqual "TransportDisconnected"
        events[0].DeviceName |> shouldEqual "Serdan-Home"
        events[0].Generation.Value |> shouldEqual 9L
        events[0].DurationMs.Value |> shouldEqual 60000L
        events[0].ErrorType |> shouldEqual "System.TimeoutException"
        events[0].ErrorMessage.Contains("ClientTimeoutInterval", StringComparison.Ordinal)
        |> shouldEqual true

        store.ListConnectionEvents("user-2", 10)
        |> Array.length
        |> shouldEqual 0
    )
