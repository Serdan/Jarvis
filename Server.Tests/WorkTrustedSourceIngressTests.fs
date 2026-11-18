module WorkTrustedSourceIngressTests

open System
open System.IO
open System.Text.Json.Nodes
open NUnit.Framework
open Server

let private account = "11111111-1111-4111-8111-111111111111"
let private project = "22222222-2222-4222-8222-222222222222"
let private otherProject = "33333333-3333-4333-8333-333333333333"
let private ledger = "44444444-4444-4444-8444-444444444444"
let private operation = "55555555-5555-4555-8555-555555555555"
let private sha = String.replicate 40 "a"

let private claim (kind: string) (effect: string) =
    let origin = JsonObject()
    origin.Add("accountId", JsonValue.Create account)
    origin.Add("projectId", JsonValue.Create project)
    origin.Add("ledgerId", JsonValue.Create ledger)
    origin.Add("repositoryId", JsonValue.Create(String.replicate 64 "b"))
    origin.Add("ref", JsonValue.Create("refs/heads/main"))
    origin.Add("commit", JsonValue.Create sha)
    origin.Add("operationId", JsonValue.Create operation)
    origin.Add("sequence", JsonValue.Create(7L))
    let versions = JsonObject()
    versions.Add("row", JsonValue.Create(1L))
    versions.Add("graph", JsonValue.Create(2L))
    versions.Add("stage", JsonValue.Create(1L))
    versions.Add("claim", JsonValue.Create(0L))
    versions.Add("stop", JsonValue.Create(0L))
    versions.Add("role", JsonValue.Create(1L))
    let body = JsonObject()
    body.Add("state", JsonValue.Create("Open"))
    let value = JsonObject()
    value.Add("formatVersion", JsonValue.Create(1))
    value.Add("accountId", JsonValue.Create account)
    value.Add("projectId", JsonValue.Create project)
    value.Add("kind", JsonValue.Create kind)
    value.Add("entityId", JsonValue.Create("work-A"))
    value.Add("source", origin)
    value.Add("versions", versions)
    value.Add("body", body)
    if kind = "external-effect" then
        value.Add("effectState", JsonValue.Create effect)
        value.Add("effectOperationId", JsonValue.Create("66666666-6666-4666-8666-666666666666"))
    value

let private inspect (node: JsonObject) =
    WorkTrustedSourceIngress.inspectUntrusted (node.ToJsonString())

let private isIncomplete = function
    | WorkTrustedSourceIngressDisposition.Incomplete _ -> true
    | _ -> false

let private isRejected = function
    | WorkTrustedSourceIngressDisposition.Rejected _ -> true
    | _ -> false

[<TestCase("project")>]
[<TestCase("work")>]
[<TestCase("stage")>]
[<TestCase("launch")>]
[<TestCase("role")>]
[<TestCase("review")>]
[<TestCase("integration")>]
[<TestCase("external-effect")>]
let all_eight_kinds_remain_incomplete_without_authentic_publisher kind =
    let verdict = inspect (claim kind "observed")
    Assert.That(isIncomplete verdict, Is.True, $"Untrusted {kind} cannot become a source witness: {verdict}")

[<Test>]
let valid_git_shaped_claim_is_not_authenticated () =
    let body = claim "stage" ""
    Assert.That(isIncomplete (inspect body), Is.True)
    // Even a second apparently identical receipt is not authorization.
    Assert.That(isIncomplete (inspect body), Is.True)

[<Test>]
let stale_or_cross_project_claim_cannot_seed_sqlite () =
    let body = claim "stage" ""
    body["projectId"] <- JsonValue.Create otherProject
    Assert.That(isRejected (inspect body), Is.True)
    body["projectId"] <- JsonValue.Create project
    body["source"].AsObject()["commit"] <- JsonValue.Create(String.replicate 40 "c")
    Assert.That(isIncomplete (inspect body), Is.True)

