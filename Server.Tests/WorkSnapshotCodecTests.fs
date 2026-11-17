module ServerWorkSnapshotCodecTests

open System
open System.Security.Cryptography
open System.Text
open NUnit.Framework
open Jarvis.WorkSnapshots

let private good = function
    | Ok x -> x
    | Error reason -> failwithf "Valid snapshot rejected: %s" reason

let private invalid = function
    | Error _ -> ()
    | Ok _ -> Assert.Fail "Malformed snapshot unexpectedly accepted"

let private source =
    { RepositoryId = "jarvis-repository"
      Ref = "refs/heads/feature/example"
      Commit = String.replicate 40 "a" }

let private record id kind revision sequence fields source : WorkSnapshotRecord =
    { Id = id
      Kind = kind
      Revision = revision
      Sequence = sequence
      Source = source
      Fields = Map.ofList fields }

let private fixture () =
    { CommittedSequence = 8L
      AccountRecords =
        [ record "capacity" "AccountCapacity" 3L 6L
            [ "maxConcurrent", "4"; "stopRequested", "false"; "status", "Active" ] None ]
      Projects =
        [ { ProjectId = "beta"
            Records =
              [ record "audit-beta" "AuditEvent" 1L 8L
                  [ "eventKind", "StagePublished"; "status", "Complete"; "workId", "beta-job" ] None ] }
          { ProjectId = "alpha"
            Records =
              [ record "reservation-alpha" "Reservation" 1L 6L
                  [ "workId", "unit-alpha"; "stageId", "stage-alpha";
                    "roleId", "Worker"; "status", "Registered"; "generation", "1" ] None
                record "verifier-alpha" "ScheduledVerifier" 1L 7L
                  [ "verifierSha256", String.replicate 64 "f"; "reservationId", "reservation-alpha" ] None
                record "stage-alpha" "WorkStage" 2L 5L
                  [ "workId", "unit-alpha"; "status", "Available"; "queueId", "implementation";
                    "priority", "2"; "generation", "1"; "availableSequence", "5" ] (Some source)
                record "unit-alpha" "WorkUnit" 1L 4L
                  [ "unitKind", "Feature"; "status", "Open"; "specSha", String.replicate 40 "b" ] None ] } ] }

let private editFile path f (bundle: WorkSnapshotBundle) =
    { Files = bundle.Files.Add(path, f bundle.Files.[path]) }

let private replaceBytes before after (bytes: byte[]) =
    bytes |> Encoding.UTF8.GetString |> fun s -> s.Replace(before, after, StringComparison.Ordinal)
    |> Encoding.UTF8.GetBytes

[<Test>]
let snapshot_codec_is_deterministic_readable_and_two_project_restorable () =
    let snapshot = fixture ()
    let a = WorkSnapshotCodec.exportSnapshot snapshot |> good
    let reversed = { snapshot with Projects = List.rev snapshot.Projects; AccountRecords = List.rev snapshot.AccountRecords }
    let b = WorkSnapshotCodec.exportSnapshot reversed |> good
    Assert.That(a.Files.Count, Is.EqualTo(4))
    Assert.That(a.Files.Keys |> Set.ofSeq, Is.EqualTo(box (set [
        "manifest.json"; "account.json"; "projects/alpha.json"; "projects/beta.json" ])))
    for KeyValue(path, bytes) in a.Files do
        Assert.That(bytes, Is.EqualTo(box b.Files.[path]), sprintf "Different bytes: %s" path)
        Assert.That(bytes.[0], Is.Not.EqualTo(0xEFuy))
        Assert.That(bytes.[bytes.Length - 1], Is.EqualTo(byte '\n'))
        Assert.That(Encoding.UTF8.GetString(bytes), Does.Contain("\"formatVersion\": 1"))
    let restored = WorkSnapshotCodec.importSnapshot a |> good
    let replay = WorkSnapshotCodec.exportSnapshot restored |> good
    for KeyValue(path, bytes) in a.Files do
        Assert.That(replay.Files.[path], Is.EqualTo(box bytes))
    Assert.That(restored.CommittedSequence, Is.EqualTo(8L))
    Assert.That(restored.Projects |> List.map _.ProjectId, Is.EqualTo(box [ "alpha"; "beta" ]))
    let joined = a.Files.Values |> Seq.map (fun bytes -> Encoding.UTF8.GetString(bytes)) |> String.concat "\n"
    Assert.That(joined, Does.Contain("refs/heads/feature/example"))
    Assert.That(joined, Does.Contain(String.replicate 64 "f"))

