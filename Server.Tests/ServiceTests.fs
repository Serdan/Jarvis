module ServerServiceTests

open System
open System.Text.Json
open System.Threading.Tasks
open Common
open ModelContextProtocol.Server
open NUnit.Framework
open FsUnitTyped
open Server
open Server.Services

[<Test>]
let ``user service preserves a replacement connection when the old connection is removed`` () =
    let users = UserService()
    users.Add("session", "old-connection")
    users.Add("session", "new-connection")

    users.Remove("old-connection")

    users.GetConnectionId("session")
    |> shouldEqual (ValueSome "new-connection")

[<Test>]
let ``response tracker completes registered response`` () =
    task {
        let tracker = ClientResponseTracker()
        let correlationId, pending = tracker.Register(TimeSpan.FromSeconds 5L)
        let expected =
            { Result = Some "ok"
              Error = None }

        tracker.Complete(correlationId, expected)
        let! actual = pending

        actual |> shouldEqual expected
    }

[<Test>]
let ``response tracker cancellation removes pending response`` () =
    task {
        let tracker = ClientResponseTracker()
        let correlationId, pending = tracker.Register(TimeSpan.FromSeconds 5L)

        tracker.Cancel(correlationId)

        try
            let! _ = pending
            Assert.Fail("Expected pending response to be canceled.")
        with :? TaskCanceledException ->
            ()
    }

[<Test>]
let ``response tracker honors configured timeout`` () =
    task {
        let tracker = ClientResponseTracker()
        let _, pending = tracker.Register(TimeSpan.FromMilliseconds 25L)

        try
            let! _ = pending
            Assert.Fail("Expected pending response to time out.")
        with :? TaskCanceledException ->
            ()
    }

[<Test>]
let ``all Jarvis MCP tools return structured JSON content`` () =
    let methods =
        typeof<JarvisMcpTools>.GetMethods()
        |> Array.choose (fun methodInfo ->
            match methodInfo.GetCustomAttributes(typeof<McpServerToolAttribute>, false) with
            | [| :? McpServerToolAttribute as attribute |] -> Some(methodInfo, attribute)
            | _ -> None)

    methods.Length > 0 |> shouldEqual true

    for methodInfo, attribute in methods do
        attribute.UseStructuredContent |> shouldEqual true
        methodInfo.ReturnType.IsGenericType |> shouldEqual true
        methodInfo.ReturnType.GetGenericTypeDefinition() |> shouldEqual typedefof<Task<_>>
        methodInfo.ReturnType.GetGenericArguments()[0] |> shouldEqual typeof<JsonElement>
