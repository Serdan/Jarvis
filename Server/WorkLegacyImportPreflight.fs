namespace Jarvis.WorkQueues

open System
open System.Collections.Generic
open System.Text.RegularExpressions

// C0: immutable, read-only mapping of independently inspected legacy Git ledgers.
// The caller must independently pin and verify each Git object, its ancestry and
// receipt contents. No value returned here is a migration/cutover capability.
// In particular, true flags supplied in an inspection do not authorize writers.
type WorkLegacyImportHead =
    { PinnedCommit: string
      ObservedCommit: string
      AncestryVerified: bool
      HistoryComplete: bool
      SourceHighWater: int64
      ObservedHighWater: int64 }

type WorkLegacyImportOperation =
    { OperationId: Guid
      Sequence: int64
      RequestSha256: string
      ResultSha256: string
      Kind: string }

type WorkLegacyImportOrigin =
    { WorkId: string
      StageGeneration: int
      SubjectId: string
      SourceHead: string
      SourceHighWater: int64
      SourceAncestryVerified: bool
      RepositoryId: string
      SourceRef: string
      SourceCommit: string
      RoleId: string
      RoleVersion: int
      GraphVersion: int
      ContextDigest: string }

type WorkLegacyImportReview =
    { WorkId: string
      SubjectId: string
      ReceiptId: string
      FrozenCommit: string
      DecisionSequence: int64
      Accepted: bool }

type WorkLegacyImportIntegration =
    { WorkId: string
      ReviewWorkId: string
      ReceiptId: string
      SourceCommit: string
      TargetRef: string
      IntegratedCommit: string
      SourceAncestryVerified: bool }

// Immutable WQ02 project terminal evidence as verified against the original
// Git operation and blob, not merely an account claim that capacity was freed.
type WorkLegacyImportTerminal =
    { LaunchId: string
      AccountTokenId: string
      ReceiptId: string
      OperationId: Guid
      TerminalHead: string
      TerminalBlobOid: string
      AncestryVerified: bool
      LaunchGeneration: int64 }

type WorkLegacyImportValidation =
    { EvidenceRef: string
      SourceCommit: string
      EvidenceVerified: bool }

type WorkLegacyImportProject =
    { ProjectId: Guid
      LedgerId: Guid
      LedgerEpoch: int
      Head: WorkLegacyImportHead
      Queue: Project
      Graph: WorkCompositionGraph
      Origins: WorkLegacyImportOrigin list
      Reviews: WorkLegacyImportReview list
      Integrations: WorkLegacyImportIntegration list
      Terminals: WorkLegacyImportTerminal list
      Validations: WorkLegacyImportValidation list
      Operations: WorkLegacyImportOperation list }

type WorkLegacyImportAccountToken =
    { LaunchId: string
      ProjectId: Guid
      AccountTokenId: string
      Released: bool
      TerminalReceipt: string option }

type WorkLegacyImportAccount =
    { AccountId: Guid
      LedgerId: Guid
      LedgerEpoch: int
      Head: WorkLegacyImportHead
      MaxConcurrent: int
      Tokens: WorkLegacyImportAccountToken list
      Operations: WorkLegacyImportOperation list }

type WorkLegacyImportInspection =
    { Account: WorkLegacyImportAccount
      Projects: WorkLegacyImportProject list }

[<RequireQualifiedAccess>]
type WorkLegacyImportGap =
    | InvalidIdentity of string
    | HeadMismatch of string
    | IncompleteHistory of string
    | DuplicateOperation of string
    | InvalidReceipt of string
    | InvalidGraph of string
    | InvalidStage of string
    | MissingOrigin of string
    | MissingReviewDecision of string
    | InvalidReviewDecision of string
    | MissingIntegration of string
    | InvalidIntegration of string
    | MissingAcceptanceEvidence of string
    | AccountCapacityConflict of string
    | UnresolvedExternalEffect of string