[<Test>]
let snapshot_codec_rejects_missing_extra_and_tampered_files () =
    let goodBundle = fixture () |> WorkSnapshotCodec.exportSnapshot |> good
    invalid (WorkSnapshotCodec.importSnapshot { Files = goodBundle.Files.Remove("account.json") })
    invalid (WorkSnapshotCodec.importSnapshot { Files = goodBundle.Files.Add("projects/ghost.json", [| 1uy |]) })
    invalid (WorkSnapshotCodec.importSnapshot (editFile "projects/alpha.json"
        (replaceBytes "\"status\": \"Available\"" "\"status\": \"Blocked\"") goodBundle))
    invalid (WorkSnapshotCodec.importSnapshot (editFile "manifest.json"
        (replaceBytes "\"committedSequence\": 8" "\"committedSequence\": 9") goodBundle))

[<Test>]
let snapshot_codec_rejects_duplicate_identifiers_invalid_sequences_and_unsafe_paths () =
    let snapshot = fixture ()
    let beta = snapshot.Projects.Head
    let sameId = { beta.Records.Head with Id = "stage-alpha" }
    let duplicateRecord = { beta with Records = [ sameId ] }
    let duplicate = { snapshot with Projects = duplicateRecord :: snapshot.Projects.Tail }
    invalid (WorkSnapshotCodec.exportSnapshot duplicate)
    let dishonest = { snapshot with CommittedSequence = 7L }
    invalid (WorkSnapshotCodec.exportSnapshot dishonest)
    let doubleProject = { snapshot with Projects = snapshot.Projects @ [ beta ] }
    invalid (WorkSnapshotCodec.exportSnapshot doubleProject)
    let unsafeProject = { beta with ProjectId = "../escape" }
    let unsafeSnapshot = { snapshot with Projects = unsafeProject :: snapshot.Projects.Tail }
    invalid (WorkSnapshotCodec.exportSnapshot unsafeSnapshot)
    let futureRecord = { snapshot.AccountRecords.Head with Sequence = 100L }
    let future = { snapshot with AccountRecords = [ futureRecord ] }
    invalid (WorkSnapshotCodec.exportSnapshot future)

[<Test>]
let snapshot_codec_rejects_plain_scheduled_bearers_manual_grants_and_unknown_fields () =
    let snapshot = fixture ()
    let beta = snapshot.Projects.Head
    let root = beta.Records.Head
    let token = "d9f1234e-2e50-4f44-8b8e-d1d798292263"
    let manual = { root with Kind = "ManualGrant" }
    let manualProject = { beta with Records = [ manual ] }
    invalid (WorkSnapshotCodec.exportSnapshot { snapshot with Projects = manualProject :: snapshot.Projects.Tail })
    let tokenRecord = { root with Fields = root.Fields.Add("bearerToken", token) }
    let tokenProject = { beta with Records = [ tokenRecord ] }
    invalid (WorkSnapshotCodec.exportSnapshot { snapshot with Projects = tokenProject :: snapshot.Projects.Tail })
    let alpha = snapshot.Projects.Tail.Head
    let verifier = alpha.Records.Head
    let plaintext = { verifier with Fields = verifier.Fields.Add("verifierSha256", token) }
    let badAlpha = { alpha with Records = plaintext :: alpha.Records.Tail }
    invalid (WorkSnapshotCodec.exportSnapshot { snapshot with Projects = [ beta; badAlpha ] })
    // An allowed identifier key cannot silently refer to a non-existent
    // reservation; this does not replace the F1 typed producer secrecy proof.
    let misplaced = { verifier with Fields = verifier.Fields.Add("reservationId", token) }
    let badBinding = { alpha with Records = misplaced :: alpha.Records.Tail }
    invalid (WorkSnapshotCodec.exportSnapshot { snapshot with Projects = [ beta; badBinding ] })
    let valid = WorkSnapshotCodec.exportSnapshot snapshot |> good
    let joined = valid.Files.Values |> Seq.map (fun bytes -> Encoding.UTF8.GetString(bytes)) |> String.concat "\n"
    Assert.That(joined, Does.Not.Contain(token))
    Assert.That(joined, Does.Not.Contain("ManualGrant"))

