module Client.Tests.McpLatencyTraceTests

open System
open System.Text.Json
open Client
open NUnit.Framework

[<TestFixture>]
type McpLatencyTraceTests() =
    [<Test>]
    member _.OptInEmitsOnlyWhitelistedStagesWithoutGitArgumentsOrPayload() =
        let id = Guid.NewGuid().ToString("D")
        let previous, trace = McpLatencyTrace.beginTrace true id
        let diagnostic =
            try
                let secret = "credential-private-should-never-appear"
                McpLatencyTrace.measureGit
                    [ "fetch"; "https://user:password@example.invalid/" + secret ]
                    (fun () -> ())
                McpLatencyTrace.measure "json.command_parse" (fun () -> secret.Length |> ignore)
                McpLatencyTrace.render trace "completed"
            finally
                McpLatencyTrace.restore previous
        Assert.That(diagnostic.IsSome, Is.True)
        let payload = diagnostic.Value
        Assert.That(payload, Does.Not.Contain("credential-private"))
        Assert.That(payload, Does.Not.Contain("password"))
        Assert.That(payload, Does.Not.Contain("https"))
        Assert.That(payload, Does.Contain(id))
        use json = JsonDocument.Parse(payload)
        let stages = json.RootElement.GetProperty("stages").EnumerateArray() |> Seq.toArray
        Assert.That(stages.Length, Is.EqualTo(2))
        Assert.That(stages |> Array.map (fun stage -> stage.GetProperty("stage").GetString()),
                    Is.EquivalentTo([| "git.fetch"; "json.command_parse" |]))
        Assert.That(stages |> Array.forall (fun stage ->
            stage.GetProperty("calls").GetInt32() = 1), Is.True)

    [<Test>]
    member _.DefaultDisabledPathHasNoPayloadOrAmbientTrace() =
        let previous, trace = McpLatencyTrace.beginTrace false (Guid.NewGuid().ToString("D"))
        try
            let actual = McpLatencyTrace.measureGit [ "ls-tree"; "secret" ] (fun () -> 42)
            Assert.That(actual, Is.EqualTo(42))
            Assert.That(McpLatencyTrace.render trace "failed", Is.EqualTo(None))
        finally
            McpLatencyTrace.restore previous

    [<Test>]
    member _.NonUuidCorrelationIsRedactedAndUnexpectedOutcomeIsNotEmitted() =
        let previous, trace = McpLatencyTrace.beginTrace true "private-device-name"
        try
            McpLatencyTrace.measure "private-secret-stage" (fun () -> ())
            let actual = McpLatencyTrace.render trace "response-body-private"
            Assert.That(actual.IsSome, Is.True)
            Assert.That(actual.Value, Does.Contain("\"correlationId\":\"invalid\""))
            Assert.That(actual.Value, Does.Contain("\"outcome\":\"failed\""))
            Assert.That(actual.Value, Does.Not.Contain("private-device"))
            Assert.That(actual.Value, Does.Not.Contain("response-body"))
            Assert.That(actual.Value, Does.Not.Contain("private-secret-stage"))
            Assert.That(actual.Value, Does.Contain("stage.other"))
        finally
            McpLatencyTrace.restore previous
