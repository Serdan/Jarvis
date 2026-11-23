
open Client.AutomationNativeAccountAuthority

[<TestFixture>]
type AutomationNativeAccountAuthorityTests() =
    let success value = NativeFixture.success value
    let deny result =
        match result with
        | Error(AccountPolicyNotAuthoritative _) -> ()
        | other -> failwithf "Expected fail-closed authority hold: %A" other

    let establish f =
        NativeReservationFixture.establish f (NativeFixture.limits 1 2)
            [| NativeFixture.limits 1 1; NativeFixture.limits 1 2 |]

    let roster f account key subject projects =
        readDisposableRoster f.Root account key subject projects

    let locator candidate operation (write: AccountWrite) =
        { ReservationOperationId = operation
          ReservationRequestSha256 = write.Receipt.RequestSha256
          Candidate = candidate
          ExpectedGeneration = 1L
          ExpectedProjectFenceOid = ""
          ExpectedProviderSessionId = "" }

    [<Test>]
    member _.SignedCompleteRosterAndHeadAreVerifiedAcrossClones() =
        NativeFixture.withGit (fun f ->
            deny (roster f f.AccountOne f.PublicKey f.Subject f.ProjectsOne)
            establish f
            let one = roster f f.AccountOne f.PublicKey f.Subject f.ProjectsOne |> success
            let two = roster f f.AccountTwo f.PublicKey f.Subject f.ProjectsTwo |> success
            Assert.That(two, Is.EqualTo(one))
            Assert.That(two.AccountId, Is.EqualTo(f.AccountTwo.AccountId))
            Assert.That(two.AccountHead, Is.EqualTo(NativeFixture.head f.AccountTwo))
            Assert.That(two.ProjectHeads.Length, Is.EqualTo(2))
            Assert.That(two.ApprovedProjects.Length, Is.EqualTo(2))
            Assert.That(two.AccountLimits, Is.EqualTo(NativeFixture.limits 1 2))
            deny (roster f f.AccountTwo f.PublicKey "foreign-subject" f.ProjectsTwo)
            deny (roster f f.AccountTwo f.PublicKey f.Subject [| f.ProjectsTwo[0] |])
            deny (roster f { f.AccountTwo with Epoch = 2 } f.PublicKey f.Subject f.ProjectsTwo)
            deny (roster f f.AccountTwo f.PublicKey f.Subject
                    [| f.ProjectsTwo[0]; { f.ProjectsTwo[1] with Epoch = 2 } |])
            use foreign = ECDsa.Create(ECCurve.NamedCurves.nistP256)
            deny (roster f f.AccountTwo (foreign.ExportSubjectPublicKeyInfo()) f.Subject f.ProjectsTwo)
            deny (readProductionRoster ())
            Assert.That(NativeFixture.head f.AccountTwo, Is.EqualTo(two.AccountHead)))

    [<Test>]
    member _.ExactOriginalReceiptAndCurrentHeadsSurviveIndependentCloneAndReplay() =
        NativeFixture.withGit (fun f ->
            establish f
            let candidate = NativeReservationFixture.worker f 0
            let op = Guid.NewGuid()
            let originalCoverage = NativeReservationFixture.coverage f f.AccountOne f.ProjectsOne
            let write = NativeReservationFixture.reserve f f.AccountOne f.ProjectsOne
                            originalCoverage op candidate |> success
            let replay = NativeReservationFixture.reserve f f.AccountTwo f.ProjectsTwo
                            originalCoverage op candidate |> success
            Assert.That(replay.AlreadyApplied, Is.True)
            let exact = locator candidate op write
            let coverage = NativeReservationFixture.coverage f f.AccountTwo f.ProjectsTwo
            let read witness input =
                readDisposableOriginal f.Root f.AccountTwo f.PublicKey f.Subject f.ProjectsTwo witness input
            let context = read coverage exact |> success
            Assert.That(context.AccountHead, Is.EqualTo(write.CurrentHead))
            Assert.That(context.ReservationOrigin, Is.EqualTo(write.CurrentHead))
            Assert.That(context.ReservationParent, Is.EqualTo(originalCoverage.AccountHead))
            Assert.That(context.ReservationReceipt, Is.EqualTo(write.Receipt))
            Assert.That(context.CurrentReservation.Candidate, Is.EqualTo(candidate))
            deny (read originalCoverage exact)
            deny (read { coverage with Complete = false } exact)
            deny (read { coverage with ProjectHeads = [| coverage.ProjectHeads[0] |] } exact)
            deny (read coverage { exact with ReservationOperationId = Guid.NewGuid() })
            deny (read coverage { exact with ReservationRequestSha256 = String.replicate 64 "0" })
            deny (read coverage { exact with ExpectedGeneration = 2L })
            deny (read coverage { exact with Candidate = { candidate with EffectOperationId = string (Guid.NewGuid()) } })
            deny (readProductionOriginal exact)
            deny (reconcileProduction exact)
            Assert.That(NativeFixture.head f.AccountTwo, Is.EqualTo(context.AccountHead))
            // Issuing another canonical project candidate changes project head
            // while the account stays unchanged; the old observation must fail.
            NativeReservationFixture.worker f 1 |> ignore
            deny (read coverage exact)
            let updated = NativeReservationFixture.coverage f f.AccountTwo f.ProjectsTwo
            let current = read updated exact |> success
            Assert.That(current.ReservationOrigin, Is.EqualTo(context.ReservationOrigin))
            Assert.That(current.VerifiedProjectHeads, Is.Not.EqualTo(context.VerifiedProjectHeads)))

    [<Test>]
    member _.UnknownRemainsOccupiedAndOnlyOriginalProviderTerminalReleases() =
        NativeFixture.withGit (fun f ->
            establish f
            let candidate = NativeReservationFixture.worker f 0
            let competing = NativeReservationFixture.worker f 1
            let reviewer = NativeReservationFixture.reviewer f 1
            let op = Guid.NewGuid()
            let coverage () = NativeReservationFixture.coverage f f.AccountOne f.ProjectsOne
            let write = NativeReservationFixture.reserve f f.AccountOne f.ProjectsOne (coverage ()) op candidate |> success
            let dispatch, effect = Guid.Parse candidate.DispatchId, Guid.Parse candidate.EffectOperationId
            let fence = (LedgerStore.fetch f.ProjectsOne[0] |> success).Commit
            let move generation status session =
                NativeSchedulerAccountReservation.transitionVerifiedDisposable
                    f.Root f.AccountOne f.PublicKey f.Subject f.ProjectsOne (coverage ())
                    (Guid.NewGuid()) "Fixture actor" dispatch effect generation status fence session
                |> success |> ignore
            move 1L "Dispatching" ""
            move 2L "Uncertain" ""
            move 3L "Unknown" "fixture-session"
            let exact =
                { locator candidate op write with
                    ExpectedGeneration = 4L
                    ExpectedProjectFenceOid = fence
                    ExpectedProviderSessionId = "fixture-session" }
            let read input =
                readDisposableOriginal f.Root f.AccountTwo f.PublicKey f.Subject f.ProjectsTwo
                    (NativeReservationFixture.coverage f f.AccountTwo f.ProjectsTwo) input
            let before = read exact |> success
            Assert.That(before.CurrentReservation.Status, Is.EqualTo("Unknown"))
            deny (read { exact with ExpectedProviderSessionId = "foreign-session" })
            deny (read { exact with ExpectedProjectFenceOid = String.replicate 40 "0" })
            deny (reconcileProduction exact)
            Assert.That(NativeFixture.head f.AccountTwo, Is.EqualTo(before.AccountHead))
            match NativeReservationFixture.reserve f f.AccountOne f.ProjectsOne (coverage ()) (Guid.NewGuid()) competing with
            | Error(AccountCapacityFull(1,1)) -> ()
            | other -> failwithf "Unknown released capacity: %A" other
            NativeReservationFixture.reserve f f.AccountOne f.ProjectsOne (coverage ()) (Guid.NewGuid()) reviewer
                |> success |> ignore
            let terminal =
                { DispatchId = dispatch; OriginalReservationId = op; EffectOperationId = effect
                  TerminalOperationId = Guid.NewGuid() }
            let proof = NativeReservationFixture.terminalProof f candidate before.CurrentReservation
                            terminal fence "fixture-session"
            let verifier =
                { new INativeDisposableTerminalVerifier with
                    member _.VerifyCurrentTerminal _ = Ok proof }
            let release account projects witness releaseId request =
                NativeSchedulerAccountReservation.releaseVerifiedDisposable
                    f.Root account f.PublicKey f.Subject projects witness verifier
                    releaseId "Fixture terminal verifier" request
            deny (release f.AccountOne f.ProjectsOne (coverage ()) (Guid.NewGuid())
                    { terminal with OriginalReservationId = Guid.NewGuid() })
            let w1 = coverage ()
            let w2 = NativeReservationFixture.coverage f f.AccountTwo f.ProjectsTwo
            use gate = new ManualResetEventSlim(false)
            let first = Task.Run(fun () -> gate.Wait(); release f.AccountOne f.ProjectsOne w1 (Guid.NewGuid()) terminal)
            let second = Task.Run(fun () -> gate.Wait(); release f.AccountTwo f.ProjectsTwo w2 (Guid.NewGuid()) terminal)
            gate.Set()
            Task.WaitAll([| first :> Task; second :> Task |])
            Assert.That([| first.Result; second.Result |] |> Array.filter Result.isOk |> Array.length, Is.EqualTo(1))
            let released = read { exact with ExpectedGeneration = 5L } |> success
            Assert.That(released.CurrentReservation.Status, Is.EqualTo("Released"))
            Assert.That(released.ReservationReceipt, Is.EqualTo(write.Receipt))
            NativeReservationFixture.reserve f f.AccountOne f.ProjectsOne (coverage ()) (Guid.NewGuid()) competing
                |> success |> ignore)
