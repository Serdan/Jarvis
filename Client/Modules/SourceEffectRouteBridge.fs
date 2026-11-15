module Client.SourceEffectRouteBridge

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Common
open Client.SourceEffectJournal

// A deliberately NONPRODUCTION boundary. The current source RPCs have no
// caller-stable SOURCE operation UUID or authenticated WQ03 launch/claim
// envelope. A ledger-write UUID, a SignalR correlation ID or a fresh Guid is
// never a substitute. No caller in the production dispatcher supplies a
// positive SourceRouteInvocation yet.
type RouteEvidence =
    | JournalCandidate of name: string * kind: SourceEffectKind * hostAwaitedFileEdit: bool
    | Uncovered of name: string
    | NotSourceMutation

let classify = function
    | WriteFileCommand _ -> JournalCandidate("WriteFile", FileEdit, true)
    | PatchFileCommand _ -> JournalCandidate("PatchFile", FileEdit, true)
    // The import includes network and filesystem effects and cannot get a
    // positive synchronous file-only terminal proof.
    | ImportFileCommand _ -> JournalCandidate("ImportFile", FileEdit, false)
    // Arbitrary subprocesses can themselves push/merge/launch descendants.
    // Their normal returned payload is NOT terminal source-effect evidence.
    | RunCommandCommand _ -> JournalCandidate("RunCommand", ManagedCommand, false)
    | RunProjectTaskCommand _ -> JournalCandidate("RunProjectTask", ManagedCommand, false)
    | StartJobCommand _ -> JournalCandidate("StartJob", ManagedJob, false)
    | GitCommitCommand _ -> JournalCandidate("GitCommit", GitCommit, false)
    // These source-capable paths have no trustworthy per-effect journal
    // coverage. The explicit negative classification is part of the gate.
    | CreateWorktreeCommand _ -> Uncovered "CreateWorktree"
    | RemoveWorktreeCommand _ -> Uncovered "RemoveWorktree"
    | CreateSkillCommand _ -> Uncovered "CreateSkill"
    | BrowserExportDomCommand _ -> Uncovered "BrowserExportDom"
    | ScheduleExecutionCommand _ -> Uncovered "ScheduledRunCommand"
    | RetryPendingIntentCommand _ -> Uncovered "RetryPendingIntent"
    | CreateProjectCommand _ -> Uncovered "CreateProject"
    | TaskWriteCommand c when c.Action = "ExportTaskSpec" || c.Action = "ImportTaskSpec" ->
        Uncovered "TaskSpecWorkspaceMutation"
    | _ -> NotSourceMutation

let requestSha256 (command: AgentCommand) =
    JsonSerializer.Serialize<AgentCommand>(command)
    |> Encoding.UTF8.GetBytes
    |> SHA256.HashData
    |> Convert.ToHexString
    |> fun hash -> hash.ToLowerInvariant()

let private isWq03Checkout (command: AgentCommand) =
    AgentCommandInfo.projectName command
    |> Option.bind ProjectSelector.parse
    |> Option.bind (fun (_, checkout) -> checkout)
    |> Option.exists (fun checkout -> checkout.StartsWith("wq03-", StringComparison.Ordinal))

// Inert unless the explicitly named nonproduction gate is enabled by the
// local test operator. This DOES NOT claim to journal regular RPCs: since no
// original source UUID is transported, it denies candidate/uncovered routes
// before they can reach OS or Git execution. Normal/legacy work is unchanged.
let denyUnboundGenerationDispatch enabled command =
    if not enabled || not (isWq03Checkout command) then Ok()
    else
        match classify command with
        | NotSourceMutation -> Ok()
        | JournalCandidate(name, _, _) | Uncovered name ->
            Error("RecoveryBlocked: " + name +
                  " has no authenticated original source operation UUID and WQ03 binding; dispatch denied.")

type SourceRouteInvocation =
    { Journal: SourceEffectStore
      TrustedScope: SourceEffectScope
      Intent: SourceEffectIntent }

// Only an independently host-built invocation may call this diagnostic
// adapter. Validation pins the exact incoming route, project, registered
// generation checkout and serialized request to the original UUID.
let private verifyInvocation command (invocation: SourceRouteInvocation) =
    match classify command with
    | NotSourceMutation -> Error "RecoveryBlocked: not a source-mutation route."
    | Uncovered name -> Error("RecoveryBlocked: " + name + " is an uncovered source route.")
    | JournalCandidate(name, kind, hostAwaited) ->
        match AgentCommandInfo.projectName command |> Option.bind ProjectSelector.parse with
        | Some(project, Some checkout)
            when project = invocation.Intent.Scope.ProjectName
                 && checkout = invocation.Intent.Scope.CheckoutId
                 && checkout.StartsWith("wq03-", StringComparison.Ordinal)
                 && invocation.Intent.Kind = kind
                 && invocation.Intent.Scope = invocation.TrustedScope
                 && invocation.Intent.RequestSha256 = requestSha256 command
                 && invocation.Intent.OriginalOperationId <> Guid.Empty ->
            Ok(name, hostAwaited)
        | _ ->
            Error "RecoveryBlocked: source route, original UUID, request or registered generation identity mismatch."

// Executes the ACTUAL awaited handler, not a mock ledger transition.
// Returns the handler's native Result, or a fail-closed journal error. The
// only possible terminal observation is a successful, awaited host-owned
// WriteFile/PatchFile. Import/process/commit success still remains Uncertain;
// a missing/lost acknowledgement is never positive evidence for succession.
let executeDiagnostic (invocation: SourceRouteInvocation) command
    (execute: unit -> Task<Result<'T, 'E>>) : Task<Result<Result<'T, 'E>, string>> =
    task {
        match verifyInvocation command invocation with
        | Error reason -> return Error reason
        | Ok(name, true) ->
            return! runAwaitedHostFileEdit invocation.Journal invocation.Intent execute
        | Ok(name, false) ->
            match beginBeforeDispatch invocation.Journal invocation.Intent with
            | Error reason -> return Error reason
            | Ok(AlreadyRecorded _) ->
                return Error "RecoveryBlocked: original source operation already dispatched; no automatic replay."
            | Ok FirstDispatch ->
                try
                    let! outcome = execute()
                    let uncertainty =
                        "Authoritative terminal/process-tree/remote effect proof unavailable for " + name + "."
                    match recordUncertain invocation.Journal invocation.Intent uncertainty with
                    | Error reason -> return Error reason
                    | Ok() -> return Ok outcome
                with ex ->
                    let uncertainty = "Original " + name + " dispatch raised " + ex.GetType().Name + "."
                    match recordUncertain invocation.Journal invocation.Intent uncertainty with
                    | Ok() -> return Error("RecoveryBlocked: " + uncertainty)
                    | Error reason -> return Error reason
    }
