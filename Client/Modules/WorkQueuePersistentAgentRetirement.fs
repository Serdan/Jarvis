module Client.WorkQueuePersistentAgentRetirement

open System
open System.Security.Cryptography
open System.Text
open Client.LedgerStore
open Client.WorkQueueGitLedger
open Client.WorkQueueRoleAuthority
open Client.WorkQueueToolFacade

// Read-only pre-cutover evidence boundary. No Agent ID, registration, work
// assignment, mutable account token, provider session or scheduler port exists
// in this API. All legacy paths are retained as Git OIDs, not copied or
// converted into a new schedulable principal.
type RetirementEvidenceEntry =
    { Path: string
      BlobOid: string
      Family: string
      HistoricalOnly: bool }

type WorkFirstRetirementPreflight =
    { ProjectId: string
      LedgerId: string
      Epoch: int
      VerifiedHead: string
      HistoricalManifestSha256: string
      Entries: RetirementEvidenceEntry list
      LegacyAgentRecords: int
      WorkProjection: Result<WorkToolSnapshot, string>
      ReadOnly: bool
      CutoverAuthorized: bool
      UnattestedRequirements: string list }

module WorkQueuePersistentAgentRetirement =
    let private starts (prefix: string) (path: string) =
        path.StartsWith(prefix, StringComparison.Ordinal)

    let private family path =
        if starts "agents/" path then
            if path.EndsWith(".challenge-subscription.json", StringComparison.Ordinal) then
                "LegacyAgentChallengeSubscription", true
            else "LegacyAgentIdentity", true
        elif starts "tasks/" path then
            if path.Contains("/reviews/", StringComparison.Ordinal) then "LegacyReview", true
            elif path.Contains("/integrations/", StringComparison.Ordinal) then "LegacyIntegration", true
            elif path.Contains("/assignments/", StringComparison.Ordinal) then "LegacyAssignment", true
            elif path.Contains("/attempts/", StringComparison.Ordinal) then "LegacyAttempt", true
            else "LegacyTaskAndHistory", true
        elif starts "notifications/" path then "LegacyNotification", true
        elif starts "challenges/" path then "LegacyChallenge", true
        elif starts "challenge-waits/" path then "LegacyChallengeWaitIndex", true
        elif starts "records/roles/" path then "LegacyRole", true
        elif starts "records/agent-instructions/" path then "LegacyAgentInstructions", true
        elif starts "records/notification-preferences/" path then "LegacyNotificationPreferences", true
        elif starts "records/notification-suppressions/" path then "LegacyNotificationSuppressions", true
        elif starts "records/response-alerts/" path then "LegacyResponseAlerts", true
        elif starts "records/work-queue/" path then "NativeWork", false
        elif starts "ops/" path then "ImmutableOperationReceipt", true
        elif starts "records/" path then "OtherPreservedRecord", false
        else "LedgerMetadataOrUnclassified", false

    let private digest (entries: RetirementEvidenceEntry list) =
        // Unambiguous length-framed serialization of the complete pinned tree.
        // Neither paths nor payloads may be silently omitted or rewritten.
        let framed =
            entries
            |> List.map (fun e ->
                string e.Path.Length + ":" + e.Path
                + string e.BlobOid.Length + ":" + e.BlobOid + ";")
            |> String.concat ""
        SHA256.HashData(Encoding.UTF8.GetBytes framed)
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    /// A fresh, verified Git-head read for an operator's retirement inventory.
    /// It never calls the Agent inbox, RegisterAgent, a legacy writer or a Work
    /// transition. Production authorization intentionally cannot be obtained
    /// through this report, even when the Work projection is healthy.
    let inspect (binding: LedgerBinding) : Result<WorkFirstRetirementPreflight, LedgerFailure> =
        LedgerStore.fetch binding
        |> Result.bind (fun snapshot ->
            LedgerStore.listPinnedLedgerBlobsAt binding snapshot
            |> Result.map (fun original ->
                let entries =
                    original
                    |> List.map (fun (path, oid) ->
                        let kind, historical = family path
                        { Path = path; BlobOid = oid
                          Family = kind; HistoricalOnly = historical })
                let projection =
                    match WorkQueueGitLedger.loadAt binding snapshot with
                    | Error error ->
                        Error(sprintf "UNKNOWN: WQ02 Work snapshot is unavailable at original Git head: %A" error)
                    | Ok project ->
                        let role =
                            WorkQueueRoleAuthority.loadAt binding snapshot
                            |> Result.mapError (sprintf "%A")
                        let view =
                            WorkQueueToolFacade.inspectAt
                                (binding.ProjectId.ToString("D")) snapshot.Commit project role
                        if not view.ReadOnly || view.VerifiedGitHead <> snapshot.Commit
                           || view.Work |> List.exists (fun row -> row.ClaimEnabled) then
                            Error "UNKNOWN: Work projection must be same-head and strictly read-only."
                        else Ok view
                let gaps =
                    [ "Fleet-wide legacy writer/Manager fence and original high-water have not been authenticated by this read."
                      "Account/project WQ10 epoch, all participants and uncertain source operations have not been joined by this read."
                      "Real private provider bootstrap, launch-scoped STOP and terminal journal are not authenticated by this read."
                      "Explicit operator cutover authorization is not an input; no live Agent retirement or Scheduler activation is permitted." ]
                    @ (match projection with
                       | Ok view when view.Coverage = "Verified" &&
                                      not (view.RoleAuthority.StartsWith("UNKNOWN", StringComparison.Ordinal)) -> []
                       | Ok view ->
                           [ "Pinned Work projection is incomplete: " + view.Coverage + "; " + view.RoleAuthority ]
                       | Error reason -> [ reason ])
                { ProjectId = binding.ProjectId.ToString("D")
                  LedgerId = binding.LedgerId.ToString("D")
                  Epoch = binding.Epoch
                  VerifiedHead = snapshot.Commit
                  HistoricalManifestSha256 = digest entries
                  Entries = entries
                  LegacyAgentRecords =
                    entries |> List.filter (fun e -> e.Family = "LegacyAgentIdentity") |> List.length
                  WorkProjection = projection
                  ReadOnly = true
                  CutoverAuthorized = false
                  UnattestedRequirements = gaps }))
