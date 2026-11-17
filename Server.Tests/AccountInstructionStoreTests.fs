module AccountInstructionStoreTests

open System
open System.IO
open System.Security.Claims
open System.Text
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Options
open ModelContextProtocol.Protocol
open NUnit.Framework
open Common
open Server
open Server.Services

let private temporaryFolder action =
    let path = Path.Combine(Path.GetTempPath(), "jarvis-account-instructions-" + Guid.NewGuid().ToString("N"))
    try action path
    finally
        try if Directory.Exists path then Directory.Delete(path, true) with _ -> ()

let private expectPresent content snapshot =
    Assert.That(snapshot.Status, Is.EqualTo("present"))
    Assert.That(snapshot.Content, Is.EqualTo(Some content))
    Assert.That(snapshot.Sha256.IsSome, Is.True)
    Assert.That(snapshot.Revision, Does.StartWith("sha256:"))
    Assert.That(snapshot.Problem.IsNone, Is.True)

let private commit store subject revision content =
    match (store: AccountInstructionStore).Update(subject, revision, content) with
    | Ok snapshot -> snapshot
    | Error error -> failwithf "Expected successful update, got %A" error

let private context subject scopes =
    let http = DefaultHttpContext()
    let claims = [ Claim("sub", subject); Claim("scope", scopes) ]
    http.User <- ClaimsPrincipal(ClaimsIdentity(claims, "unit-test"))
    let options: JarvisOptions =
        { Auth0Domain = "example.auth0.test"; Audience = "https://example.test"
          OpenAIAppsChallenge = ""; EnableOperatorConversations = false }
    http.RequestServices <-
        { new IServiceProvider with
            member _.GetService serviceType =
                if serviceType = typeof<IOptions<JarvisOptions>> then box(Options.Create(options))
                else null }
    // HttpContextAccessor's global AsyncLocal would alias distinct principals
    // constructed on the same test thread. Keep the two request contexts separate.
    let mutable current: HttpContext = http
    let accessor =
        { new IHttpContextAccessor with
            member _.HttpContext with get () = current and set value = current <- value }
    http, accessor

[<Test>]
let ``fresh missing is distinct from present empty and nonempty`` () =
    temporaryFolder (fun root ->
        let store = AccountInstructionStore(root)
        let first = store.Read("alice")
        Assert.That(first.Status, Is.EqualTo("missing"))
        Assert.That(first.Content.IsNone, Is.True)
        Assert.That(first.Revision, Is.EqualTo("missing"))
        let empty = commit store "alice" "missing" ""
        expectPresent "" empty
        Assert.That(empty.Revision, Is.Not.EqualTo("missing"))
        let content = "# Shared guidance\n"
        commit store "alice" empty.Revision content |> expectPresent content
        Assert.That(store.Read("bob").Status, Is.EqualTo("missing")))

[<Test>]
let ``restart preloads exact content and account files stay private`` () =
    temporaryFolder (fun root ->
        let original = AccountInstructionStore(root)
        let written = commit original "auth0|alice" "missing" "# Private\nDo not deploy."
        let expectedPath = Path.Combine(root, Auth.profileId "auth0|alice", "AGENTS.md")
        Assert.That(File.Exists expectedPath, Is.True)
        Assert.That(File.ReadAllText(expectedPath), Is.EqualTo("# Private\nDo not deploy."))
        Assert.That(File.Exists(Path.Combine(root, Auth.profileId "auth0|bob", "AGENTS.md")), Is.False)
        let restarted = AccountInstructionStore(root)
        let loaded = restarted.Read("auth0|alice")
        expectPresent "# Private\nDo not deploy." loaded
        Assert.That(loaded.Revision, Is.EqualTo(written.Revision))
        Assert.That(restarted.Read("auth0|bob").Status, Is.EqualTo("missing"))
        if not (OperatingSystem.IsWindows()) then
            let fileMode = File.GetUnixFileMode expectedPath
            Assert.That(fileMode &&& UnixFileMode.GroupRead, Is.EqualTo(enum<UnixFileMode> 0))
            Assert.That(fileMode &&& UnixFileMode.OtherRead, Is.EqualTo(enum<UnixFileMode> 0))
            let dirMode = File.GetUnixFileMode (Path.GetDirectoryName expectedPath)
            Assert.That(dirMode &&& UnixFileMode.GroupExecute, Is.EqualTo(enum<UnixFileMode> 0))
            Assert.That(dirMode &&& UnixFileMode.OtherExecute, Is.EqualTo(enum<UnixFileMode> 0)))

