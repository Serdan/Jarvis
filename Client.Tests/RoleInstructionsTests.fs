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
