module Client.SourceEffectHostBinding

open System
open System.IO
open Common
open Jarvis.WorkQueues
open Client.IO
open Client.LedgerStore
open Client.SourceEffectJournal
open Client.SourceEffectRouteBridge

// The only ingress value controlled by the caller is its ORIGINAL operation UUID.
// No caller may supply project/ledger/work/launch/source identity or a terminal receipt.
// The receiving host must reload its approved ledger binding and canonical project
// snapshot on every dispatch (including retries after a reconnect).
let private blocked reason = Error("RecoveryBlocked: " + reason)

let private samePath left right =
    let comparison =
        if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase
        else StringComparison.Ordinal
    String.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath left),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath right),
        comparison)

let private withinPath root candidate =
    let comparison =
        if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase
        else StringComparison.Ordinal
    let parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath root)
    let child = Path.TrimEndingDirectorySeparator(Path.GetFullPath candidate)
    String.Equals(parent, child, comparison)
    || child.StartsWith(parent + string Path.DirectorySeparatorChar, comparison)

// This testable pure boundary MUST be supplied with a state fetched and checked
// through WorkQueueGitLedger.load in the real caller. It does not accept a scope
// from a worker, a SignalR envelope, a TUI selector, or an old conversation.
let internal bindCanonicalClaim (binding: LedgerBinding) (project: Project)
    (trustedProjectName: string) (registeredCheckout: string)
    (command: AgentCommand) (originalOperationId: Guid)
    (store: SourceEffectStore) : Result<SourceRouteInvocation, string> =
    if originalOperationId = Guid.Empty then
        blocked "caller did not retain an original source-operation UUID."
    elif project.Migration <> MigrationEpoch.Frozen then
        // No migration or Scheduler phase may reuse this diagnostic bridge
        // as a production source-effect authorization.
        blocked "host-bound source bridge requires frozen migration and disabled Scheduler."
    else
        match classify command, AgentCommandInfo.projectName command |> Option.bind ProjectSelector.parse with
        | JournalCandidate(_, FileEdit, true), Some (projectName, Some checkout)
            when projectName = trustedProjectName && checkout = registeredCheckout ->
            let matches =
                project.Works
                |> Map.toList
                |> List.choose (fun (_, work) ->
                    match work.State, work.SourceLineage with
                    | WorkState.Claimed launchId, Some lineage
                        when work.Kind = WorkKind.Implementation
                             && work.ClaimGeneration > 0
                             && work.StageGeneration > 0
                             && work.ProjectId = binding.ProjectId.ToString("D")
                             && lineage.TaskId = work.SubjectId
                             && lineage.Generation > 0
                             && lineage.RepositoryId = WorkQueueGitLedger.WorkQueueGitLedger.sourceRepositoryId binding
                             && lineage.TargetRef.StartsWith("refs/heads/", StringComparison.Ordinal)
                             && work.Domain = DomainState.Undecided
                             && work.Push.IsNone
                             && not work.IntegrationAuthorized
                             && work.Gate = GateState.Clear ->
                        match project.Launches.TryFind launchId with
                        | Some launch when launch.Id = launchId
                                           && launch.PrimaryWorkId = Some work.Id
                                           && launch.ProjectId = binding.ProjectId.ToString("D")
                                           && launch.RoleVersion > 0
                                           && (match launch.State with
                                               | LaunchState.Registered (agentUuid, sessionKey) ->
                                                   not (String.IsNullOrWhiteSpace agentUuid || String.IsNullOrWhiteSpace sessionKey)
                                               | _ -> false)
                                           && registeredCheckout =
                                               sprintf "wq03-%s-%s-g%d" work.Id launchId work.ClaimGeneration ->
                            match Guid.TryParse launchId with
                            | true, launchUuid when launchUuid <> Guid.Empty ->
                                Some (work, lineage, launchUuid)
                            | _ -> None
                        | _ -> None
                    | _ -> None)
            match matches with
            | [ work, lineage, launchUuid ] ->
                let scope =
                    fromTrustedLedgerBinding binding trustedProjectName
                        (WorkQueueGitLedger.WorkQueueGitLedger.sourceRepositoryId binding)
                        lineage.TargetRef registeredCheckout work.Id launchUuid work.ClaimGeneration
                Ok { Journal = store
                     TrustedScope = scope
                     Intent =
                        { OriginalOperationId = originalOperationId
                          Scope = scope
                          Kind = FileEdit
                          RequestSha256 = requestSha256 command } }
            | _ -> blocked "no unique authoritative registered source claim matches project, repository, work, launch and generation."
        | JournalCandidate _ , _ -> blocked "file-only host transport cannot authorize this route or selected checkout."
        | Uncovered _, _ -> blocked "uncovered legacy/out-of-band source mutation."
        | _ -> blocked "not a bound host-owned source file-edit route."

// Host-only resolution: approved configuration, independently fetched ledger,
// canonical state and registered checkout. All failures precede filesystem/OS
// mutation and retain the source RecoveryBlocked fence.
let prepareHostFileEdit (resolved: ProjectPaths.ResolvedProject)
    (command: AgentCommand) (originalOperationId: Guid)
    (journalRoot: string) =
    try
        let target = ProjectPaths.ResolvedProject.target resolved
        let projectName = ProjectPaths.ProjectTarget.projectName target
        let checkout = ProjectPaths.ProjectTarget.checkout target
                       |> ProjectPaths.ProjectTarget.worktreeName
        let (FolderPath sourceRoot) = ProjectPaths.ResolvedProject.sourceRoot resolved
        let (FolderPath workspaceRoot) = ProjectPaths.ResolvedProject.workspaceRoot resolved
        match checkout with
        | None -> blocked "source command must select its exact registered generation worktree."
        | Some checkoutId ->
            if String.IsNullOrWhiteSpace journalRoot
               || not (Path.IsPathFullyQualified journalRoot)
               || withinPath sourceRoot journalRoot
               || withinPath workspaceRoot journalRoot then
                blocked "trusted source journal is absent or inside mutable source workspace."
            else
                match OrchestrationApi.LedgerBindingConfig.loadForContext resolved with
                | Error _ -> blocked "approved host ledger binding unavailable or unverifiable."
                | Ok binding when not (samePath binding.RepositoryPath sourceRoot) ->
                    blocked "trusted ledger binding does not refer to resolved registered checkout."
                | Ok binding ->
                    match LedgerStore.LedgerStore.sourceRepositoryIdentity binding with
                    | Error _ -> blocked "registered source/approved remote identity unavailable."
                    | Ok identity when identity.RepositoryId = "" ->
                        blocked "empty independently verified source repository identity."
                    | Ok _ ->
                        match WorkQueueGitLedger.WorkQueueGitLedger.load binding with
                        | Error _ -> blocked "canonical source claim snapshot not verified."
                        | Ok (_, canonical) ->
                            match openAtHostRoot journalRoot with
                            | Error reason -> Error reason
                            | Ok store ->
                                bindCanonicalClaim binding canonical projectName checkoutId
                                    command originalOperationId store
    with _ -> blocked "cannot derive trusted source effect envelope from host state."
