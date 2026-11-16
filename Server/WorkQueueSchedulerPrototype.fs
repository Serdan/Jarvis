namespace Jarvis.WorkQueues

open System
open Jarvis.WorkQueues.Contracts

// WQ08 prototype boundary. There is deliberately NO production Scheduler
// registration, timer, browser binding, or account/project Git adapter here.
// The future WQ04/WQ06 host supplies source evidence and durable CAS ports.
type OriginalDispatchProvenance =
    { AccountHead: string
      ProjectHead: string
      SourceRef: string option
      SourceCommit: string option }

type OriginalBrowserIntent =
    { LaunchId: string
      ProjectId: string
      WorkId: string
      WorkGeneration: int
      RoleId: string
      RoleVersion: int
      RequestFingerprint: string
      OperationId: string
      Provenance: OriginalDispatchProvenance }

[<RequireQualifiedAccess>]
type OriginalBrowserOutcome =
    | Accepted of receipt: string
    | DefinitiveNoEffect of reason: string
    | Unknown of reason: string

type OriginalBrowserJournalEntry =
    { Intent: OriginalBrowserIntent
      // None means the first send might have happened. Never blindly resend.
      Outcome: OriginalBrowserOutcome option }

[<RequireQualifiedAccess>]
type OriginalMarkResult =
    | Marked
    | Existing of OriginalBrowserJournalEntry

// Each port must be backed by a caller-owned durable, compare-and-swap record.
// In particular, TryMarkOriginal must win a durable one-shot marker BEFORE
// SubmitOriginal is invoked. These ports are not wired to the live server.
type SchedulerDispatchPorts =
    { TryMarkOriginal: OriginalBrowserIntent -> Result<OriginalMarkResult, string>
      FindOriginal: string -> Result<OriginalBrowserJournalEntry option, string>
      SubmitOriginal: OriginalBrowserIntent -> OriginalBrowserOutcome
      RecordOriginalOutcome: OriginalBrowserIntent -> OriginalBrowserOutcome -> Result<unit, string>
      RecordProjectDispatch: InertLaunchPlan -> DispatchState -> Result<unit, string> }

// A single non-agent tick: the real adapters must commit account/project CAS
// in this order. C01 abort is legal only for an unbound launch and must be
// followed by the verified terminal global release, never a blind token drop.
type SchedulerAdmissionPorts =
    { ReserveGlobal: InertLaunchPlan -> Result<Account, string>
      AdmitProject: InertLaunchPlan -> Account -> Result<Project, string>
      AbortUnboundC01: InertLaunchPlan -> Result<Project, string>
      ReleaseAborted: InertLaunchPlan -> Account -> Project -> Result<unit, string>
      Browser: SchedulerDispatchPorts }

type SchedulerReconciliation =
    { Intent: OriginalBrowserIntent
      LastObserved: OriginalBrowserOutcome option
      Reason: string }

[<RequireQualifiedAccess>]
type SchedulerDispatchResult =
    | Submitted of OriginalBrowserIntent * receipt: string
    | DefinitiveNoEffect of SchedulerReconciliation
    | NeedsReconciliation of SchedulerReconciliation
    | AbortedC01 of OriginalBrowserIntent
    | NotReady of string