[<Test>]
let ``competing edits with one expected revision have one winner`` () =
    temporaryFolder (fun root ->
        let store = AccountInstructionStore(root)
        let initial = commit store "alice" "missing" "initial"
        let results =
            [| 1 .. 24 |]
            |> Array.Parallel.map (fun i -> store.Update("alice", initial.Revision, string i))
        let successes =
            results |> Array.filter (function Ok _ -> true | _ -> false)
        let conflicts =
            results |> Array.filter (function Error(RevisionConflict _) -> true | _ -> false)
        Assert.That(successes.Length, Is.EqualTo(1))
        Assert.That(conflicts.Length, Is.EqualTo(23))
        let current = store.Read("alice")
        Assert.That(current.Revision, Is.Not.EqualTo(initial.Revision))
        Assert.That(current.Content, Is.Not.EqualTo(Some "initial")))

let private expectInvalid (expected: string)
                          (result: Result<AccountInstructionSnapshot, AccountInstructionUpdateError>) =
    match result with
    | Error(InvalidInput reason) -> Assert.That(reason, Is.EqualTo(expected))
    | other -> Assert.Fail($"Expected invalid input, got {other}")

[<Test>]
let ``input bounds UTF16 integrity and strict revision are enforced`` () =
    temporaryFolder (fun root ->
        let store = AccountInstructionStore(root)
        let invalid = String([| char 0xD800 |])
        store.Update("alice", "missing", invalid)
        |> expectInvalid "Content contains an invalid UTF-16 surrogate."
        store.Update("alice", "missing", String.replicate 65537 "x")
        |> expectInvalid "Content exceeds 64 KiB."
        store.Update("alice", "", "text")
        |> expectInvalid "An exact expected revision from GetAccountInstructions is required."
        let wrote = commit store "alice" "missing" "new"
        match store.Update("alice", "missing", "overwrite") with
        | Error(RevisionConflict revision) -> Assert.That(revision, Is.EqualTo(wrote.Revision))
        | other -> Assert.Fail($"Stale update did not conflict: {other}")
        expectPresent "new" (store.Read("alice")))

[<Test>]
let ``preload preserves invalid UTF8 and linked files as unavailable`` () =
    temporaryFolder (fun root ->
        let account = Path.Combine(root, Auth.profileId "alice")
        Directory.CreateDirectory(account) |> ignore
        File.WriteAllBytes(Path.Combine(account, "AGENTS.md"), [| 0xFFuy; 0xFEuy |])
        let store = AccountInstructionStore(root)
        let invalid = store.Read("alice")
        Assert.That(invalid.Status, Is.EqualTo("unavailable"))
        Assert.That(invalid.Content.IsNone, Is.True)
        Assert.That(invalid.Problem.IsSome, Is.True)
        match store.Update("alice", "unavailable", "replacement") with
        | Error(StorageUnavailable _) -> ()
        | other -> Assert.Fail($"Unreadable file must be repaired, not overwritten: {other}")
        let repeated = store.NextDelivery("alice", "Worker 9", "Jarvis", "device", 1L, false)
        Assert.That(repeated.IsSome, Is.True)
        Assert.That(store.NextDelivery("alice", "Worker 9", "Jarvis", "device", 1L, false).IsSome, Is.True)
        if not (OperatingSystem.IsWindows()) then
            let linked = Path.Combine(root, Auth.profileId "bob")
            Directory.CreateDirectory(linked) |> ignore
            File.CreateSymbolicLink(Path.Combine(linked, "AGENTS.md"),
                                    Path.Combine(account, "AGENTS.md")) |> ignore
            let fromLink = AccountInstructionStore(root)
            Assert.That(fromLink.Read("bob").Status, Is.EqualTo("unavailable")))

[<Test>]
let ``delivery is revision-aware independent of device project and generation`` () =
    temporaryFolder (fun root ->
        let store = AccountInstructionStore(root)
        // Default missing does not repeatedly advertise an absent policy.
        Assert.That(store.NextDelivery("alice", "Worker 9", "Jarvis", "device", 1L, false).IsNone, Is.True)
        let first = commit store "alice" "missing" "global"
        let delivery () = store.NextDelivery("alice", "Worker 9", "Jarvis", "device", 1L, false)
        Assert.That((delivery()).IsSome, Is.True)
        Assert.That((delivery()).IsNone, Is.True)
        Assert.That(store.NextDelivery("alice", "Worker 9", "Other", "device", 1L, false).IsSome, Is.True)
        Assert.That(store.NextDelivery("alice", "Worker 9", "Jarvis", "laptop", 1L, false).IsSome, Is.True)
        Assert.That(store.NextDelivery("alice", "Worker 9", "Jarvis", "device", 2L, false).IsSome, Is.True)
        Assert.That(store.NextDelivery("alice", "Worker 9", "Jarvis", "device", 1L, true).IsSome, Is.True)
        commit store "alice" first.Revision "revised" |> ignore
        Assert.That((delivery()).Value.Content, Is.EqualTo(Some "revised")))

