module Client.Tests.LedgerStoreTests
open System
open System.IO
open System.Diagnostics
open Client.LedgerStore
open NUnit.Framework

[<TestFixture>]
type LedgerStoreTests() =
    let git dir args =
        use proc = new Process()
        proc.StartInfo.FileName <- "git"
        proc.StartInfo.WorkingDirectory <- dir
        proc.StartInfo.RedirectStandardOutput <- true
        proc.StartInfo.RedirectStandardError <- true
        args |> List.iter proc.StartInfo.ArgumentList.Add
        Assert.That(proc.Start(), Is.True)
        let output = proc.StandardOutput.ReadToEnd()
        let errors = proc.StandardError.ReadToEnd()
        proc.WaitForExit()
        Assert.That(proc.ExitCode, Is.EqualTo(0), errors)
        output.Trim()

    let success = function
        | Ok value -> value
        | Error error -> failwithf "Ledger failure: %A" error

    [<Test>]
    member _.CrossCloneIdempotency() =
        let root = Path.Combine(Path.GetTempPath(), "jarvis-v2-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        try
            let bare = Path.Combine(root, "remote.git")
            let one = Path.Combine(root, "one")
            let two = Path.Combine(root, "two")
            git root [ "init"; "--bare"; bare ] |> ignore
            git root [ "clone"; bare; one ] |> ignore
            File.WriteAllText(Path.Combine(one, "source.txt"), "untouched")
            git one [ "add"; "source.txt" ] |> ignore
            git one [ "-c"; "user.name=Fixture"; "-c"; "user.email=fixture@example.test"; "commit"; "-m"; "source" ] |> ignore
            git one [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
            git root [ "clone"; bare; two ] |> ignore
            let projectId = Guid.NewGuid()
            let ledgerId = Guid.NewGuid()
            let binding path =
                { RepositoryPath = path
                  RemoteName = "origin"
                  ApprovedFetchUrl = bare
                  ApprovedPushUrl = bare
                  ProjectId = projectId
                  LedgerId = ledgerId
                  Epoch = 1 }
            let first = binding one
            let second = binding two
            let before = git one [ "rev-parse"; "HEAD" ]
            let initial = LedgerStore.initialize first true |> success
            Assert.That((git one [ "cat-file"; "-p"; initial.Commit ]).Contains("parent "), Is.False)
            Assert.That((LedgerStore.fetch second |> success).Commit, Is.EqualTo(initial.Commit))
            let change =
                { OperationId = Guid.NewGuid()
                  Actor = "Agent 1"
                  Path = "records/a.json"
                  ExpectedBlobOid = None
                  Content = "{\"value\":1}" }
            let firstWrite = LedgerStore.putRecord first change |> success
            Assert.That(firstWrite.AlreadyApplied, Is.False)
            let replay = LedgerStore.putRecord second change |> success
            Assert.That(replay.AlreadyApplied, Is.True)
            Assert.That(replay.Operation.SchemaVersion, Is.EqualTo(2))
            Assert.That(replay.Operation.CanonicalRequestJson, Is.EqualTo(firstWrite.Operation.CanonicalRequestJson))
            Assert.That(replay.Operation.OriginalResultJson, Is.EqualTo(firstWrite.Operation.OriginalResultJson))
            Assert.That(replay.Operation.OriginalResultHash, Is.EqualTo(firstWrite.Operation.OriginalResultHash))
            use initialReceipt = System.Text.Json.JsonDocument.Parse(firstWrite.Operation.OriginalResultJson)
            Assert.That(initialReceipt.RootElement.GetProperty("sequence").GetInt64(),
                        Is.EqualTo(firstWrite.Operation.Sequence))
            let originalValue = initialReceipt.RootElement.GetProperty("changes")[0]
            Assert.That(originalValue.GetProperty("path").GetString(), Is.EqualTo("records/a.json"))
            Assert.That(originalValue.GetProperty("committedBlobOid").GetString(),
                        Is.EqualTo(firstWrite.Operation.BlobOid))
            Assert.That((LedgerStore.readRecord second "records/a.json" |> success |> snd), Is.EqualTo(Some change.Content))
            let snapshot, record = LedgerStore.getOperationStatus second change.OperationId |> success
            Assert.That(snapshot.Commit, Is.Not.Empty)
            Assert.That(record.IsSome, Is.True)
            Assert.That(record.Value.OperationId, Is.EqualTo(change.OperationId.ToString("D")))
            Assert.That(record.Value.Sequence, Is.GreaterThan(0L))
            Assert.That(record.Value.OriginalResultJson, Is.EqualTo(firstWrite.Operation.OriginalResultJson))
            let unrelated =
                { change with OperationId = Guid.NewGuid(); Path = "records/b.json"; Content = """{"value":2}""" }
            LedgerStore.putRecord first unrelated |> success |> ignore
            let laterReplay = LedgerStore.putRecord second change |> success
            Assert.That(laterReplay.AlreadyApplied, Is.True)
            Assert.That(laterReplay.CurrentHead, Is.Not.EqualTo(firstWrite.CurrentHead))
            Assert.That(laterReplay.Operation.OriginalResultJson, Is.EqualTo(firstWrite.Operation.OriginalResultJson))
            let canonicalId = Guid.NewGuid()
            let canonical =
                { OperationId = canonicalId; Actor = "Agent 1"
                  Kind = "CanonicalJsonTest"; RequestJson = """{ "b": 2, "a": 1 }""" }
            let canonicalWrite =
                LedgerStore.transact first canonical (fun _ ->
                    Ok [ { Path = "records/canonical.json"
                           ExpectedBlobOid = None
                           Content = """{"accepted":true}""" } ])
                |> success
            let reordered =
                { canonical with RequestJson = """{"a":1,"b":2}""" }
            let canonicalReplay =
                LedgerStore.transact second reordered (fun _ ->
                    Error(StaleEntityVersion "The planner must not execute on a duplicate."))
                |> success
            Assert.That(canonicalReplay.AlreadyApplied, Is.True)
            Assert.That(canonicalReplay.Operation.RequestHash,
                        Is.EqualTo(canonicalWrite.Operation.RequestHash))
            Assert.That(canonicalReplay.Operation.CanonicalRequestJson,
                        Is.EqualTo(canonicalWrite.Operation.CanonicalRequestJson))
            Assert.That(canonicalReplay.Operation.OriginalResultJson,
                        Is.EqualTo(canonicalWrite.Operation.OriginalResultJson))
            let malformed =
                { OperationId = Guid.NewGuid(); Actor = "Agent 1"
                  Kind = "CanonicalJsonTest"; RequestJson = """{"a":1,"a":2}""" }
            match LedgerStore.transact first malformed (fun _ ->
                    Error(StaleEntityVersion "Not called")) with
            | Error(InvalidBinding _) -> ()
            | other -> Assert.Fail(sprintf "Duplicate JSON properties must be rejected: %A" other)
            let _, absent = LedgerStore.getOperationStatus second (Guid.NewGuid()) |> success
            Assert.That(absent.IsNone, Is.True)

            match LedgerStore.putRecord second { change with Content = "different" } with
            | Error OperationIdReused -> ()
            | result -> Assert.Fail(sprintf "Expected OperationIdReused: %A" result)
            Assert.That(git one [ "rev-parse"; "HEAD" ], Is.EqualTo(before))
            Assert.That(git one [ "status"; "--porcelain" ], Is.Empty)
            Assert.That(git two [ "status"; "--porcelain" ], Is.Empty)
        finally
            Directory.Delete(root, true)

    [<Test>]
    member _.ConcurrentWritersAndRewriteDetection() =
        let root = Path.Combine(Path.GetTempPath(), "jarvis-v2-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        try
            let bare = Path.Combine(root, "remote.git")
            let one = Path.Combine(root, "one")
            let two = Path.Combine(root, "two")
            git root [ "init"; "--bare"; bare ] |> ignore
            git root [ "clone"; bare; one ] |> ignore
            File.WriteAllText(Path.Combine(one, "source.txt"), "untouched")
            git one [ "add"; "source.txt" ] |> ignore
            git one [ "-c"; "user.name=Fixture"; "-c"; "user.email=fixture@example.test"; "commit"; "-m"; "source" ] |> ignore
            git one [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
            git root [ "clone"; bare; two ] |> ignore
            let projectId = Guid.NewGuid()
            let ledgerId = Guid.NewGuid()
            let binding path =
                { RepositoryPath = path
                  RemoteName = "origin"
                  ApprovedFetchUrl = bare
                  ApprovedPushUrl = bare
                  ProjectId = projectId
                  LedgerId = ledgerId
                  Epoch = 1 }
            let first = binding one
            let second = binding two
            LedgerStore.initialize first true |> success |> ignore
            LedgerStore.fetch second |> success |> ignore
            let put binding path =
                LedgerStore.putRecord binding
                    { OperationId = Guid.NewGuid()
                      Actor = "Agent 2"
                      Path = path
                      ExpectedBlobOid = None
                      Content = "{\"ok\":true}" }
                |> success
            let a = System.Threading.Tasks.Task.Run(fun () -> put first "records/a.json")
            let b = System.Threading.Tasks.Task.Run(fun () -> put second "records/b.json")
            System.Threading.Tasks.Task.WaitAll([| a :> System.Threading.Tasks.Task; b :> System.Threading.Tasks.Task |])
            Assert.That((LedgerStore.readRecord first "records/a.json" |> success |> snd).IsSome)
            Assert.That((LedgerStore.readRecord second "records/b.json" |> success |> snd).IsSome)
            let sourceRoot = git one [ "rev-list"; "--max-parents=0"; "HEAD" ]
            git one [ "push"; "--force"; "origin"; sourceRoot + ":refs/heads/jarvis-orchestration" ] |> ignore
            match LedgerStore.fetch first with
            | Error(LedgerIntegrityError _) -> ()
            | result -> Assert.Fail(sprintf "Expected rewrite rejection, got %A" result)
        finally
            Directory.Delete(root, true)