[<Test>]
let snapshot_codec_rejects_schema_provenance_and_noncanonical_json () =
    let valid = fixture () |> WorkSnapshotCodec.exportSnapshot |> good
    invalid (WorkSnapshotCodec.importSnapshot (editFile "manifest.json"
        (replaceBytes "\"formatVersion\": 1" "\"formatVersion\": 42") valid))
    invalid (WorkSnapshotCodec.importSnapshot (editFile "manifest.json"
        (replaceBytes "\"sourceRefs\": [" "\"unknown\": 4,\n  \"sourceRefs\": [") valid))
    invalid (WorkSnapshotCodec.importSnapshot (editFile "manifest.json"
        (replaceBytes "\"projects\": [" "\"projects\": [\"alpha\", \"alpha\"], \"ignored\": [") valid))
    invalid (WorkSnapshotCodec.importSnapshot (editFile "manifest.json"
        (replaceBytes "\"commit\": \"" "\"commit\": \"deadbeef") valid))

[<Test>]
let snapshot_codec_rejects_noncanonical_field_names_even_if_hashes_are_recalculated () =
    let snapshot = fixture ()
    let account = snapshot.AccountRecords.Head
    let leaked = { account with Fields = account.Fields.Add("manualGrantCredential", "forbidden") }
    let invalidData = { snapshot with AccountRecords = [ leaked ] }
    invalid (WorkSnapshotCodec.exportSnapshot invalidData)

[<Test>]
let snapshot_codec_safely_handles_empty_initial_database () =
    let empty = { CommittedSequence = 0L; AccountRecords = []; Projects = [] }
    let exported = WorkSnapshotCodec.exportSnapshot empty |> good
    let restored = WorkSnapshotCodec.importSnapshot exported |> good
    Assert.That(restored, Is.EqualTo(box empty))

[<Test>]
let snapshot_codec_retains_authoritative_sequence_after_deletion_and_full_compaction () =
    let snapshot = { fixture () with CommittedSequence = 19L }
    let restored = snapshot |> WorkSnapshotCodec.exportSnapshot |> good
                            |> WorkSnapshotCodec.importSnapshot |> good
    Assert.That(restored.CommittedSequence, Is.EqualTo(19L))
    let highest = restored.Projects |> List.collect _.Records |> List.maxBy _.Sequence
    Assert.That(highest.Sequence, Is.LessThan(19L))

    let compacted = { CommittedSequence = 22L; AccountRecords = []; Projects = [] }
    let restoredEmpty = compacted |> WorkSnapshotCodec.exportSnapshot |> good
                                  |> WorkSnapshotCodec.importSnapshot |> good
    Assert.That(restoredEmpty, Is.EqualTo(box compacted))

let private tamperAndRehash path (transform: byte[] -> byte[]) (bundle: WorkSnapshotBundle) =
    let oldBytes = bundle.Files.[path]
    let newBytes = transform oldBytes
    if oldBytes.AsSpan().SequenceEqual(newBytes) then
        failwithf "Test tampering was a no-op: %s" path
    let hash (bytes: byte[]) =
        SHA256.HashData(bytes) |> Convert.ToHexString |> fun x -> x.ToLowerInvariant()
    let manifest = replaceBytes (hash oldBytes) (hash newBytes) bundle.Files.["manifest.json"]
    { Files = bundle.Files.Add(path, newBytes).Add("manifest.json", manifest) }