[<Test>]
let unknown_kind_or_format_and_extra_key_are_rejected () =
    Assert.That(isRejected (inspect (claim "future-kind" "")), Is.True)
    let body = claim "project" ""
    body["formatVersion"] <- JsonValue.Create(2)
    Assert.That(isRejected (inspect body), Is.True)
    body["formatVersion"] <- JsonValue.Create(1)
    body.Add("verified", JsonValue.Create(true))
    Assert.That(isRejected (inspect body), Is.True)

[<Test>]
let source_identity_must_be_bounded_and_well_formed_but_is_not_authority () =
    let body = claim "stage" ""
    body["entityId"] <- JsonValue.Create(String.replicate 257 "x")
    Assert.That(isRejected (inspect body), Is.True)
    body["entityId"] <- JsonValue.Create("work-A")
    body["source"].AsObject()["ref"] <- JsonValue.Create("refs/heads/../../evil")
    Assert.That(isRejected (inspect body), Is.True)
    body["source"].AsObject()["ref"] <- JsonValue.Create("refs/heads/main")
    body["source"].AsObject()["repositoryId"] <- JsonValue.Create("claimed repository")
    Assert.That(isRejected (inspect body), Is.True)

[<Test>]
let missing_source_cannot_be_upgraded_from_json () =
    let body = claim "review" ""
    body.Remove("source") |> ignore
    Assert.That(isIncomplete (inspect body), Is.True)

[<Test>]
let duplicate_nested_json_names_and_negative_generations_are_rejected () =
    let raw = (claim "stage" "").ToJsonString()
    let duplicate = raw.Replace("\"state\":\"Open\"", "\"state\":\"Open\",\"state\":\"Blocked\"")
    Assert.That(
        WorkTrustedSourceIngress.inspectUntrusted duplicate |> isRejected,
        Is.True)
    let body = claim "stage" ""
    body["versions"].AsObject()["stop"] <- JsonValue.Create(-1L)
    Assert.That(isRejected (inspect body), Is.True)

[<Test>]
let prepared_and_unknown_effects_remain_uncertain () =
    for state in [ "prepared"; "unknown" ] do
        let verdict = inspect (claim "external-effect" state)
        match verdict with
        | WorkTrustedSourceIngressDisposition.UncertainEffect _ -> ()
        | _ -> Assert.Fail($"Effect {state} must remain uncertain: {verdict}")

[<Test>]
let secret_bearing_body_never_becomes_a_mutation_or_leaks_in_errors () =
    let body = claim "launch" ""
    body["body"].AsObject()["manualGrant"] <- JsonValue.Create("a-secret-test-value")
    let verdict = inspect body
    Assert.That(isRejected verdict, Is.True)
    Assert.That(string verdict, Does.Not.Contain("a-secret-test-value"))
    let nested = claim "external-effect" "unknown"
    let child = JsonObject()
    child.Add("originalStages", JsonValue.Create("bearer-test-secret"))
    nested["body"].AsObject().Add("recovery", child)
    let result = inspect nested
    Assert.That(isRejected result, Is.True)
    Assert.That(string result, Does.Not.Contain("bearer-test-secret"))

[<Test>]
let untrusted_claims_cannot_write_or_advance_an_authority () =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-ingress-negative-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    try
        let store = WorkSqliteAuthority(Path.Combine(root, "work.sqlite"))
        match store.Initialize() with
        | Ok _ -> ()
        | Error issue -> Assert.Fail($"Unexpected fixture storage failure: {issue}")
        let before = store.CurrentSequence()
        let body = claim "stage" ""
        Assert.That(isIncomplete (inspect body), Is.True)
        Assert.That(store.CurrentSequence(), Is.EqualTo before)
        let restarted = WorkSqliteAuthority(Path.Combine(root, "work.sqlite"))
        Assert.That(restarted.CurrentSequence(), Is.EqualTo before)
    finally
        Directory.Delete(root, true)