module SchedulerDispatchPrototype =
    let private nonBlank (value: string) = not (String.IsNullOrWhiteSpace value)

    // WQ04 typed role/demand selection and WQ07 project/account receipts meet
    // here. This validates consistency in an already admitted projection; it
    // does NOT turn fixture data into a verified canonical producer snapshot.
    let prepareOriginal (plan: InertLaunchPlan) (demand: RankedRoleDemand)
                        (role: RoleConfiguration) (provenance: OriginalDispatchProvenance)
                        (account: Account) (project: Project) =
        let launch = plan.Launch
        if not (nonBlank provenance.AccountHead && nonBlank provenance.ProjectHead)
           || not (nonBlank plan.RequestFingerprint && nonBlank plan.DispatchOperationId) then
            Error "Missing account/project head or original operation identity; inspect canonical Git heads"
        elif demand.WorkId <> plan.WorkId || demand.ProjectId <> launch.ProjectId
             || demand.StageGeneration <> plan.WorkGeneration
             || demand.RoleId <> launch.RoleId || demand.RoleVersion <> launch.RoleVersion then
            Error "WQ04 demand differs from pinned launch; reread the original role/work generation"
        elif role.RoleId <> launch.RoleId || role.Version <> launch.RoleVersion
             || not role.Enabled || not (nonBlank role.Instructions)
             || not (role.QueueSubscriptions.Contains demand.QueueId) then
            Error "Pinned WQ04 role configuration unavailable or incompatible"
        elif account.Tokens.TryFind launch.Id <> Some false
             || account.Receipts.TryFind plan.ReserveOperationId <> Some("reserve|" + launch.Id) then
            Error "Account reservation/original receipt absent; reconcile account Git CAS first"
        elif not (project.Receipts.ContainsKey plan.AdmitOperationId) then
            Error "Project admission receipt absent; reconcile C01 project CAS first"
        else
            match project.Launches.TryFind launch.Id, project.Works.TryFind plan.WorkId with
            | Some admitted, Some work
                when admitted.ProjectId = launch.ProjectId
                     && admitted.RoleId = launch.RoleId
                     && admitted.RoleVersion = launch.RoleVersion
                     && admitted.AccountTokenId = launch.AccountTokenId
                     && admitted.Credit = Some plan.WorkId
                     && work.StageGeneration = plan.WorkGeneration
                     && work.QueueId = demand.QueueId
                     && (match admitted.State with
                         | LaunchState.ReservedUnregistered
                         | LaunchState.SubmittedUnregistered _ -> true
                         | _ -> false) ->
                Ok
                    { LaunchId = launch.Id
                      ProjectId = launch.ProjectId
                      WorkId = plan.WorkId
                      WorkGeneration = plan.WorkGeneration
                      RoleId = launch.RoleId
                      RoleVersion = launch.RoleVersion
                      RequestFingerprint = plan.RequestFingerprint
                      OperationId = plan.DispatchOperationId
                      Provenance = provenance }
            | _ ->
                Error "Project launch credit or generation no longer matches; inspect project terminal state"

    let private needs intent observation reason =
        SchedulerDispatchResult.NeedsReconciliation
            { Intent = intent; LastObserved = observation; Reason = reason }

    let private noEffect intent reason =
        SchedulerDispatchResult.DefinitiveNoEffect
            { Intent = intent
              LastObserved = Some(OriginalBrowserOutcome.DefinitiveNoEffect reason)
              Reason = "Original browser attempt definitively did not submit; reconcile project terminal and global release before a new launch" }

    let private finish (ports: SchedulerDispatchPorts) (plan: InertLaunchPlan)
                       (intent: OriginalBrowserIntent) outcome =
        let record state =
            try ports.RecordProjectDispatch plan state with ex -> Error ex.Message
        match outcome with
        | OriginalBrowserOutcome.Accepted receipt when nonBlank receipt ->
            match record DispatchState.Submitted with
            | Ok () -> SchedulerDispatchResult.Submitted(intent, receipt)
            | Error why ->
                needs intent (Some outcome) ("Browser receipt saved, project dispatch CAS unresolved: " + why)
        | OriginalBrowserOutcome.Accepted _ ->
            needs intent (Some outcome) "Empty browser submission receipt; inspect original browser response"
        | OriginalBrowserOutcome.DefinitiveNoEffect reason ->
            noEffect intent reason
        | OriginalBrowserOutcome.Unknown reason ->
            match record DispatchState.OutcomeUnknown with
            | Ok () -> needs intent (Some outcome) ("Original browser outcome unknown: " + reason)
            | Error why ->
                needs intent (Some outcome) ("Original browser outcome unknown; project CAS also unresolved: " + reason + "; " + why)

    let private reuse (ports: SchedulerDispatchPorts) plan intent (existing: OriginalBrowserJournalEntry) =
        if existing.Intent <> intent then
            needs intent existing.Outcome "Original journal identity differs: preserve both records and inspect the CAS heads"
        else
            match existing.Outcome with
            | None ->
                needs intent None "One-shot marker exists without a settled browser result; inspect original execution, never resend"
            | Some outcome -> finish ports plan intent outcome

    let private recover (ports: SchedulerDispatchPorts) plan intent =
        let found =
            try ports.FindOriginal intent.OperationId with ex -> Error ex.Message
        match found with
        | Error why -> needs intent None ("Cannot read original one-shot journal: " + why)
        | Ok None -> needs intent None "Project says submitted/unknown but original browser journal is missing; inspect original IDs"
        | Ok(Some entry) -> reuse ports plan intent entry

    let runOriginal (ports: SchedulerDispatchPorts) (plan: InertLaunchPlan)
                    (demand: RankedRoleDemand) (role: RoleConfiguration)
                    provenance account project =
        match prepareOriginal plan demand role provenance account project with
        | Error why -> SchedulerDispatchResult.NotReady why
        | Ok intent ->
            match InertScheduler.dispatchAdvice plan project with
            | InertDispatchAdvice.DoNotSubmit ->
                SchedulerDispatchResult.NotReady "Launch is not eligible for a browser one-shot"
            | InertDispatchAdvice.ReconcileOriginal _
            | InertDispatchAdvice.AlreadySubmitted ->
                recover ports plan intent
            | InertDispatchAdvice.SubmitOriginal original when original <> intent.OperationId ->
                needs intent None "Dispatch operation mismatch; inspect pinned launch"
            | InertDispatchAdvice.SubmitOriginal _ ->
                // A marker write timeout is itself uncertain. Do not send.
                let marked =
                    try ports.TryMarkOriginal intent
                    with ex -> Error ex.Message
                match marked with
                | Error why ->
                    needs intent None ("Cannot confirm durable original one-shot marker: " + why)
                | Ok(OriginalMarkResult.Existing previous) ->
                    reuse ports plan intent previous
                | Ok OriginalMarkResult.Marked ->
                    // Unexpected browser exceptions are unknown outcomes, not
                    // evidence of failure/no-effect and not permission to retry.
                    let outcome =
                        try ports.SubmitOriginal intent
                        with ex -> OriginalBrowserOutcome.Unknown ex.Message
                    let recorded =
                        try ports.RecordOriginalOutcome intent outcome
                        with ex -> Error ex.Message
                    match recorded with
                    | Error why ->
                        needs intent (Some outcome) ("Original browser result could not be saved: " + why)
                    | Ok () -> finish ports plan intent outcome

    let private protect (call: unit -> Result<'T, string>) =
        try call () with ex -> Error ex.Message

    // Functional one-tick coordinator for an already-selected WQ08/WQ04
    // demand. No Manager slot is used. The caller-owned port implementations
    // must serialize each step through the corresponding verified Git CAS.
    // There is intentionally no live adapter or autonomous runtime loop.
    let drivePreparedLaunch (ports: SchedulerAdmissionPorts)
                            (plan: InertLaunchPlan) (demand: RankedRoleDemand)
                            (role: RoleConfiguration) (provenance: OriginalDispatchProvenance) =
        let launch = plan.Launch
        let original =
            { LaunchId = launch.Id
              ProjectId = launch.ProjectId
              WorkId = plan.WorkId
              WorkGeneration = plan.WorkGeneration
              RoleId = launch.RoleId
              RoleVersion = launch.RoleVersion
              RequestFingerprint = plan.RequestFingerprint
              OperationId = plan.DispatchOperationId
              Provenance = provenance }
        let roleMatches =
            demand.WorkId = plan.WorkId
            && demand.ProjectId = launch.ProjectId
            && demand.StageGeneration = plan.WorkGeneration
            && demand.RoleId = launch.RoleId
            && demand.RoleVersion = launch.RoleVersion
            && role.RoleId = launch.RoleId
            && role.Version = launch.RoleVersion
            && role.Enabled
            && nonBlank role.Instructions
            && role.QueueSubscriptions.Contains demand.QueueId
        if not (nonBlank provenance.AccountHead && nonBlank provenance.ProjectHead
                && nonBlank plan.DispatchOperationId) then
            SchedulerDispatchResult.NotReady "Missing original Git head or launch operation; inspect source before reservation"
        elif not roleMatches then
            SchedulerDispatchResult.NotReady "WQ04 demand/role mismatch; reread original canonical producer and role"
        else
            match protect (fun () -> ports.ReserveGlobal plan) with
            | Error why ->
                needs original None ("Account reserve CAS uncertain: " + why
                                     + "; reconcile original reserve operation, never reserve a second launch")
            | Ok reserved ->
                match protect (fun () -> ports.AdmitProject plan reserved) with
                | Ok admitted ->
                    runOriginal ports.Browser plan demand role provenance reserved admitted
                | Error why ->
                    // C01 may succeed only if no admission committed; a
                    // concurrent/lost-reply admission makes abort fail.
                    match protect (fun () -> ports.AbortUnboundC01 plan) with
                    | Error abortWhy ->
                        needs original None ("Project admission uncertain: " + why
                                             + "; C01 abort not proven: " + abortWhy)
                    | Ok terminal ->
                        match protect (fun () -> ports.ReleaseAborted plan reserved terminal) with
                        | Ok () -> SchedulerDispatchResult.AbortedC01 original
                        | Error releaseWhy ->
                            needs original None ("C01 tombstone recorded but global release unresolved: "
                                                 + releaseWhy)

    // An agent can use this packet directly after making a backup of the
    // current Git ledger head/diff. Exceptional ledger repairs remain audited
    // Git changes; they never require a new runtime migration or code edit.
    let manualRepairPacket (issue: SchedulerReconciliation) =
        let p = issue.Intent.Provenance
        String.concat "\n"
            [ "Launch: " + issue.Intent.LaunchId
              "Project/work: " + issue.Intent.ProjectId + "/" + issue.Intent.WorkId
              "Role/version: " + issue.Intent.RoleId + "/" + string issue.Intent.RoleVersion
              "Work generation: " + string issue.Intent.WorkGeneration
              "Fingerprint: " + issue.Intent.RequestFingerprint
              "Original browser operation: " + issue.Intent.OperationId
              "Account head observed: " + p.AccountHead
              "Project head observed: " + p.ProjectHead
              "Source ref: " + (p.SourceRef |> Option.defaultValue "(unknown)")
              "Source commit: " + (p.SourceCommit |> Option.defaultValue "(unknown)")
              "Original outcome: " + sprintf "%A" issue.LastObserved
              "Unresolved: " + issue.Reason
              "Repair: backup current ledger Git SHA and diff; inspect original operation and browser receipt; "
              + "commit any authorized direct ledger correction with reason and original IDs; reconcile project terminal before account release; never blindly resend." ]