[<Test>]
let snapshot_codec_requires_complete_logical_fences_and_known_statuses () =
    let baseSnapshot = fixture ()
    let beta = baseSnapshot.Projects.Head
    let effect =
        record "effect-beta" "EffectJournal" 1L 8L
            [ "workId", "beta-job"; "effectKind", "GitPush"; "status", "Unknown";
              "operationId", "operation-beta"; "targetRef", "refs/heads/main" ] None
    let receipt =
        record "receipt-beta" "Receipt" 1L 8L
            [ "workId", "beta-job"; "operationId", "receipt-operation"; "status", "Applied" ] None
    let complete = { baseSnapshot with
                       Projects = { beta with Records = effect :: receipt :: beta.Records }
                                  :: baseSnapshot.Projects.Tail }
    let exported = WorkSnapshotCodec.exportSnapshot complete |> good
    let imported = WorkSnapshotCodec.importSnapshot exported |> good
    Assert.That(imported.Projects |> List.sumBy (fun p -> p.Records.Length), Is.EqualTo(7))

    let alpha = baseSnapshot.Projects.Tail.Head
    let stage = alpha.Records |> List.find (fun r -> r.Kind = "WorkStage")
    let reservation = alpha.Records |> List.find (fun r -> r.Kind = "Reservation")
    let invalidAlpha newRecord =
        let records = alpha.Records |> List.map (fun r -> if r.Id = newRecord.Id then newRecord else r)
        invalid (WorkSnapshotCodec.exportSnapshot
                    { baseSnapshot with Projects = [ beta; { alpha with Records = records } ] })
    invalidAlpha { stage with Fields = stage.Fields.Remove("queueId") }
    invalidAlpha { stage with Fields = stage.Fields.Add("status", "Teleporting") }
    invalidAlpha { stage with Fields = stage.Fields.Add("status", "Claimed") }
    invalidAlpha { reservation with Fields = reservation.Fields.Remove("roleId") }
    let invalidBeta newRecord =
        let changed = { beta with Records = newRecord :: beta.Records }
        invalid (WorkSnapshotCodec.exportSnapshot { baseSnapshot with Projects = [ changed; alpha ] })
    invalidBeta { effect with Fields = effect.Fields.Remove("operationId") }
    invalidBeta { effect with Fields = effect.Fields.Add("status", "Maybe") }
    invalidBeta { receipt with Fields = receipt.Fields.Remove("workId") }

    // Rehash the canonical file to bypass checksum protection: the importer
    // must reject logical incompleteness rather than only tampered checksums.
    let expectRehashedFailure path original replacement (expected: string) =
        let modified =
            tamperAndRehash path (replaceBytes original replacement) exported
        match WorkSnapshotCodec.importSnapshot modified with
        | Error message -> Assert.That(message, Is.EqualTo(expected), sprintf "File: %s" path)
        | Ok _ -> Assert.Fail(sprintf "Rehashed incomplete %s unexpectedly restored" path)

    expectRehashedFailure "projects/alpha.json"
        "\"queueId\": \"implementation\"," "" "Missing required logical fields"
    expectRehashedFailure "projects/alpha.json"
        "\"roleId\": \"Worker\"," "" "Missing required logical fields"
    expectRehashedFailure "projects/beta.json"
        "\"operationId\": \"operation-beta\"," "" "Missing required logical fields"
    expectRehashedFailure "projects/beta.json"
        "\"operationId\": \"receipt-operation\"," "" "Missing required logical fields"
    expectRehashedFailure "projects/alpha.json"
        "\"status\": \"Available\"" "\"status\": \"Teleporting\""
        "Unsupported or invalid logical field"
    expectRehashedFailure "projects/beta.json"
        "\"status\": \"Unknown\"" "\"status\": \"Maybe\""
        "Unsupported or invalid logical field"
    expectRehashedFailure "projects/alpha.json"
        "\"reservationId\": \"reservation-alpha\""
        "\"reservationId\": \"d9f1234e-2e50-4f44-8b8e-d1d798292263\""
        "Unbound scheduled credential verifier"

[<Test>]
let snapshot_codec_rejects_invalid_git_refs_in_export_and_rehashed_import () =
    let baseline = fixture ()
    let beta = baseline.Projects.Head
    let alpha = baseline.Projects.Tail.Head
    let stage = alpha.Records |> List.find (fun r -> r.Kind = "WorkStage")
    let withRef value =
        let source = { stage.Source.Value with Ref = value }
        let updated = { stage with Source = Some source }
        let records = alpha.Records |> List.map (fun r -> if r.Id = stage.Id then updated else r)
        { baseline with Projects = [ beta; { alpha with Records = records } ] }
    for invalidRef in [ "refs/heads/a:b"; "refs/heads/a\\b"; "refs/heads/a..b";
                        "refs/heads/.hidden"; "refs/heads/a.lock"; "refs/heads/a//b";
                        "refs/heads/a?b"; "refs/heads/a" + string (char 1) ] do
        invalid (WorkSnapshotCodec.exportSnapshot (withRef invalidRef))

    let valid = WorkSnapshotCodec.exportSnapshot baseline |> good
    let rehashed =
        tamperAndRehash "projects/alpha.json"
            (replaceBytes "refs/heads/feature/example" "refs/heads/a:b") valid
    invalid (WorkSnapshotCodec.importSnapshot rehashed)