[<Test>]
let ``MCP read and mutation derive account from authenticated principal and scopes`` () =
    temporaryFolder (fun root ->
        let store = AccountInstructionStore(root)
        let _, alice = context "alice" "workspace:read workspace:write"
        let _, bob = context "bob" "workspace:read workspace:write"
        let read who = McpToolHelpers.getAccountInstructions store who |> fun task -> task.GetAwaiter().GetResult()
        let mutate who revision body =
            McpToolHelpers.updateAccountInstructions store who revision body
            |> fun task -> task.GetAwaiter().GetResult()
        let inserted = mutate alice "missing" "Alice policy"
        Assert.That(inserted.IsError.Value, Is.False)
        let first = read alice
        Assert.That(first.IsError.Value, Is.False)
        let second = read bob
        Assert.That(second.IsError.Value, Is.False)
        let firstText = (first.Content.[0] :?> TextContentBlock).Text
        let secondText = (second.Content.[0] :?> TextContentBlock).Text
        Assert.That(firstText, Does.Contain("Alice policy"))
        Assert.That(secondText, Does.Contain("missing"))
        Assert.That(secondText, Does.Not.Contain("Alice policy"))
        let _, withoutWrite = context "alice" "workspace:read"
        Assert.That((mutate withoutWrite "missing" "bad").IsError.Value, Is.True)
        let _, withoutRead = context "alice" "workspace:write"
        Assert.That((read withoutRead).IsError.Value, Is.True)
        let _, unauthenticated = context "" "workspace:read workspace:write"
        Assert.That((read unauthenticated).IsError.Value, Is.True))

[<Test>]
let ``explicit hierarchy presentation puts account guidance before repository guidance`` () =
    temporaryFolder (fun root ->
        let store = AccountInstructionStore(root)
        commit store "alice" "missing" "global first" |> ignore
        let http, _ = context "alice" "workspace:read"
        let response: AgentCommandResponse =
            { Result = Some "\"\""; Error = None; Instructions = []
              AgentStateChanges = []; InboxActivation = None; PendingJob = None }
        let command = GetAgentInstructionsCommand
                          { ProjectName = "Jarvis"; AgentId = "Worker 9"
                            Directory = ""; Cursor = None }
        let output = CallToolResult()
        output.Content <- ResizeArray<ContentBlock>([
            TextContentBlock(Text = "repository second") :> ContentBlock ])
        let delivered = McpToolHelpers.attachAccountGlobal store (UserService()) command http response output
        let first = (delivered.Content.[0] :?> TextContentBlock).Text
        let second = (delivered.Content.[1] :?> TextContentBlock).Text
        Assert.That(first, Does.Contain("global first"))
        Assert.That(first, Does.Contain("account-global"))
        Assert.That(second, Is.EqualTo("repository second")))


[<Test>]
let ``account IDs resembling paths cannot escape the private store root`` () =
    temporaryFolder (fun root ->
        let suspicious = "../../other\\../../account"
        let store = AccountInstructionStore(root)
        commit store suspicious "missing" "# contained" |> ignore
        let onlyFolder = Directory.GetDirectories(root)
        Assert.That(onlyFolder.Length, Is.EqualTo(1))
        let folderName = Path.GetFileName(onlyFolder[0])
        Assert.That(folderName, Is.EqualTo(Auth.profileId suspicious))
        Assert.That(folderName.Length, Is.EqualTo(64))
        Assert.That(File.Exists(Path.Combine(onlyFolder[0], "AGENTS.md")), Is.True)
        Assert.That(store.Read("other").Status, Is.EqualTo("missing")))

[<Test>]
let ``interrupted temporary write does not replace the complete committed policy`` () =
    temporaryFolder (fun root ->
        let initialStore = AccountInstructionStore(root)
        let first = commit initialStore "alice" "missing" "# complete old"
        let folder = Path.Combine(root, Auth.profileId "alice")
        let unfinished = Path.Combine(folder, ".AGENTS.md.aborted.tmp")
        File.WriteAllBytes(unfinished, Encoding.UTF8.GetBytes("# partial new"))
        let recovered = AccountInstructionStore(root)
        expectPresent "# complete old" (recovered.Read("alice"))
        Assert.That(recovered.Read("alice").Revision, Is.EqualTo(first.Revision))
        let updated = commit recovered "alice" first.Revision "# complete new"
        let restarted = AccountInstructionStore(root)
        expectPresent "# complete new" (restarted.Read("alice"))
        Assert.That(updated.Revision, Is.EqualTo(restarted.Read("alice").Revision)))
