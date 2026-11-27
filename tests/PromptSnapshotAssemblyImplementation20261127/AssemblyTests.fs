module PromptSnapshotAssemblyImplementationTests

open System.Text
open NUnit.Framework
open Client.PromptSnapshotAssembly
open Client.PromptContextAccounting

let scope = { Project = "Fixture"; Agent = "Worker"; Snapshot = "snapshot-1" }
let pin = { Task = "task"; Attempt = "attempt"; Generation = 2; SpecBlob = "issued-blob" }
let scoped scope value = { Scope = scope; Value = value }
let resolved scope value = Resolved (scoped scope value)
let template = { Version = 8; Common = "COMMON"; IdentitySuffix = "Your agent ID is Worker." }
let layer kind ordinal source body =
    { Kind = kind; Ordinal = ordinal; SourceKey = source; Version = Some 1; Text = body }
let spec = { Task = pin.Task; Blob = pin.SpecBlob; Html = "<h1>Source preserved</h1>" }
let continuation =
    { Pin = pin; Sequence = 100L; Summary = "LATEST-ELIGIBLE-YIELD"
      NextAction = "Continue fixture"; OwnerReportedEvidence = Some "owner-reported fixture evidence" }
let assignment =
    { Pin = pin; Issuance = resolved scope pin; Spec = resolved scope spec
      SelectedCheckpoint = Resolved (Some 100L); Checkpoint = Resolved (Some (scoped scope continuation)) }
let input =
    { Scope = scope; Template = resolved scope template
      Layers = Resolved [
          scoped scope (layer Role 1 "role-B" "ROLE-ORDER-SECOND: 界")
          scoped scope (layer Global 0 "global" "GLOBAL")
          scoped scope (layer Agent 0 "agent" "AGENT-SPECIFIC")
          scoped scope (layer Role 0 "role-A" "ROLE-ORDER-FIRST: å")
          scoped scope (layer ProjectDirectory 1 "src" "DIRECTORY-GUIDANCE")
          scoped scope (layer Workspace 0 "workspace" "WORKSPACE-GUIDANCE")
          scoped scope (layer ProjectDirectory 0 "project" "PROJECT-GUIDANCE") ]
      Focus = resolved scope (Assigned assignment)
      Actions = Resolved [
          scoped scope { Pin = Some pin; Kind = ActionKind.Dependency; Ordinal = 0; Text = "OWN-DEPENDENCY" }
          scoped scope { Pin = Some pin; Kind = ActionKind.Review; Ordinal = 0; Text = "OWN-FOCUSED-REVIEW" } ] }
let run input baseline = assemble input baseline noBudgets
let ok = function Ok result -> result | Error e -> failwithf "Unexpected assembly failure: %A" e
let rejected (expected: Invalid) (result: Result<Assembled, Invalid list>) =
    match result with
    | Error errors -> Assert.That(List.contains expected errors, Is.True, sprintf "Expected %A among %A" expected errors)
    | Ok _ -> Assert.Fail("Invalid evidence assembled a prompt.")
let withAssignment assignment = { input with Focus = resolved scope (Assigned assignment) }

[<Test>]
let exact_fixture_obligations_and_stable_prefix () =
    let result = run input None |> ok
    for value in [ spec.Html; "ROLE-ORDER-FIRST: å"; "ROLE-ORDER-SECOND: 界"; "AGENT-SPECIFIC"; continuation.Summary ] do
        Assert.That(result.Prompt, Does.Contain(value))
        Assert.That(result.Prompt.IndexOf(value), Is.EqualTo(result.Prompt.LastIndexOf(value)))
    Assert.That(result.Prompt, Does.Not.Contain("LATEST-NOT-ISSUED"))
    Assert.That(result.Prompt.IndexOf("ROLE-ORDER-FIRST"), Is.LessThan(result.Prompt.IndexOf("ROLE-ORDER-SECOND")))
    Assert.That(result.StablePrefix, Does.Not.Contain("AGENT-SPECIFIC"))
    Assert.That(result.StablePrefix, Does.Not.Contain(pin.SpecBlob))
    Assert.That(result.StablePrefix, Does.Not.Contain(template.IdentitySuffix))
    Assert.That(result.Prompt, Does.StartWith(result.StablePrefix))
    Assert.That(result.Accounting.Prompt.Utf8Bytes, Is.EqualTo(int64 (Encoding.UTF8.GetByteCount result.Prompt)))
    Assert.That(result.Accounting.Prompt.Utf16Units, Is.EqualTo(int64 result.Prompt.Length))
    Assert.That(result.Accounting.Sections |> Array.exists _.DuplicateLayer, Is.False)
    Assert.That(result.Accounting.EnvelopeUtf8Bytes.HasValue, Is.False)
    let diagnostics = serialize result.Accounting
    for secret in [ "ROLE-ORDER-FIRST"; "AGENT-SPECIFIC"; pin.SpecBlob; scope.Agent; "role-A"; scope.Snapshot; "OWN-FOCUSED-REVIEW" ] do
        Assert.That(diagnostics, Does.Not.Contain(secret))