[<Test>]
let snapshot_codec_rejects_hashed_but_invalid_source_content () =
    let valid = fixture () |> WorkSnapshotCodec.exportSnapshot |> good
    let duplicateProperty =
        valid |> tamperAndRehash "projects/alpha.json"
            (replaceBytes "\"id\": \"stage-alpha\"" "\"id\": \"stage-alpha\", \"id\": \"stage-alpha\"")
    invalid (WorkSnapshotCodec.importSnapshot duplicateProperty)

    let unexpectedCredential =
        valid |> tamperAndRehash "projects/alpha.json"
            (replaceBytes "\"queueId\": \"implementation\""
                "\"queueId\": \"implementation\", \"bearerToken\": \"never-export\"")
    invalid (WorkSnapshotCodec.importSnapshot unexpectedCredential)

    let noLongerAHash =
        valid |> tamperAndRehash "projects/alpha.json"
            (replaceBytes (String.replicate 64 "f") "d9f1234e-2e50-4f44-8b8e-d1d798292263")
    invalid (WorkSnapshotCodec.importSnapshot noLongerAHash)

    let changedWhitespace =
        valid |> tamperAndRehash "account.json" (fun bytes ->
            bytes |> Encoding.UTF8.GetString
            |> fun text -> text.Replace("\n", "\r\n", StringComparison.Ordinal)
            |> Encoding.UTF8.GetBytes)
    invalid (WorkSnapshotCodec.importSnapshot changedWhitespace)

[<Test>]
let snapshot_codec_two_project_export_restore_initial_performance () =
    let stages project start count =
        [ for index in start .. start + count - 1 do
              yield record (sprintf "%s-stage-%04d" project index) "WorkStage" 1L (int64 (index + 1))
                  [ "workId", sprintf "%s-work-%04d" project index
                    "status", "Available"
                    "queueId", "implementation"
                    "priority", "1"
                    "generation", "1"
                    "availableSequence", string (index + 1) ] None ]
    let large =
        { CommittedSequence = 2000L
          AccountRecords = []
          Projects =
            [ { ProjectId = "alpha"; Records = stages "alpha" 0 1000 }
              { ProjectId = "beta"; Records = stages "beta" 1000 1000 } ] }
    let exportTime = Diagnostics.Stopwatch.StartNew()
    let bytes = WorkSnapshotCodec.exportSnapshot large |> good
    exportTime.Stop()
    let restoreTime = Diagnostics.Stopwatch.StartNew()
    let restored = WorkSnapshotCodec.importSnapshot bytes |> good
    restoreTime.Stop()
    Assert.That(restored.Projects |> List.sumBy (fun x -> x.Records.Length), Is.EqualTo(2000))
    let totalBytes = bytes.Files |> Map.toSeq |> Seq.sumBy (fun (_, b) -> b.Length)
    TestContext.Progress.WriteLine(sprintf
        "F0 2-project 2000-record snapshot: %d bytes, serialize %.1fms, parse/verify %.1fms"
        totalBytes exportTime.Elapsed.TotalMilliseconds restoreTime.Elapsed.TotalMilliseconds)

[<Test>]
let snapshot_codec_reports_bounded_typed_corruption_reasons () =
    let valid = fixture () |> WorkSnapshotCodec.exportSnapshot |> good
    let check (expected: string) (result: Result<WorkSnapshot, string>) =
        match result with
        | Error why -> Assert.That(why, Is.EqualTo(expected))
        | Ok _ -> Assert.Fail(sprintf "Expected failure: %s" expected)

    let corrupt = editFile "projects/alpha.json" (replaceBytes "\"status\": \"Available\"" "\"status\": \"Blocked\"") valid
    check "Backup content checksum mismatch" (WorkSnapshotCodec.importSnapshot corrupt)

    let unsupported = editFile "manifest.json" (replaceBytes "\"formatVersion\": 1" "\"formatVersion\": 2") valid
    check "Unsupported Work snapshot format" (WorkSnapshotCodec.importSnapshot unsupported)

    let torn = tamperAndRehash "projects/alpha.json" (replaceBytes "\"committedSequence\": 8" "\"committedSequence\": 9") valid
    check "Torn snapshot sequence" (WorkSnapshotCodec.importSnapshot torn)
