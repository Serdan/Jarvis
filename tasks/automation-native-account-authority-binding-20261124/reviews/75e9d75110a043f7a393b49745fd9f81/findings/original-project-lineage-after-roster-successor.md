# Original reservation context succeeds without its original project lineage

**Blocking for the exact original-reservation context contract; default-off source only.** Frozen SHA `a5c1172a840a1fad414a085ba5d3e611c2c45ae0`, task spec blob `619c201b4afd523b37c5bc64e265124e69b8f833`. Task requires exact account/project lineage and explicit holds for foreign roster/epoch and incomplete evidence.

In `Client/AutomationNativeAccountAuthority.fs:134–169`, `readDisposableOriginal` verifies current account/roster coverage via `readVerifiedDisposable`, matches the current reservation/locator, and verifies its immutable historical account reserve receipt/append. It then returns Ok with the *current roster's* verified heads without requiring a head for the reservation candidate's original ProjectId/LedgerId/Epoch. Existing `AccountCapacityLedger.setVerifiedDisposableNativeRosterPolicy` (1296–1344) permits a genuinely signed successor roster with a changed complete membership. Current complete coverage need not cover the historical reservation's project. Thus an account-origin proof is promoted to successful original context even if the project side is absent. This is an evidence-boundary defect in the new read seam, not a claim of an actual unauthorized release.

Independent reproducer: detached review checkout at the exact frozen SHA; run the documented prepare.fsx and append the following test to the generated obj/Generated.AuthorityTests.fs (not tracked source); use the existing private two-clone fixture, then run dotnet test on the standalone project with --filter FullyQualifiedName~Reviewer2OriginalProjectLineageProbe.

```fsharp
[<TestFixture>]
type Reviewer2OriginalProjectLineageProbe() =
    [<Test>]
    member _.RemovedOriginalProjectCannotSupplyExactCurrentProjectContext() =
        NativeFixture.withGit (fun f ->
            let ok value = NativeFixture.success value
            NativeReservationFixture.establish f (NativeFixture.limits 1 2)
                [| NativeFixture.limits 1 1; NativeFixture.limits 1 2 |]
            let candidate = NativeReservationFixture.worker f 0
            let op = Guid.NewGuid()
            let original = NativeReservationFixture.coverage f f.AccountOne f.ProjectsOne
            let write = NativeReservationFixture.reserve f f.AccountOne f.ProjectsOne original op candidate |> ok
            let locator : NativeOriginalReservationLocator =
                { ReservationOperationId = op
                  ReservationRequestSha256 = write.Receipt.RequestSha256
                  Candidate = candidate
                  ExpectedGeneration = 1L
                  ExpectedProjectFenceOid = ""
                  ExpectedProviderSessionId = "" }
            let successor = NativeFixture.approval f 2L 1L (NativeFixture.head f.AccountOne)
                                (Guid.NewGuid()) (NativeFixture.limits 1 2)
                                [| NativeFixture.limits 1 1; NativeFixture.limits 1 2 |]
            let reduced =
                { successor with Policy = { successor.Policy with Projects = [| successor.Policy.Projects[1] |] } }
                |> NativeFixture.resign f
            NativeFixture.writer f f.AccountOne [| f.ProjectsOne[1] |] reduced |> ok |> ignore
            let survivingProjects = [| f.ProjectsTwo[1] |]
            let coverage = NativeReservationFixture.coverage f f.AccountTwo survivingProjects
            Assert.That(coverage.ProjectHeads |> Array.exists (fun h -> h.ProjectId = candidate.ProjectId), Is.False)
            match readDisposableOriginal f.Root f.AccountTwo f.PublicKey f.Subject survivingProjects coverage locator with
            | Error _ -> ()
            | Ok context ->
                Assert.Fail(sprintf "Verified original context returned without original project %s ledger %s epoch %d; verified heads = %A" candidate.ProjectId candidate.ProjectLedgerId candidate.ProjectEpoch context.VerifiedProjectHeads))
```

Observed independent job `c725bfabc0ca4f2dbf4c7c37e093873b`: exit 1, one discovered test and one assertion failure (not build or fixture failure); event 14–17: "Verified original context returned without original project 8006b445-2438-49d6-b918-42fdcb7342d6 ledger 02a4a973-03ad-4e49-ad3d-67278b728b02 epoch 1; verified heads = [{ ProjectId = \"411c4940-c7b2-4422-a861-61435237f8d0\" ... }]" (generated disposable IDs). Original candidate remains in account state. No production action occurred.

A bounded candidate remedy is to require exactly one verified head matching the reservation candidate's immutable project identity tuple, or represent missing original-project evidence explicitly as a nonauthoritative hold rather than returning the same verified context. Cover signed membership removal and changed identity/epoch; preserve the ability to inspect historical account evidence without implying verified project authority and retain occupancy on uncertainty. Do not require a new signing service, forbid all roster changes, or expand the project's trust model as part of this review. Remedy/scope adjudication remains separate under Operator policy.