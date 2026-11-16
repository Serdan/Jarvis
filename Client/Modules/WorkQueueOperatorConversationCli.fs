module Client.WorkQueueOperatorConversationCli

open System
open System.IO
open System.Text.Json
open Client.LedgerStore
open Client.OrchestrationApi
open Client.WorkQueueOperatorConversations

// Operator-only local CLI. Uses the same approved trust binding as Jarvis;
// actor labels identify humans/agents for audit, not authorization.
module WorkQueueOperatorConversationCli =
    let private pretty value =
        JsonSerializer.Serialize(value, JsonSerializerOptions(WriteIndented = true))

    let private help () =
        eprintfn "Usage: JarvisClient conversation ROOT Task|Role SUBJECT list"
        eprintfn "       JarvisClient conversation ROOT Task|Role SUBJECT post ACTOR OP_UUID VERSION MESSAGE_ID BODY"
        eprintfn "       JarvisClient conversation ROOT Task|Role SUBJECT claim HANDLER OP_UUID VERSION MESSAGE_ID"
        eprintfn "       JarvisClient conversation ROOT Task|Role SUBJECT handle HANDLER OP_UUID VERSION MESSAGE_ID GENERATION REPLY_ID REPLY"
        eprintfn "       JarvisClient conversation ROOT Task|Role SUBJECT release ACTOR OP_UUID VERSION MESSAGE_ID GENERATION REASON"
        eprintfn "       JarvisClient conversation ROOT Task|Role SUBJECT raw"
        eprintfn "       JarvisClient conversation ROOT Task|Role SUBJECT preview-repair REPLACEMENT_JSON_FILE"
        eprintfn "       JarvisClient conversation ROOT Task|Role SUBJECT repair ACTOR OP_UUID OLD_BLOB_SHA_OR_none REASON REPLACEMENT_JSON_FILE"
        eprintfn "       JarvisClient conversation ROOT inspect OP_UUID"
        eprintfn "ROOT must already have an approved Git ledger trust binding. No Scheduler is started."
        2

    let private parseUuid (value: string) =
        match Guid.TryParse value with
        | true, guid when guid <> Guid.Empty -> Some guid
        | _ -> None

    let private parseVersion (value: string) =
        match Int32.TryParse value with
        | true, version when version >= 0 -> Some version
        | _ -> None

    let private printFailure operationId error =
        match error with
        | OutcomeUnknown message ->
            eprintfn "%s" (pretty {| status = "Unknown"
                                     operationId = operationId
                                     detail = message
                                     next = "Inspect this exact operation UUID; then retry identical command if appropriate." |})
            4
        | StaleEntityVersion message ->
            eprintfn "%s" (pretty {| status = "Conflict"
                                     operationId = operationId
                                     detail = message
                                     next = "List thread again before changing state; never reuse this UUID for new content." |})
            3
        | other ->
            eprintfn "%s" (pretty {| status = "Error"
                                     operationId = operationId
                                     detail = sprintf "%A" other
                                     next = "Inspect thread and Git ledger; resolve errors explicitly." |})
            2

    let private mutation operationId result =
        match result with
        | Ok receipt ->
            printfn "%s" (pretty
                {| status = "Applied"
                   operationId = receipt.OperationId
                   observedGitHead = receipt.ObservedGitHead
                   ledgerSequence = receipt.LedgerSequence
                   alreadyApplied = receipt.AlreadyApplied |})
            0
        | Error error -> printFailure operationId error

    let private runWithBinding binding argv =
        match argv with
        | [ "inspect"; uuid ] ->
            match parseUuid uuid with
            | None -> help ()
            | Some operationId ->
                match WorkQueueOperatorConversations.inspectOperation binding operationId with
                | Error error -> printFailure uuid error
                | Ok observed ->
                    match observed.Operation with
                    | Some operation ->
                        printfn "%s" (pretty
                            {| status = "Applied"
                               verifiedGitHead = observed.VerifiedGitHead
                               operationId = operation.OperationId
                               action = operation.Type
                               actor = operation.Actor
                               sequence = operation.Sequence
                               paths = operation.Paths
                               requestHash = operation.RequestHash
                               originalResultHash = operation.OriginalResultHash |})
                    | None ->
                        printfn "%s" (pretty
                            {| status = "NotFoundAtVerifiedHead"
                               verifiedGitHead = observed.VerifiedGitHead
                               operationId = uuid
                               warning = "Absence at this head does not prove an uncertain push cannot arrive." |})
                    0
        | kind :: subject :: command when WorkQueueOperatorConversations.validSubject kind subject ->
            match command with
            | [ "raw" ] ->
                match WorkQueueOperatorConversations.inspectRaw binding kind subject with
                | Ok (gitHead, blob, raw) ->
                    printfn "%s" (pretty
                        {| gitHead = gitHead; blobOid = blob |> Option.defaultValue "none"
                           content = raw |> Option.defaultValue "" |})
                    0
                | Error error -> printFailure "" error
            | [ "preview-repair"; replacementPath ] ->
                match WorkQueueOperatorConversations.inspectRaw binding kind subject with
                | Ok (gitHead, blob, raw) ->
                    let replacement = File.ReadAllText replacementPath
                    printfn "%s" (pretty
                        {| status = "PreviewOnly"; gitHead = gitHead
                           backupBlobOid = blob |> Option.defaultValue "none"
                           before = raw |> Option.defaultValue ""
                           after = replacement
                           note = "Inspect before/after content; apply requires exact backup blob SHA." |})
                    0
                | Error error -> printFailure "" error
            | [ "repair"; actor; op; expectedOid; reason; replacementPath ] ->
                match parseUuid op with
                | None -> help ()
                | Some uuid ->
                    let replacement = File.ReadAllText replacementPath
                    WorkQueueOperatorConversations.repair binding actor uuid kind subject
                        expectedOid reason replacement |> mutation op
            | [ "list" ] ->
                match WorkQueueOperatorConversations.read binding kind subject with
                | Ok snapshot ->
                    printfn "%s" (pretty
                        {| status = "ReadOnly"
                           gitHead = snapshot.GitHead
                           blobOid = snapshot.BlobOid |> Option.defaultValue ""
                           subjectKind = snapshot.Thread.SubjectKind
                           subjectId = snapshot.Thread.SubjectId
                           version = snapshot.Thread.Version
                           messages = snapshot.Thread.Messages
                           audit = snapshot.Thread.Audit |})
                    0
                | Error error -> printFailure "" error
            | [ "post"; actor; op; version; messageId; body ] ->
                match parseUuid op, parseVersion version with
                | Some uuid, Some v ->
                    WorkQueueOperatorConversations.post binding actor uuid v kind subject messageId body
                    |> mutation op
                | _ -> help ()
            | [ "claim"; actor; op; version; messageId ] ->
                match parseUuid op, parseVersion version with
                | Some uuid, Some v ->
                    WorkQueueOperatorConversations.claim binding actor uuid v kind subject messageId
                    |> mutation op
                | _ -> help ()
            | [ "handle"; actor; op; version; messageId; generation; replyId; reply ] ->
                match parseUuid op, parseVersion version, parseVersion generation with
                | Some uuid, Some v, Some g when g > 0 ->
                    WorkQueueOperatorConversations.handle binding actor uuid v
                        kind subject messageId g replyId reply
                    |> mutation op
                | _ -> help ()
            | [ "release"; actor; op; version; messageId; generation; reason ] ->
                match parseUuid op, parseVersion version, parseVersion generation with
                | Some uuid, Some v, Some g when g > 0 ->
                    WorkQueueOperatorConversations.release binding actor uuid v
                        kind subject messageId g reason
                    |> mutation op
                | _ -> help ()
            | _ -> help ()
        | _ -> help ()

    let run args =
        match args with
        | "conversation" :: root :: command ->
            try
                let projectRoot = Path.GetFullPath root
                // This performs trusted-head verification and loads only an
                // explicitly enabled, locally approved project Git binding.
                match LedgerBindingConfig.loadAtRootWithSnapshotUsing
                          LedgerStore.fetch projectRoot with
                | Ok (binding, _) -> runWithBinding binding command
                | Error error ->
                    eprintfn "Approved ledger binding unavailable: %A" error
                    2
            with ex ->
                eprintfn "Cannot access operator conversation: %s" ex.Message
                2
        | _ -> help ()
