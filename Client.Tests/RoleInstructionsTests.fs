module Client.Tests.RoleInstructionsTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open Client.LedgerStore
open Client.RoleInstructions
open Client.ReviewWorkflow
open NUnit.Framework

[<TestFixture>]
type RoleInstructionsTests() =
    let git path args =
        use proc = new Process()
        proc.StartInfo.FileName <- "git"
        proc.StartInfo.WorkingDirectory <- path
        proc.StartInfo.RedirectStandardOutput <- true
        proc.StartInfo.RedirectStandardError <- true
        args |> List.iter proc.StartInfo.ArgumentList.Add
        Assert.That(proc.Start(), Is.True)
        let stdout = proc.StandardOutput.ReadToEnd()
        let stderr = proc.StandardError.ReadToEnd()
        proc.WaitForExit()
        Assert.That(proc.ExitCode, Is.EqualTo(0), stderr)
        stdout.Trim()

    let success = function
        | Ok value -> value
        | Error failure -> failwithf "Unexpected ledger error: %A" failure

    let expectStale = function
        | Error(StaleEntityVersion _) -> ()
        | other -> Assert.Fail(sprintf "Expected version/identity conflict: %A" other)

    let fixture action =
        let root = Path.Combine(Path.GetTempPath(), "jarvis-roles-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        try
            let bare = Path.Combine(root, "remote.git")
            let owner = Path.Combine(root, "owner")
            let reader = Path.Combine(root, "reader")
            git root [ "init"; "--bare"; bare ] |> ignore
            git root [ "clone"; bare; owner ] |> ignore
            git owner [ "config"; "user.name"; "Role Fixture" ] |> ignore
            git owner [ "config"; "user.email"; "fixture@example.test" ] |> ignore
            File.WriteAllText(Path.Combine(owner, "source.txt"), "untouched")
            git owner [ "add"; "source.txt" ] |> ignore
            git owner [ "commit"; "-m"; "base" ] |> ignore
            git owner [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
            git root [ "clone"; bare; reader ] |> ignore
            git reader [ "config"; "user.name"; "Role Fixture" ] |> ignore
            git reader [ "config"; "user.email"; "fixture@example.test" ] |> ignore
            let project = Guid.NewGuid()
            let ledger = Guid.NewGuid()
            let binding path : LedgerBinding =
                { RepositoryPath = path; RemoteName = "origin"
                  ApprovedFetchUrl = bare; ApprovedPushUrl = bare
                  ProjectId = project; LedgerId = ledger; Epoch = 1 }
            let one, two = binding owner, binding reader
            LedgerStore.initialize one true |> success |> ignore
            for agent in [ "Reviewer 1"; "Reviewer 2"; "Reviewer 3"; "Worker A" ] do
                ReviewWorkflow.registerAgent one (Guid.NewGuid()) agent |> success |> ignore
            action one two
        finally
            for checkout in [ "owner"; "reader" ] do
                let config =
                    Client.OrchestrationApi.LedgerBindingConfig.pathForRoot(
                        Path.Combine(root, checkout))
                if File.Exists config then File.Delete config
                if File.Exists(config + ".lock") then File.Delete(config + ".lock")
            Directory.Delete(root, true)

    [<Test>]
    member _.SharedRoleAndDistinctAgentInstructionsAreIndependentOfTaskAssignment() =
        fixture (fun one two ->
            let created =
                RoleInstructions.createRole one "AuthenticatedUser" (Guid.NewGuid()) "Reviewer"
                    "Independent source review" "Review code and source evidence." |> success
            Assert.That(created.AlreadyApplied, Is.False)
            let role = RoleInstructions.getRole two "Reviewer" |> success
            Assert.That(role.Role.Version, Is.EqualTo(1))
            Assert.That(role.Role.Purpose, Is.EqualTo("Independent source review"))
            Assert.That(role.BlobOid, Is.Not.Empty)
            Assert.That((RoleInstructions.listRoles two |> success).Length, Is.EqualTo(1))

            for name in [ "Reviewer 1"; "Reviewer 2"; "Reviewer 3" ] do
                RoleInstructions.attachRole one "AuthenticatedUser" (Guid.NewGuid())
                    name "Reviewer" 0 |> success |> ignore
            RoleInstructions.setAgentInstructions one "AuthenticatedUser" (Guid.NewGuid())
                "Reviewer 1" 1 "Focus on concurrency." |> success |> ignore
            RoleInstructions.setAgentInstructions one "AuthenticatedUser" (Guid.NewGuid())
                "Reviewer 2" 1 "Focus on serialization." |> success |> ignore

            for name in [ "Reviewer 1"; "Reviewer 2"; "Reviewer 3" ] do
                let inbox = ReviewWorkflow.getAgentInbox two name |> success
                Assert.That(inbox.Agent.CurrentAssignment, Is.Null)
                Assert.That(inbox.InstructionLayers.RoleIds, Is.EqualTo(box [ "Reviewer" ]))
                Assert.That(inbox.InstructionLayers.Documents.Head.Content,
                            Is.EqualTo("Review code and source evidence."))
                Assert.That(inbox.InstructionLayers.Documents.Head.Version, Is.EqualTo(1))
                Assert.That(inbox.InstructionLayers.Documents.Head.BlobOid, Is.EqualTo(role.BlobOid))
            let oneLayers = RoleInstructions.resolve two "Reviewer 1" |> success
            let twoLayers = RoleInstructions.resolve two "Reviewer 2" |> success
            let threeLayers = RoleInstructions.resolve two "Reviewer 3" |> success
            Assert.That(oneLayers.Documents |> List.map (fun layer -> layer.Scope),
                        Is.EqualTo(box [ "role"; "agent" ]))
            Assert.That(oneLayers.Documents[1].Content, Is.EqualTo("Focus on concurrency."))
            Assert.That(twoLayers.Documents[1].Content, Is.EqualTo("Focus on serialization."))
            Assert.That(threeLayers.Documents.Length, Is.EqualTo(1))
            Assert.That(threeLayers.Precedence,
                        Is.EqualTo(box [ "global"; "workspace"; "project/directory";
                                          "role (attachment order)"; "agent"; "assignment" ]))

            let oldRoleBlob = role.BlobOid
            let before = RoleInstructions.getAgentConfig two "Reviewer 1" |> success
            let updated =
                RoleInstructions.updateRole one "AuthenticatedUser" (Guid.NewGuid()) "Reviewer"
                    1 "Independent source review" "Review for source, semantic and behavior regressions."
                |> success
            Assert.That(updated.AlreadyApplied, Is.False)
            let refreshed = RoleInstructions.resolve two "Reviewer 1" |> success
            Assert.That(refreshed.Documents.Head.Version, Is.EqualTo(2))
            Assert.That(refreshed.Documents.Head.Content,
                        Is.EqualTo("Review for source, semantic and behavior regressions."))
            Assert.That(refreshed.Documents.Head.BlobOid, Is.Not.EqualTo(oldRoleBlob))
            Assert.That(refreshed.Documents[1].Content, Is.EqualTo("Focus on concurrency."))
            Assert.That((RoleInstructions.getAgentConfig two "Reviewer 1" |> success).Value.Config.Version,
                        Is.EqualTo(before.Value.Config.Version))
            // The recorded old content survives a later role edit as immutable Git evidence.
            let earlier = LedgerStore.verifiedHistoricalSnapshot two role.SnapshotCommit |> success
            let oldRaw, oldOid = LedgerStore.readAt two earlier "records/roles/Reviewer.json" |> success
            Assert.That(oldOid, Is.EqualTo(Some oldRoleBlob))
            Assert.That(oldRaw.Value.Contains("Review code and source evidence."),
                        Is.True)
            Assert.That((RoleInstructions.resolve two "Reviewer 3" |> success).Documents.Head.Version,
                        Is.EqualTo(2))
            Assert.That((RoleInstructions.resolve two "Worker A" |> success).Documents, Is.Empty))

    [<Test>]
    member _.VersionConflictsAndRetriesAreDeterministicAcrossClones() =
        fixture (fun one two ->
            let op = Guid.NewGuid()
            let first =
                RoleInstructions.createRole one "Lead" op "Reviewer"
                    "Review" "Inspect every source change." |> success
            let replay =
                RoleInstructions.createRole two "Lead" op "Reviewer"
                    "Review" "Inspect every source change." |> success
            Assert.That(replay.AlreadyApplied, Is.True)
            Assert.That(replay.Operation.RequestHash, Is.EqualTo(first.Operation.RequestHash))
            match RoleInstructions.createRole one "Lead" op "Reviewer" "Review" "Different" with
            | Error OperationIdReused -> ()
            | other -> Assert.Fail(sprintf "Operation ID re-use must be rejected: %A" other)
            expectStale (RoleInstructions.createRole one "Lead" (Guid.NewGuid())
                             "Reviewer" "Review" "Another version")
            expectStale (RoleInstructions.updateRole two "Lead" (Guid.NewGuid())
                             "Reviewer" 2 "Review" "Version gap")
            RoleInstructions.updateRole two "Lead" (Guid.NewGuid())
                "Reviewer" 1 "Review" "Second version" |> success |> ignore
            expectStale (RoleInstructions.updateRole one "Lead" (Guid.NewGuid())
                             "Reviewer" 1 "Review" "Stale optimistic update")

            expectStale (RoleInstructions.attachRole one "Lead" (Guid.NewGuid())
                             "Missing Agent" "Reviewer" 0)
            expectStale (RoleInstructions.attachRole one "Lead" (Guid.NewGuid())
                             "Reviewer 1" "MissingRole" 0)
            RoleInstructions.attachRole one "Lead" (Guid.NewGuid())
                "Reviewer 1" "Reviewer" 0 |> success |> ignore
            expectStale (RoleInstructions.attachRole two "Lead" (Guid.NewGuid())
                             "Reviewer 1" "Reviewer" 0)
            expectStale (RoleInstructions.setAgentInstructions two "Lead" (Guid.NewGuid())
                             "Reviewer 1" 0 "Stale")
            RoleInstructions.setAgentInstructions two "Lead" (Guid.NewGuid())
                "Reviewer 1" 1 "Personal instructions" |> success |> ignore
            expectStale (RoleInstructions.detachRole one "Lead" (Guid.NewGuid())
                             "Reviewer 1" "Reviewer" 1)
            RoleInstructions.detachRole one "Lead" (Guid.NewGuid())
                "Reviewer 1" "Reviewer" 2 |> success |> ignore
            let resolved = RoleInstructions.resolve two "Reviewer 1" |> success
            Assert.That(resolved.RoleIds, Is.Empty)
            Assert.That(resolved.Documents.Length, Is.EqualTo(1))
            Assert.That(resolved.Documents.Head.Scope, Is.EqualTo("agent"))
            Assert.That(resolved.Documents.Head.Content, Is.EqualTo("Personal instructions"))
        )

    [<Test>]
    member _.TamperedRoleRecordsFailClosedAndUnauthorizedArgumentsAreRejected() =
        fixture (fun one two ->
            RoleInstructions.createRole one "Lead" (Guid.NewGuid())
                "Reviewer" "Review" "Read-only code inspection" |> success |> ignore
            match RoleInstructions.createRole one "" (Guid.NewGuid())
                    "Bad" "Purpose" "Instructions" with
            | Error(InvalidBinding _) -> ()
            | other -> Assert.Fail(sprintf "Empty actor rejected: %A" other)
            match RoleInstructions.createRole one "Lead" (Guid.NewGuid())
                    "../Escape" "Purpose" "Instructions" with
            | Error(InvalidBinding _) -> ()
            | other -> Assert.Fail(sprintf "Invalid role ID rejected: %A" other)

            // Authorized high-level writers never delete a role. If a low-level
            // ledger mutation breaks canonical integrity, instruction reads fail.
            let snapshot = LedgerStore.fetch one |> success
            let _, oid = LedgerStore.readAt one snapshot "records/roles/Reviewer.json" |> success
            let wrong =
                """{"schemaVersion":1,"roleId":"Different","purpose":"Review","instructions":"X","version":2,"updatedBy":"Lead","updatedSequence":999}"""
            LedgerStore.transact one
                { OperationId = Guid.NewGuid(); Actor = "Test"; Kind = "TamperFixture"
                  RequestJson = """{"fixture":true}""" }
                (fun _ ->
                    Ok [ { Path = "records/roles/Reviewer.json"; ExpectedBlobOid = oid; Content = wrong } ])
            |> success |> ignore
            match RoleInstructions.getRole two "Reviewer" with
            | Error(LedgerIntegrityError _) -> ()
            | other -> Assert.Fail(sprintf "Tampered role identity must fail: %A" other))


    [<Test>]
    member _.ExplicitInstructionsCombineCheckoutHierarchyAndCanonicalRolesWithoutDuplication() =
        fixture (fun one _ ->
            let root = Path.GetDirectoryName(one.RepositoryPath)
            let workspaceInstructions = Path.Combine(root, "AGENTS.md")
            File.WriteAllText(workspaceInstructions, "Workspace scope applies.")
            File.WriteAllText(Path.Combine(one.RepositoryPath, "AGENTS.md"), "Main checkout scope applies.")
            let roleBody = "Review against the canonical ledger; keep source changes independent."
            RoleInstructions.createRole one "Lead" (Guid.NewGuid())
                "Reviewer" "Stable reviewer responsibility" roleBody |> success |> ignore
            RoleInstructions.attachRole one "Lead" (Guid.NewGuid())
                "Reviewer 1" "Reviewer" 0 |> success |> ignore
            RoleInstructions.setAgentInstructions one "Lead" (Guid.NewGuid())
                "Reviewer 1" 1 "Check serialized boundaries." |> success |> ignore

            let rt = Client.Runtime(root)
            let trusted = LedgerStore.fetch one |> success
            match Client.OrchestrationApi.LedgerBindingConfig.bindExisting
                    "owner" rt one.ProjectId one.LedgerId one.Epoch
                    one.RemoteName one.ApprovedFetchUrl one.ApprovedPushUrl
                    trusted.Commit true with
            | Error error -> Assert.Fail(sprintf "Cannot bind fixture ledger: %A" error)
            | Ok _ -> ()

            let inspectAs selector cursor agentId =
                let target =
                    Client.ProjectPaths.ProjectTarget.parse selector
                    |> function
                        | Ok value -> value
                        | Error error -> failwithf "Invalid fixture selector: %A" error
                let command : Common.GetAgentInstructionsCommand =
                    { ProjectName = selector
                      AgentId = agentId
                      Directory = ""
                      Cursor = cursor }
                let result =
                    Client.AgentInstructions.getExplicit target command rt
                    |> function
                        | Ok value -> value
                        | Error error -> failwithf "Cannot resolve layered instructions: %A" error
                JsonDocument.Parse(result.ToJsonString())

            let inspect selector cursor = inspectAs selector cursor "Reviewer 1"
            // A repository read must remain available before agent registration.
            use unregistered = inspectAs "owner" None "Unregistered Reader"
            Assert.That(unregistered.RootElement.GetProperty("AGENTS.md").EnumerateObject() |> Seq.length,
                        Is.EqualTo(2))
            let mutable missing = Unchecked.defaultof<JsonElement>
            Assert.That(unregistered.RootElement.TryGetProperty("instructionLayers", &missing), Is.False)

            use main = inspect "owner" None
            let mainRoot = main.RootElement
            let mainRepo = mainRoot.GetProperty("AGENTS.md")
            Assert.That(mainRepo.EnumerateObject() |> Seq.length, Is.EqualTo(2))
            Assert.That(mainRepo.GetProperty(".").GetString(), Is.EqualTo("Workspace scope applies."))
            Assert.That(mainRepo.GetProperty("owner").GetString(), Is.EqualTo("Main checkout scope applies."))
            let roleLayers = mainRoot.GetProperty("instructionLayers")
            let ids = roleLayers.GetProperty("RoleIds")
            Assert.That(ids[0].GetString(), Is.EqualTo("Reviewer"))
            Assert.That(roleLayers.GetProperty("AgentConfigVersion").GetInt32(), Is.EqualTo(2))
            let docs = roleLayers.GetProperty("Documents")
            Assert.That(docs.GetArrayLength(), Is.EqualTo(2))
            Assert.That(docs[0].GetProperty("Scope").GetString(), Is.EqualTo("role"))
            Assert.That(docs[0].GetProperty("Content").GetString(), Is.EqualTo(roleBody))
            Assert.That(docs[1].GetProperty("Scope").GetString(), Is.EqualTo("agent"))
            Assert.That(docs[1].GetProperty("Content").GetString(), Is.EqualTo("Check serialized boundaries."))
            Assert.That(docs[0].GetProperty("Sha256").GetString(), Does.StartWith("sha256:"))

            // Switch to an actual registered checkout. It contributes its own
            // path-scoped AGENTS.md while both checkouts share ledger roles.
            let checkout = Path.Combine(root, "roles-checkout")
            git one.RepositoryPath [ "worktree"; "add"; "-b"; "test-role-worktree"; checkout ] |> ignore
            File.WriteAllText(Path.Combine(checkout, "AGENTS.md"), "Worktree scope applies.")
            use worktree = inspect "owner::roles-checkout" None
            let worktreeRoot = worktree.RootElement
            let repository = worktreeRoot.GetProperty("AGENTS.md")
            Assert.That(repository.EnumerateObject() |> Seq.length, Is.EqualTo(2))
            Assert.That(repository.GetProperty("owner").GetString(), Is.EqualTo("Worktree scope applies."))
            Assert.That(repository.GetRawText(), Does.Not.Contain("Main checkout scope applies."))
            let worktreeDocs =
                worktreeRoot.GetProperty("instructionLayers").GetProperty("Documents")
            Assert.That(worktreeDocs[0].GetProperty("Content").GetString(), Is.EqualTo(roleBody))

            // The first page carries full canonical role information; local
            // AGENTS.md continuation pages are bounded and do not re-fetch it.
            File.WriteAllText(workspaceInstructions, String('x', 1100000))
            use page1 = inspect "owner::roles-checkout" None
            Assert.That(System.Text.Encoding.UTF8.GetByteCount(page1.RootElement.GetRawText()),
                        Is.LessThanOrEqualTo(640 * 1024))
            let nextCursor = page1.RootElement.GetProperty("nextCursor").GetString()
            Assert.That(nextCursor, Is.Not.Empty)
            use page2 = inspect "owner::roles-checkout" (Some nextCursor)
            Assert.That(System.Text.Encoding.UTF8.GetByteCount(page2.RootElement.GetRawText()),
                        Is.LessThanOrEqualTo(640 * 1024))
            let later = page2.RootElement.GetProperty("instructionLayers")
            Assert.That(later.GetProperty("status").GetString(), Is.EqualTo("continuation"))
            Assert.That(later.GetProperty("complete").GetBoolean(), Is.False)
            Assert.That(later.GetProperty("code").GetString(), Is.EqualTo("RoleLayersNotRepeated")))

    [<Test>]
    member _.EscapedBodySizeIsBoundedAndMalformedStoredInstructionsFailClosed() =
        fixture (fun one two ->
            let pathological = String(char 1, 3000)
            match RoleInstructions.createRole one "Lead" (Guid.NewGuid()) "Oversized"
                    "Escaped control bytes" pathological with
            | Error(InvalidBinding _) -> ()
            | other -> Assert.Fail(sprintf "JSON-expanded size must be rejected: %A" other)
            RoleInstructions.createRole one "Lead" (Guid.NewGuid()) "Reviewer"
                "Reviewer role" "Canonical review guidance" |> success |> ignore
            match RoleInstructions.setAgentInstructions one "Lead" (Guid.NewGuid())
                    "Reviewer 1" 0 pathological with
            | Error(InvalidBinding _) -> ()
            | other -> Assert.Fail(sprintf "JSON-expanded agent body must be rejected: %A" other)
            RoleInstructions.attachRole one "Lead" (Guid.NewGuid())
                "Reviewer 1" "Reviewer" 0 |> success |> ignore
            let prior = LedgerStore.fetch one |> success
            let configPath = "records/agent-instructions/" +
                             Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("Reviewer 1"))
                                .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".json"
            let _, oid = LedgerStore.readAt one prior configPath |> success
            let malicious =
                """{"schemaVersion":1,"agentId":"Reviewer 1","roleIds":["Nonexistent"],"instructions":"","version":2,"updatedBy":"Lead","updatedSequence":999}"""
            LedgerStore.transact one
                { OperationId = Guid.NewGuid(); Actor = "Test"; Kind = "InvalidRoleFixture"
                  RequestJson = """{"fixture":true}""" }
                (fun _ ->
                    Ok [ { Path = configPath; ExpectedBlobOid = oid; Content = malicious } ])
            |> success |> ignore
            match RoleInstructions.resolve two "Reviewer 1" with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Missing referenced role must fail: %A" other))


    [<Test>]
    member _.SameAgentAndRoleNamesAreIsolatedBetweenProjectLedgers() =
        fixture (fun first _ ->
            let root = Path.GetDirectoryName(first.RepositoryPath)
            RoleInstructions.createRole first "Lead" (Guid.NewGuid()) "Reviewer"
                "First project review" "Use the first project's requirements." |> success |> ignore
            RoleInstructions.attachRole first "Lead" (Guid.NewGuid())
                "Reviewer 1" "Reviewer" 0 |> success |> ignore

            let remote = Path.Combine(root, "second.git")
            let checkout = Path.Combine(root, "second")
            git root [ "init"; "--bare"; remote ] |> ignore
            git root [ "clone"; remote; checkout ] |> ignore
            git checkout [ "config"; "user.name"; "Role Fixture" ] |> ignore
            git checkout [ "config"; "user.email"; "fixture@example.test" ] |> ignore
            File.WriteAllText(Path.Combine(checkout, "source.txt"), "second project")
            git checkout [ "add"; "source.txt" ] |> ignore
            git checkout [ "commit"; "-m"; "second project base" ] |> ignore
            git checkout [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
            let second : LedgerBinding =
                { first with RepositoryPath = checkout; ApprovedFetchUrl = remote
                             ApprovedPushUrl = remote; ProjectId = Guid.NewGuid()
                             LedgerId = Guid.NewGuid() }
            LedgerStore.initialize second true |> success |> ignore
            ReviewWorkflow.registerAgent second (Guid.NewGuid()) "Reviewer 1" |> success |> ignore
            RoleInstructions.createRole second "Lead" (Guid.NewGuid()) "Reviewer"
                "Second project review" "Use only the second project's requirements."
            |> success |> ignore
            RoleInstructions.attachRole second "Lead" (Guid.NewGuid())
                "Reviewer 1" "Reviewer" 0 |> success |> ignore

            let firstResolved = RoleInstructions.resolve first "Reviewer 1" |> success
            let secondResolved = RoleInstructions.resolve second "Reviewer 1" |> success
            Assert.That(firstResolved.Documents.Length, Is.EqualTo(1))
            Assert.That(secondResolved.Documents.Length, Is.EqualTo(1))
            Assert.That(firstResolved.Documents.Head.Content,
                        Is.EqualTo("Use the first project's requirements."))
            Assert.That(secondResolved.Documents.Head.Content,
                        Is.EqualTo("Use only the second project's requirements."))
            Assert.That(firstResolved.SnapshotCommit, Is.Not.EqualTo(secondResolved.SnapshotCommit))
            Assert.That(firstResolved.Documents.Head.BlobOid, Is.Not.EqualTo(secondResolved.Documents.Head.BlobOid)))


    [<Test>]
    member _.RoleAttachmentOrderAndIndividualOverrideAreDeterministic() =
        fixture (fun first _ ->
            RoleInstructions.createRole first "Lead" (Guid.NewGuid())
                "Architecture" "Architecture guidance" "Review architectural dependencies."
            |> success |> ignore
            RoleInstructions.createRole first "Lead" (Guid.NewGuid())
                "Reviewer" "Review guidance" "Review test and source evidence."
            |> success |> ignore
            RoleInstructions.attachRole first "Lead" (Guid.NewGuid())
                "Reviewer 1" "Architecture" 0 |> success |> ignore
            RoleInstructions.attachRole first "Lead" (Guid.NewGuid())
                "Reviewer 1" "Reviewer" 1 |> success |> ignore
            RoleInstructions.setAgentInstructions first "Lead" (Guid.NewGuid())
                "Reviewer 1" 2 "Use careful diff inspection." |> success |> ignore
            let combined = RoleInstructions.resolve first "Reviewer 1" |> success
            Assert.That(combined.RoleIds, Is.EqualTo(box [ "Architecture"; "Reviewer" ]))
            Assert.That(combined.Documents |> List.map (fun layer -> layer.Scope),
                        Is.EqualTo(box [ "role"; "role"; "agent" ]))
            Assert.That(combined.Documents[0].Content, Is.EqualTo("Review architectural dependencies."))
            Assert.That(combined.Documents[1].Content, Is.EqualTo("Review test and source evidence."))
            Assert.That(combined.Documents[2].Content, Is.EqualTo("Use careful diff inspection."))
            RoleInstructions.setAgentInstructions first "Lead" (Guid.NewGuid())
                "Reviewer 1" 3 "" |> success |> ignore
            let cleared = RoleInstructions.resolve first "Reviewer 1" |> success
            Assert.That(cleared.AgentConfigVersion, Is.EqualTo(4))
            Assert.That(cleared.RoleIds, Is.EqualTo(box [ "Architecture"; "Reviewer" ]))
            Assert.That(cleared.Documents.Length, Is.EqualTo(2)))


    [<Test>]
    member _.OfflineLedgerPreservesLocalHierarchyWithIncompleteRoleWarningAndPages() =
        fixture (fun binding _ ->
            let workspace = Path.GetDirectoryName(binding.RepositoryPath)
            File.WriteAllText(Path.Combine(workspace, "AGENTS.md"), String('w', 900000))
            File.WriteAllText(Path.Combine(binding.RepositoryPath, "AGENTS.md"), "Local project guidance.")
            RoleInstructions.createRole binding "Lead" (Guid.NewGuid())
                "Reviewer" "Review responsibility" "Canonical reviewer instructions."
            |> success |> ignore
            RoleInstructions.attachRole binding "Lead" (Guid.NewGuid())
                "Reviewer 1" "Reviewer" 0 |> success |> ignore
            let rt = Client.Runtime(workspace)
            let trusted = LedgerStore.fetch binding |> success
            match Client.OrchestrationApi.LedgerBindingConfig.bindExisting
                    "owner" rt binding.ProjectId binding.LedgerId binding.Epoch
                    binding.RemoteName binding.ApprovedFetchUrl binding.ApprovedPushUrl
                    trusted.Commit true with
            | Error error -> Assert.Fail(sprintf "Cannot bind fixture ledger: %A" error)
            | Ok _ -> ()

            let target =
                Client.ProjectPaths.ProjectTarget.parse "owner"
                |> function
                    | Ok value -> value
                    | Error failure -> failwithf "Invalid target: %A" failure
            let retrieve agentId cursor =
                let command : Common.GetAgentInstructionsCommand =
                    { ProjectName = "owner"; AgentId = agentId; Directory = ""; Cursor = cursor }
                Client.AgentInstructions.getExplicit target command rt
            let read agentId cursor =
                match retrieve agentId cursor with
                | Ok node -> JsonDocument.Parse(node.ToJsonString())
                | Error failure -> failwithf "Local hierarchy must survive remote outage: %A" failure

            use healthy = read "Reviewer 1" None
            let healthyDocs = healthy.RootElement.GetProperty("instructionLayers").GetProperty("Documents")
            Assert.That(healthyDocs[0].GetProperty("Content").GetString(),
                        Is.EqualTo("Canonical reviewer instructions."))

            // The approved bare Git remote disappears. This must not deny
            // local source guidance to a registered or unregistered reader.
            let unavailable = binding.ApprovedFetchUrl + "-offline"
            Directory.Move(binding.ApprovedFetchUrl, unavailable)
            use registered = read "Reviewer 1" None
            let source = registered.RootElement.GetProperty("AGENTS.md")
            Assert.That(source.GetProperty(".").GetString(), Does.StartWith(String('w', 100)))
            let roleWarning = registered.RootElement.GetProperty("instructionLayers")
            Assert.That(roleWarning.GetProperty("status").GetString(), Is.EqualTo("unavailable"))
            Assert.That(roleWarning.GetProperty("complete").GetBoolean(), Is.False)
            Assert.That(roleWarning.GetProperty("code").GetString(), Is.EqualTo("RemoteUnavailable"))
            Assert.That(roleWarning.GetProperty("message").GetString(), Does.Contain("NOT loaded"))

            use unregistered = read "Unregistered Reader" None
            Assert.That(unregistered.RootElement.GetProperty("AGENTS.md").GetProperty(".").GetString(),
                        Does.StartWith(String('w', 100)))
            Assert.That(unregistered.RootElement.GetProperty("instructionLayers")
                                  .GetProperty("status").GetString(), Is.EqualTo("unavailable"))

            let cursor = registered.RootElement.GetProperty("nextCursor").GetString()
            Assert.That(cursor, Is.Not.Empty)
            use continuation = read "Reviewer 1" (Some cursor)
            Assert.That(continuation.RootElement.GetProperty("AGENTS.md").GetProperty(".").GetString(),
                        Does.StartWith("wwww"))
            let continued = continuation.RootElement.GetProperty("instructionLayers")
            Assert.That(continued.GetProperty("status").GetString(), Is.EqualTo("continuation"))
            Assert.That(continued.GetProperty("complete").GetBoolean(), Is.False)
            Assert.That(continued.GetProperty("message").GetString(), Does.Contain("initial hierarchy page"))
            Assert.That(
                System.Text.Encoding.UTF8.GetByteCount(continuation.RootElement.GetRawText()),
                Is.LessThanOrEqualTo(640 * 1024))

            // An available but corrupt role record is NOT an outage: it must
            // continue to fail closed rather than return an incomplete warning.
            Directory.Move(unavailable, binding.ApprovedFetchUrl)
            let snapshot = LedgerStore.fetch binding |> success
            let rolePath = "records/roles/Reviewer.json"
            let _, oid = LedgerStore.readAt binding snapshot rolePath |> success
            let badRecord =
                """{"schemaVersion":1,"roleId":"OtherRole","purpose":"Review","instructions":"Untrusted","version":2,"updatedBy":"Lead","updatedSequence":999}"""
            LedgerStore.transact binding
                { OperationId = Guid.NewGuid(); Actor = "Fixture"; Kind = "CorruptRoleFixture"
                  RequestJson = """{"fixture":true}""" }
                (fun _ -> Ok [ { Path = rolePath; ExpectedBlobOid = oid; Content = badRecord } ])
            |> success |> ignore
            match retrieve "Reviewer 1" None with
            | Error(Client.LedgerFailureError detail) ->
                Assert.That(detail.Code, Is.EqualTo("LedgerIntegrityError"))
            | other -> Assert.Fail(sprintf "Corrupt canonical role must be rejected: %A" other))


    [<Test>]
    member _.MalformedBoundAgentConfigurationFailsClosedDuringExplicitRetrieval() =
        fixture (fun binding _ ->
            let workspace = Path.GetDirectoryName(binding.RepositoryPath)
            File.WriteAllText(Path.Combine(binding.RepositoryPath, "AGENTS.md"), "Valid local guidance.")
            RoleInstructions.createRole binding "Lead" (Guid.NewGuid())
                "Reviewer" "Review" "Valid canonical role guidance."
            |> success |> ignore
            RoleInstructions.attachRole binding "Lead" (Guid.NewGuid())
                "Reviewer 1" "Reviewer" 0 |> success |> ignore
            let rt = Client.Runtime(workspace)
            let snapshot = LedgerStore.fetch binding |> success
            match Client.OrchestrationApi.LedgerBindingConfig.bindExisting
                    "owner" rt binding.ProjectId binding.LedgerId binding.Epoch
                    binding.RemoteName binding.ApprovedFetchUrl binding.ApprovedPushUrl
                    snapshot.Commit true with
            | Error error -> Assert.Fail(sprintf "Cannot bind fixture ledger: %A" error)
            | Ok _ -> ()

            let configPath = "records/agent-instructions/" +
                             Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("Reviewer 1"))
                                .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".json"
            let _, oid = LedgerStore.readAt binding snapshot configPath |> success
            let wrongIdentity =
                """{"schemaVersion":1,"agentId":"Spoofed Agent","roleIds":["Reviewer"],"instructions":"","version":2,"updatedBy":"Lead","updatedSequence":999}"""
            LedgerStore.transact binding
                { OperationId = Guid.NewGuid(); Actor = "Fixture"; Kind = "CorruptAgentConfigFixture"
                  RequestJson = """{"fixture":true}""" }
                (fun _ ->
                    Ok [ { Path = configPath; ExpectedBlobOid = oid; Content = wrongIdentity } ])
            |> success |> ignore

            let target =
                Client.ProjectPaths.ProjectTarget.parse "owner"
                |> function
                    | Ok value -> value
                    | Error failure -> failwithf "Invalid target: %A" failure
            let command : Common.GetAgentInstructionsCommand =
                { ProjectName = "owner"; AgentId = "Reviewer 1"; Directory = ""; Cursor = None }
            match Client.AgentInstructions.getExplicit target command rt with
            | Error(Client.LedgerFailureError detail) ->
                Assert.That(detail.Code, Is.EqualTo("LedgerIntegrityError"))
            | other ->
                Assert.Fail(sprintf "Malformed agent config must not degrade into incomplete-role success: %A" other))
