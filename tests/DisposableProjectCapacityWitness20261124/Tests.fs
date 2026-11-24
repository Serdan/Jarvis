module DisposableProjectCapacityWitnessTests
open System
open NUnit.Framework
open Client.LedgerStore
open Client.DisposableProjectCapacityWitness
open ProjectCapacityDisposableTests

[<Test>]
let freshLedgerHoldsAndRejectsMismatchedIdentity () =
    withClones (fun a _ ->
        let snapshot = LedgerStore.fetch a |> good
        match inspect a snapshot with
        | CapacityEvidenceMissing (project, ledger, epoch, head, missing) ->
            Assert.That(project, Is.EqualTo(a.ProjectId))
            Assert.That(ledger, Is.EqualTo(a.LedgerId))
            Assert.That(epoch, Is.EqualTo(a.Epoch))
            Assert.That(head, Is.EqualTo(snapshot.Commit))
            Assert.That(missing.Length, Is.EqualTo(5))
        | other -> Assert.Fail(sprintf "Expected explicit hold, got %A" other)
        Assert.That(inspect { a with Epoch = a.Epoch + 1 } snapshot, Is.EqualTo(IdentityMismatch))
        Assert.That(inspect { a with ProjectId = Guid.NewGuid() } snapshot, Is.EqualTo(IdentityMismatch))
        Assert.That(inspect { a with LedgerId = Guid.NewGuid() } snapshot, Is.EqualTo(IdentityMismatch)))