// An evidence index, not a migration plan or writer authority. It deliberately
// has no writable connection, activation decision, bearer or approval flag.
type WorkLegacyImportEvidenceIndex =
    { AccountId: Guid
      AccountHead: string
      Projects: (Guid * Guid * string * int * int) list
      Stages: (Guid * string * int * int) list
      FrozenReviews: (Guid * string * string * bool) list
      Integrations: (Guid * string * string * string) list
      OperationIds: (Guid * Guid) list
      StoppedLaunches: (Guid * string * int) list
      ReservedCapacity: int }

module WorkLegacyImportPreflight =
    let private oid = Regex("^[0-9a-f]{40}([0-9a-f]{24})?$", RegexOptions.Compiled)
    let private hash = Regex("^[0-9a-f]{64}$", RegexOptions.Compiled)
    let private valid (pattern: Regex) (value: string) =
        not (isNull value) && pattern.IsMatch(value)
    let private text (value: string) = not (String.IsNullOrWhiteSpace value)
    let private canonicalId (value: Guid) = value <> Guid.Empty
    let private identifier (value: Guid) = value.ToString("D")

    let private inspectHead (gaps: ResizeArray<WorkLegacyImportGap>) label (head: WorkLegacyImportHead) =
        if not (valid oid head.PinnedCommit)
           || not (valid oid head.ObservedCommit)
           || head.PinnedCommit <> head.ObservedCommit
           || not head.AncestryVerified then
            gaps.Add(WorkLegacyImportGap.HeadMismatch label)
        if not head.HistoryComplete || head.SourceHighWater < 0L
           || head.ObservedHighWater <> head.SourceHighWater then
            gaps.Add(WorkLegacyImportGap.IncompleteHistory label)

    let private inspectOperations (gaps: ResizeArray<WorkLegacyImportGap>) label highWater
                                  (operations: WorkLegacyImportOperation list) =
        let sorted = operations |> List.sortBy (fun op -> op.Sequence)
        let mutable previous = 0L
        let seen = HashSet<Guid>()
        for operation in sorted do
            if operation.OperationId = Guid.Empty
               || not (seen.Add operation.OperationId) then
                gaps.Add(WorkLegacyImportGap.DuplicateOperation(label + ": " + identifier operation.OperationId))
            if operation.Sequence <> previous + 1L then
                gaps.Add(WorkLegacyImportGap.IncompleteHistory(label + ": operation sequence"))
            previous <- operation.Sequence
            if not (valid hash operation.RequestSha256)
               || not (valid hash operation.ResultSha256)
               || not (text operation.Kind) then
                gaps.Add(WorkLegacyImportGap.InvalidReceipt(label + ": " + identifier operation.OperationId))
        if previous <> highWater then
            gaps.Add(WorkLegacyImportGap.IncompleteHistory(label + ": high-water/receipt mismatch"))

    let inspect (input: WorkLegacyImportInspection) : Result<WorkLegacyImportEvidenceIndex, WorkLegacyImportGap list> =
        let gaps = ResizeArray<WorkLegacyImportGap>()
        let account = input.Account
        if not (canonicalId account.AccountId) || not (canonicalId account.LedgerId)
           || account.LedgerEpoch < 0 then
            gaps.Add(WorkLegacyImportGap.InvalidIdentity "account binding")
        inspectHead gaps "account" account.Head
        inspectOperations gaps "account" account.Head.SourceHighWater account.Operations

        let uniqueProjects = HashSet<Guid>()
        let uniqueProjectLedgers = HashSet<Guid>()
        let launchKeys = Dictionary<string, Guid>(StringComparer.Ordinal)
        let tokenKeys = HashSet<string>(StringComparer.Ordinal)
        for project in input.Projects do
            let label = identifier project.ProjectId
            if not (canonicalId project.ProjectId)
               || not (canonicalId project.LedgerId)
               || project.LedgerEpoch < 0
               || not (uniqueProjects.Add project.ProjectId)
               || not (uniqueProjectLedgers.Add project.LedgerId)
               || project.LedgerId = account.LedgerId then
                gaps.Add(WorkLegacyImportGap.InvalidIdentity("ambiguous project/ledger: " + label))
            inspectHead gaps ("project " + label) project.Head
            inspectOperations gaps ("project " + label) project.Head.SourceHighWater project.Operations
            if not project.Queue.QueueCovered || not project.Queue.GraphCovered
               || not project.Queue.MessageCovered || not project.Queue.SourceCovered then
                gaps.Add(WorkLegacyImportGap.IncompleteHistory(label + ": WQ coverage"))
            if WorkComposition.validate project.Graph |> Result.isError then
                gaps.Add(WorkLegacyImportGap.InvalidGraph label)
            if project.Queue.Version < 0 || project.Queue.GraphVersion < 0
               || project.Graph.Version < 0 then
                gaps.Add(WorkLegacyImportGap.InvalidGraph(label + ": versions"))
            if project.Queue.Migration = MigrationEpoch.Preparing
               || project.Queue.Migration = MigrationEpoch.Validated then
                gaps.Add(WorkLegacyImportGap.InvalidStage(label + ": partial legacy migration epoch"))

            let origins = Dictionary<string, WorkLegacyImportOrigin>(StringComparer.Ordinal)
            for origin in project.Origins do
                if not (origins.TryAdd(origin.WorkId, origin)) then
                    gaps.Add(WorkLegacyImportGap.InvalidStage(label + ": duplicate origin " + origin.WorkId))
                if not (valid oid origin.SourceHead)
                   || not origin.SourceAncestryVerified
                   || origin.SourceHighWater < 0L
                   || origin.SourceHighWater > project.Head.SourceHighWater
                   || not (text origin.RepositoryId)
                   || not (text origin.SourceRef)
                   || not (valid oid origin.SourceCommit)
                   || not (valid hash origin.ContextDigest)
                   || origin.RoleVersion <= 0 || not (text origin.RoleId)
                   || origin.GraphVersion < 0 || origin.GraphVersion > project.Graph.Version
                   || origin.StageGeneration <= 0 then
                    gaps.Add(WorkLegacyImportGap.MissingOrigin(label + ": unverified origin " + origin.WorkId))

            let reviews = Dictionary<string, WorkLegacyImportReview>(StringComparer.Ordinal)
            for review in project.Reviews do
                if not (reviews.TryAdd(review.WorkId, review))
                   || not (text review.ReceiptId)
                   || not (valid oid review.FrozenCommit)
                   || review.DecisionSequence <= 0L then
                    gaps.Add(WorkLegacyImportGap.InvalidReviewDecision(label + ": " + review.WorkId))

            let integrations = Dictionary<string, WorkLegacyImportIntegration>(StringComparer.Ordinal)
            for integration in project.Integrations do
                if not (integrations.TryAdd(integration.WorkId, integration))
                   || not (text integration.ReceiptId)
                   || not (valid oid integration.SourceCommit)
                   || not (valid oid integration.IntegratedCommit)
                   || not (text integration.TargetRef)
                   || not integration.SourceAncestryVerified then
                    gaps.Add(WorkLegacyImportGap.InvalidIntegration(label + ": " + integration.WorkId))

            let acceptedEvidence =
                (project.Reviews
                 |> List.filter (fun review -> review.Accepted)
                 |> List.map (fun review -> review.ReceiptId))
                @ (project.Integrations |> List.map (fun integration -> integration.ReceiptId))
                @ (project.Validations
                   |> List.filter (fun proof -> proof.EvidenceVerified && valid oid proof.SourceCommit)
                   |> List.map (fun proof -> proof.EvidenceRef))
                |> Set.ofList
            for validation in project.Validations do
                if not (text validation.EvidenceRef)
                   || not (valid oid validation.SourceCommit)
                   || not validation.EvidenceVerified then
                    gaps.Add(WorkLegacyImportGap.MissingAcceptanceEvidence(label + ": invalid independent validation"))
            for KeyValue(_, unit) in project.Graph.Units do
                for KeyValue(_, acceptance) in unit.Acceptances do
                    if not (acceptedEvidence.Contains acceptance.EvidenceRef) then
                        gaps.Add(WorkLegacyImportGap.MissingAcceptanceEvidence(label + ": " + unit.Id))

            for KeyValue(workId, stage) in project.Queue.Works do
                let prefix = label + "/" + workId
                if workId <> stage.Id
                   || stage.ProjectId <> label
                   || stage.StageGeneration <= 0
                   || stage.ClaimGeneration < 0
                   || not (text stage.SubjectId)
                   || not (text stage.QueueId) then
                    gaps.Add(WorkLegacyImportGap.InvalidStage prefix)
                let requiresOrigin =
                    match stage.Kind with
                    | WorkKind.Implementation | WorkKind.FrozenReview | WorkKind.Integration -> true
                    | _ -> false
                match origins.TryGetValue workId with
                | true, origin ->
                    if origin.StageGeneration <> stage.StageGeneration
                       || origin.SubjectId <> stage.SubjectId
                       || not (project.Queue.Subscriptions.ContainsKey origin.RoleId)
                       || not (project.Queue.Subscriptions.[origin.RoleId].Contains stage.QueueId) then
                        gaps.Add(WorkLegacyImportGap.MissingOrigin(prefix + ": stage/role/source mismatch"))
                | false, _ when requiresOrigin ->
                    gaps.Add(WorkLegacyImportGap.MissingOrigin prefix)
                | _ -> ()
                match stage.State with
                | WorkState.Claimed launchId ->
                    match project.Queue.Launches.TryFind launchId with
                    | Some launch when launch.PrimaryWorkId = Some workId
                                       && launch.EverClaimed
                                       && stage.ClaimGeneration > 0 -> ()
                    | _ -> gaps.Add(WorkLegacyImportGap.InvalidStage(prefix + ": claim/generation"))
                | _ -> ()
                match stage.Push with
                | Some push when push.Outcome = PushOutcome.Unknown ->
                    gaps.Add(WorkLegacyImportGap.UnresolvedExternalEffect prefix)
                | _ -> ()
                let matchingFrozenSource workId commit =
                    match origins.TryGetValue workId with
                    | true, source -> source.SourceCommit = commit
                    | _ -> false
                match stage.Kind, stage.Domain with
                | WorkKind.FrozenReview, DomainState.FinalizedPendingTerminal receipt ->
                    match reviews.TryGetValue workId with
                    | true, decision when decision.ReceiptId = receipt
                                          && decision.SubjectId = stage.SubjectId
                                          && decision.Accepted
                                          && matchingFrozenSource workId decision.FrozenCommit -> ()
                    | _ -> gaps.Add(WorkLegacyImportGap.MissingReviewDecision prefix)
                | WorkKind.FrozenReview, _ when stage.State = WorkState.Completed ->
                    gaps.Add(WorkLegacyImportGap.MissingReviewDecision prefix)
                | WorkKind.Integration, DomainState.Integrated receipt ->
                    match integrations.TryGetValue workId with
                    | true, integration when integration.ReceiptId = receipt
                                            && matchingFrozenSource workId integration.SourceCommit
                                            && project.Reviews |> List.exists (fun review ->
                                                review.WorkId = integration.ReviewWorkId
                                                && review.Accepted
                                                && review.SubjectId = stage.SubjectId
                                                && review.FrozenCommit = integration.SourceCommit) -> ()
                    | _ -> gaps.Add(WorkLegacyImportGap.MissingIntegration prefix)
                | WorkKind.Integration, _ when stage.State = WorkState.Completed ->
                    gaps.Add(WorkLegacyImportGap.MissingIntegration prefix)
                | _ -> ()
                if stage.IntegrationAuthorized && stage.Kind = WorkKind.Integration then
                    match origins.TryGetValue workId with
                    | true, origin when project.Reviews |> List.exists (fun review ->
                        review.Accepted && review.SubjectId = stage.SubjectId
                        && review.FrozenCommit = origin.SourceCommit) -> ()
                    | _ -> gaps.Add(WorkLegacyImportGap.MissingReviewDecision(prefix + ": integration authorization"))

            for origin in project.Origins do
                if not (project.Queue.Works.ContainsKey origin.WorkId) then
                    gaps.Add(WorkLegacyImportGap.MissingOrigin(label + ": orphan " + origin.WorkId))
            for review in project.Reviews do
                match project.Queue.Works.TryFind review.WorkId with
                | Some stage when stage.Kind = WorkKind.FrozenReview
                                  && stage.SubjectId = review.SubjectId -> ()
                | _ -> gaps.Add(WorkLegacyImportGap.InvalidReviewDecision(label + ": orphan " + review.WorkId))
            for integration in project.Integrations do
                match project.Queue.Works.TryFind integration.WorkId with
                | Some stage when stage.Kind = WorkKind.Integration -> ()
                | _ -> gaps.Add(WorkLegacyImportGap.InvalidIntegration(label + ": orphan " + integration.WorkId))

            let knownTerminals = HashSet<string>(StringComparer.Ordinal)
            for terminal in project.Terminals do
                if not (knownTerminals.Add terminal.LaunchId)
                   || not (text terminal.ReceiptId)
                   || not (valid oid terminal.TerminalHead)
                   || not (valid oid terminal.TerminalBlobOid)
                   || not terminal.AncestryVerified
                   || terminal.LaunchGeneration < 0L
                   || not (project.Operations |> List.exists (fun op -> op.OperationId = terminal.OperationId)) then
                    gaps.Add(WorkLegacyImportGap.AccountCapacityConflict(label + ": unverified terminal " + terminal.LaunchId))
                match project.Queue.Launches.TryFind terminal.LaunchId with
                | Some launch when launch.AccountTokenId = terminal.AccountTokenId
                                   && (launch.Lease |> Option.forall (fun lease ->
                                       int64 lease.Generation = terminal.LaunchGeneration)) -> ()
                | _ ->
                    gaps.Add(WorkLegacyImportGap.AccountCapacityConflict(label + ": orphan terminal " + terminal.LaunchId))

            for KeyValue(launchId, launch) in project.Queue.Launches do
                if launchId <> launch.Id || launch.ProjectId <> label
                   || not (text launch.AccountTokenId) || launch.RoleVersion <= 0
                   || launch.Version < 0 || launch.DispatchCount < 0
                   || not (launchKeys.TryAdd(launchId, project.ProjectId)) then
                    gaps.Add(WorkLegacyImportGap.InvalidStage(label + ": launch/identity " + launchId))
                match launch.PrimaryWorkId with
                | Some primary when not (project.Queue.Works.ContainsKey primary)
                                    || not launch.EverClaimed ->
                    gaps.Add(WorkLegacyImportGap.InvalidStage(label + ": dangling primary " + launchId))
                | _ -> ()
                match launch.Lease with
                | Some lease when lease.Generation < 0 ->
                    gaps.Add(WorkLegacyImportGap.InvalidStage(label + ": lease generation " + launchId))
                | _ -> ()

        let mutable occupied = 0
        let usedLaunches = HashSet<string>(StringComparer.Ordinal)
        for token in account.Tokens do
            let label = token.LaunchId
            if not (text label) || not (tokenKeys.Add token.AccountTokenId)
               || not (usedLaunches.Add label) then
                gaps.Add(WorkLegacyImportGap.AccountCapacityConflict("duplicate token " + label))
            if token.Released then
                if token.TerminalReceipt |> Option.exists text |> not then
                    gaps.Add(WorkLegacyImportGap.AccountCapacityConflict("unproved release " + label))
            else
                occupied <- occupied + 1
                if token.TerminalReceipt.IsSome then
                    gaps.Add(WorkLegacyImportGap.AccountCapacityConflict("unreleased terminal " + label))
            match input.Projects |> List.tryFind (fun p -> p.ProjectId = token.ProjectId) with
            | Some project ->
                match project.Queue.Launches.TryFind label with
                | Some launch when launch.AccountTokenId = token.AccountTokenId
                                   && (not token.Released
                                       || (match launch.State with
                                           | LaunchState.TerminalPendingRelease _ -> true
                                           | _ -> false)) -> ()
                | _ -> gaps.Add(WorkLegacyImportGap.AccountCapacityConflict("unmatched token " + label))
                if token.Released && (project.Queue.Works |> Map.exists (fun _ stage ->
                    stage.State = WorkState.Claimed token.LaunchId)) then
                    gaps.Add(WorkLegacyImportGap.AccountCapacityConflict("released live claim " + label))
                if token.Released then
                    match token.TerminalReceipt with
                    | Some receipt when project.Terminals |> List.exists (fun terminal ->
                        terminal.LaunchId = token.LaunchId
                        && terminal.AccountTokenId = token.AccountTokenId
                        && terminal.ReceiptId = receipt
                        && terminal.AncestryVerified
                        && project.Operations |> List.exists (fun op -> op.OperationId = terminal.OperationId)) -> ()
                    | _ ->
                        gaps.Add(WorkLegacyImportGap.AccountCapacityConflict("unverified project terminal release " + label))
            | None -> gaps.Add(WorkLegacyImportGap.AccountCapacityConflict("unknown project token " + label))
        for KeyValue(launchId, _) in launchKeys do
            if not (usedLaunches.Contains launchId) then
                gaps.Add(WorkLegacyImportGap.AccountCapacityConflict("missing token " + launchId))
        if account.MaxConcurrent < 0 || occupied > account.MaxConcurrent then
            gaps.Add(WorkLegacyImportGap.AccountCapacityConflict "global capacity")
        if input.Projects.IsEmpty then
            gaps.Add(WorkLegacyImportGap.InvalidIdentity "no project ledgers")

        if gaps.Count > 0 then Error(List.ofSeq gaps)
        else
            Ok
                { AccountId = account.AccountId
                  AccountHead = account.Head.PinnedCommit
                  Projects =
                      input.Projects
                      |> List.sortBy (fun p -> p.ProjectId)
                      |> List.map (fun p ->
                          p.ProjectId, p.LedgerId, p.Head.PinnedCommit, p.Queue.Version, p.Graph.Version)
                  Stages =
                      input.Projects
                      |> List.collect (fun p ->
                          p.Queue.Works |> Map.toList |> List.map (fun (_, w) ->
                              p.ProjectId, w.Id, w.StageGeneration, w.ClaimGeneration))
                      |> List.sort
                  FrozenReviews =
                      input.Projects
                      |> List.collect (fun p -> p.Reviews |> List.map (fun r ->
                          p.ProjectId, r.WorkId, r.FrozenCommit, r.Accepted))
                      |> List.sort
                  Integrations =
                      input.Projects
                      |> List.collect (fun p -> p.Integrations |> List.map (fun i ->
                          p.ProjectId, i.WorkId, i.SourceCommit, i.IntegratedCommit))
                      |> List.sort
                  OperationIds =
                      (account.Operations |> List.map (fun op -> account.LedgerId, op.OperationId))
                      @ (input.Projects |> List.collect (fun p ->
                          p.Operations |> List.map (fun op -> p.LedgerId, op.OperationId)))
                      |> List.sort
                  StoppedLaunches =
                      input.Projects
                      |> List.collect (fun p ->
                          p.Queue.Launches |> Map.toList |> List.choose (fun (_, launch) ->
                              match launch.Lease with
                              | Some lease when lease.StopRequested ->
                                  Some(p.ProjectId, launch.Id, lease.Generation)
                              | _ -> None))
                      |> List.sort
                  ReservedCapacity = occupied }