[<Test>]
let permutations_and_changed_identity_preserve_common_prefix () =
    let original = run input None |> ok
    let layers = match input.Layers with Resolved xs -> xs | Missing -> failwith "fixture"
    let actions = match input.Actions with Resolved xs -> xs | Missing -> failwith "fixture"
    let permuted = { input with Layers = Resolved (List.rev layers); Actions = Resolved (List.rev actions) }
    Assert.That((run permuted None |> ok).Prompt, Is.EqualTo(original.Prompt))
    let other = { scope with Agent = "Worker 2"; Snapshot = "snapshot-2" }
    let taskless =
        { Scope = other
          Template = resolved other { template with IdentitySuffix = "Your agent ID is Worker 2." }
          Layers = Resolved (layers |> List.filter (fun x -> x.Value.Kind <> Agent) |> List.map (fun x -> scoped other x.Value))
          Focus = resolved other Taskless; Actions = Resolved [] }
    let changed = run taskless (Some (ActualPrompt original.Prompt)) |> ok
    Assert.That(changed.StablePrefix, Is.EqualTo(original.StablePrefix))
    Assert.That(changed.Accounting.CommonPrefixUtf8Bytes.Value, Is.GreaterThanOrEqualTo(int64 (Encoding.UTF8.GetByteCount original.StablePrefix)))
    Assert.That(changed.Prompt, Does.Not.Contain(spec.Html))
    Assert.That(changed.Prompt, Does.Not.Contain(continuation.Summary))

[<Test>]
let reject_foreign_scope_duplicate_layers_stale_and_missing_pin () =
    let layers = match input.Layers with Resolved xs -> xs | Missing -> failwith "fixture"
    let foreign = { scope with Project = "FOREIGN-SECRET" }
    run { input with Layers = Resolved (scoped foreign layers.Head.Value :: layers.Tail) } None
    |> rejected (ForeignScope LayersField)
    run { input with Layers = Resolved (layers.Head :: layers) } None |> rejected DuplicateLayer
    run { input with Layers = Resolved ({ layers.Head with Value = { layers.Head.Value with Ordinal = 99 } } :: layers) } None
    |> rejected DuplicateLayer
    run (withAssignment { assignment with Issuance = Missing }) None |> rejected (MissingEvidence IssuanceField)
    run (withAssignment { assignment with Issuance = resolved scope { pin with Generation = 1 } }) None |> rejected (StalePin IssuanceField)
    run (withAssignment { assignment with Spec = resolved scope { spec with Blob = "latest"; Html = "LATEST-NOT-ISSUED" } }) None
    |> rejected (StalePin SpecField)
    run (withAssignment { assignment with Spec = Missing }) None |> rejected (MissingEvidence SpecField)
    run { input with Focus = Missing } None |> rejected (MissingEvidence FocusField)
    run { input with Actions = Missing } None |> rejected (MissingEvidence ActionsField)

[<Test>]
let checkpoint_selection_and_action_scope_are_exact () =
    run (withAssignment { assignment with Checkpoint = Resolved None }) None |> rejected (MissingEvidence CheckpointField)
    run (withAssignment { assignment with SelectedCheckpoint = Missing }) None |> rejected (MissingEvidence CheckpointSelectionField)
    run (withAssignment { assignment with SelectedCheckpoint = Resolved None }) None |> rejected UnexpectedCheckpoint
    run (withAssignment { assignment with Checkpoint = Resolved (Some (scoped scope { continuation with Sequence = 99L })) }) None
    |> rejected (StalePin CheckpointField)
    run (withAssignment { assignment with Checkpoint = Resolved (Some (scoped scope { continuation with Pin = { pin with Generation = 1 } })) }) None
    |> rejected (StalePin CheckpointField)
    let absent = run (withAssignment { assignment with SelectedCheckpoint = Resolved None; Checkpoint = Resolved None }) None |> ok
    Assert.That(absent.Prompt, Does.Contain("No eligible checkpoint resolved."))
    Assert.That(absent.Prompt, Does.Not.Contain(continuation.Summary))
    let foreignAction = { Pin = Some { pin with Task = "foreign" }; Kind = ActionKind.Review; Ordinal = 0; Text = "FOREIGN-REVIEW-SECRET" }
    let error = run { input with Actions = Resolved [scoped scope foreignAction] } None
    error |> rejected (StalePin ActionsField)
    Assert.That(sprintf "%A" error, Does.Not.Contain("FOREIGN-REVIEW-SECRET"))

[<Test>]
let invalid_unicode_is_typed_and_budgets_are_advisory () =
    let invalid = System.String(char 0xD800, 1)
    run { input with Template = resolved scope { template with Common = invalid } } None
    |> rejected InvalidAccountingInput
    let normal = run input None |> ok
    let measured = assemble input None { noBudgets with PromptUtf8Bytes = Some 1L } |> ok
    Assert.That(measured.Prompt, Is.EqualTo(normal.Prompt))
    Assert.That(measured.Accounting.Warnings.Length, Is.EqualTo(1))
