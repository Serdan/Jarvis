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
            Assert.That((LedgerStore.putRecord first change |> success).AlreadyApplied, Is.False)
            Assert.That((LedgerStore.putRecord second change |> success).AlreadyApplied, Is.True)
            Assert.That((LedgerStore.readRecord second "records/a.json" |> success |> snd), Is.EqualTo(Some change.Content))
            let snapshot, record = LedgerStore.getOperationStatus second change.OperationId |> success
            Assert.That(snapshot.Commit, Is.Not.Empty)
            Assert.That(record.IsSome, Is.True)
            Assert.That(record.Value.OperationId, Is.EqualTo(change.OperationId.ToString("D")))
            Assert.That(record.Value.Sequence, Is.GreaterThan(0L))
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
