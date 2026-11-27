module Client.PromptSnapshotAssembly

open System
open System.Globalization
open Client.PromptContextAccounting

// Pure assembly boundary. The caller authenticates the binding/snapshot, resolves
// all applicable layers, selects exact historical evidence and splits the existing
// validated template into stable and identity-bearing rendered fragments.
// These records are not a canonical reader, a credential or launch permission.
type Scope =
    { Project: string
      Agent: string
      Snapshot: string }

type Evidence<'a> =
    | Missing
    | Resolved of 'a

type Scoped<'a> =
    { Scope: Scope
      Value: 'a }

type Template =
    { Version: int
      Common: string
      IdentitySuffix: string }

type LayerKind =
    | Global
    | Workspace
    | ProjectDirectory
    | Role
    | Agent

type Layer =
    { Kind: LayerKind
      Ordinal: int
      SourceKey: string
      Version: int option
      Text: string }

type Pin =
    { Task: string
      Attempt: string
      Generation: int
      SpecBlob: string }

type Spec =
    { Task: string
      Blob: string
      Html: string }

type Continuation =
    { Pin: Pin
      Sequence: int64
      Summary: string
      NextAction: string
      OwnerReportedEvidence: string option }

type Assignment =
    { Pin: Pin
      Issuance: Evidence<Scoped<Pin>>
      Spec: Evidence<Scoped<Spec>>
      // The exact selected sequence comes from the canonical selector, not
      // sorting arbitrary filenames or searching other generations here.
      SelectedCheckpoint: Evidence<int64 option>
      Checkpoint: Evidence<Scoped<Continuation> option> }

type Focus =
    | Taskless
    | Assigned of Assignment

type ActionKind =
    | Review
    | Dependency
    | Action

type ActionContext =
    { Pin: Pin option
      Kind: ActionKind
      Ordinal: int
      Text: string }

type Input =
    { Scope: Scope
      Template: Evidence<Scoped<Template>>
      Layers: Evidence<Scoped<Layer> list>
      Focus: Evidence<Scoped<Focus>>
      Actions: Evidence<Scoped<ActionContext> list> }

type Field =
    | ScopeField
    | TemplateField
    | LayersField
    | FocusField
    | IssuanceField
    | SpecField
    | CheckpointSelectionField
    | CheckpointField
    | ActionsField

// Diagnostics and validation failures deliberately carry no content, identifiers,
// source paths, hashes or raw exception messages.
type Invalid =
    | MissingEvidence of Field
    | ForeignScope of Field
    | InvalidValue of Field
    | DuplicateLayer
    | DuplicateAction
    | StalePin of Field
    | UnexpectedCheckpoint
    | InvalidAccountingInput

type Assembled =
    { StablePrefix: string
      Prompt: string
      Accounting: Diagnostic }

let private nonblank = String.IsNullOrWhiteSpace >> not
let private validPin (pin: Pin) =
    nonblank pin.Task && nonblank pin.Attempt && pin.Generation > 0 && nonblank pin.SpecBlob

let private kind = function
    | Global -> SectionKind.Global
    | Workspace -> SectionKind.Workspace
    | ProjectDirectory -> SectionKind.ProjectDirectory
    | Role -> SectionKind.Role
    | Agent -> SectionKind.Agent

let private layerLabel = function
    | Global -> "Global instructions"
    | Workspace -> "Workspace instructions"
    | ProjectDirectory -> "Project/directory instructions"
    | Role -> "Role instructions"
    | Agent -> "Agent instructions"

let private actionLabel = function
    | Review -> "Relevant review context (not correction authority)"
    | Dependency -> "Relevant dependency context (not launch authority)"
    | Action -> "Action context"

let private fragment section ordinal version body : Fragment =
    { Kind = section; LayerOrdinal = ordinal; Version = version; Text = body }

let private renderSection title body = "\n\n## " + title + "\n\n" + body
let private concat (fragments: Fragment list) = fragments |> List.map _.Text |> String.concat ""
let private number (n: int64) = n.ToString(CultureInfo.InvariantCulture)

// No side effects, IO, time, ambient locale, truncation or policy decisions.
// Explicitly resolved empty collections and absent eligible checkpoints are
// valid; missing evidence is not silently converted to empty context.
let assemble (input: Input) baseline budgets : Result<Assembled, Invalid list> =
    let errors = ResizeArray<Invalid>()
    let reject error = errors.Add error
    let scoped field (value: Scoped<'a>) =
        if value.Scope <> input.Scope then reject (ForeignScope field)
        value.Value
    let resolve field = function
        | Missing -> reject (MissingEvidence field); None
        | Resolved value -> Some value
    let resolveScoped field evidence = resolve field evidence |> Option.map (scoped field)
    if not (nonblank input.Scope.Project && nonblank input.Scope.Agent && nonblank input.Scope.Snapshot) then
        reject (InvalidValue ScopeField)
    let template = resolveScoped TemplateField input.Template
    template |> Option.iter (fun t ->
        if t.Version < 0 || not (nonblank t.Common && nonblank t.IdentitySuffix) then
            reject (InvalidValue TemplateField))
    let layers =
        resolve LayersField input.Layers
        |> Option.defaultValue []
        |> List.map (scoped LayersField)
        |> List.sortBy (fun layer -> int (kind layer.Kind), layer.Ordinal)
    let slots = layers |> List.map (fun layer -> layer.Kind, layer.Ordinal)
    let sources = layers |> List.map _.SourceKey
    if List.distinct slots <> slots || List.distinct sources <> sources then reject DuplicateLayer
    for layer in layers do
        if layer.Ordinal < 0 || not (nonblank layer.SourceKey) || isNull layer.Text
           || Option.exists (fun version -> version < 0) layer.Version then
            reject (InvalidValue LayersField)
    let focus = resolveScoped FocusField input.Focus
    let mutable focusPin = None
    let mutable assignmentFragments = []
    match focus with
    | Some (Assigned assignment) ->
        let pin = assignment.Pin
        focusPin <- Some pin
        if not (validPin pin) then reject (InvalidValue FocusField)
        match resolveScoped IssuanceField assignment.Issuance with
        | Some issued when issued <> pin -> reject (StalePin IssuanceField)
        | _ -> ()
        let spec = resolveScoped SpecField assignment.Spec
        match spec with
        | Some value when value.Task <> pin.Task || value.Blob <> pin.SpecBlob -> reject (StalePin SpecField)
        | Some value when not (nonblank value.Html) -> reject (InvalidValue SpecField)
        | _ -> ()
        let selected = resolve CheckpointSelectionField assignment.SelectedCheckpoint
        let checkpoint =
            resolve CheckpointField assignment.Checkpoint
            |> Option.map (Option.map (scoped CheckpointField))
        match selected, checkpoint with
        | Some None, Some None -> ()
        | Some (Some seq), Some (Some c) ->
            if seq <= 0L || c.Sequence <> seq || c.Pin <> pin then reject (StalePin CheckpointField)
            if not (nonblank c.Summary && nonblank c.NextAction) || Option.exists isNull c.OwnerReportedEvidence then
                reject (InvalidValue CheckpointField)
        | Some (Some _), Some None -> reject (MissingEvidence CheckpointField)
        | Some None, Some (Some _) -> reject UnexpectedCheckpoint
        | _ -> ()
        // Render exact issued content, not the current mutable task spec.
        if errors.Count = 0 then
            assignmentFragments <-
                [ fragment SectionKind.Framing None None
                    (renderSection "Assignment identity"
                        ("Task: " + pin.Task + "\nAttempt: " + pin.Attempt +
                         "\nGeneration: " + number (int64 pin.Generation) + "\nIssued spec blob: " + pin.SpecBlob))
                  fragment SectionKind.Assignment (Some 0) None
                    (renderSection "Issued task specification" spec.Value.Html)
                  match checkpoint.Value with
                  | None ->
                      fragment SectionKind.Checkpoint None None
                          (renderSection "Continuation" "No eligible checkpoint resolved.")
                  | Some c ->
                      fragment SectionKind.Checkpoint None None
                          (renderSection ("Continuation — sequence " + number c.Sequence)
                              (c.Summary + "\n\nNext action:\n" + c.NextAction +
                               "\n\nOwner-reported evidence:\n" + (c.OwnerReportedEvidence |> Option.defaultValue "Not provided."))) ]
    | Some Taskless | None -> ()
    let actions =
        resolve ActionsField input.Actions
        |> Option.defaultValue []
        |> List.map (scoped ActionsField)
        |> List.sortBy (fun action -> action.Kind, action.Ordinal)
    let actionSlots = actions |> List.map (fun action -> action.Kind, action.Ordinal)
    if List.distinct actionSlots <> actionSlots then reject DuplicateAction
    for action in actions do
        if action.Pin <> focusPin then reject (StalePin ActionsField)
        if action.Ordinal < 0 || not (nonblank action.Text) then reject (InvalidValue ActionsField)
    if errors.Count > 0 then Error (List.ofSeq errors)
    else
        let t = template.Value
        let layerFragments =
            layers |> List.map (fun layer ->
                fragment (kind layer.Kind) (Some layer.Ordinal) layer.Version
                    (renderSection (layerLabel layer.Kind) layer.Text))
        // Agent-specific instructions belong in the suffix so the stable
        // prefix is shared by agents with the same common/project/role layers.
        let commonLayers, agentLayers =
            layerFragments |> List.partition (fun layer -> layer.Kind <> SectionKind.Agent)
        let prefix =
            [ fragment SectionKind.Template None (Some t.Version) t.Common
              fragment SectionKind.Framing None None
                  (renderSection "Instruction precedence"
                      "Global, workspace, project/directory, role (attachment order), agent, assignment. Higher entries retain priority over later entries.") ]
            @ commonLayers
        let actionFragments =
            actions |> List.map (fun action ->
                let section = if action.Kind = Review then SectionKind.Review else SectionKind.Framing
                fragment section None None (renderSection (actionLabel action.Kind) action.Text))
        let all =
            prefix @ [ fragment SectionKind.Framing None None ("\n\n" + t.IdentitySuffix) ]
            @ agentLayers @ assignmentFragments @ actionFragments
        try
            // The envelope is unavailable at this pure boundary. Its actual
            // serialization belongs to the later transport adapter.
            let accounting = account all baseline None budgets
            Ok { StablePrefix = concat prefix; Prompt = concat all; Accounting = accounting }
        with :? ArgumentException -> Error [ InvalidAccountingInput ]
