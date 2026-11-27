module Client.PromptContextAccounting

open System
open System.Collections.Generic
open System.Text
open System.Text.Json

// Classification and numeric metadata only; never use document names, identities,
// hashes or content as diagnostic labels. Ordinals are caller-defined stable
// layer slots within each kind, not positions in the composed prompt.
type SectionKind =
    | Framing = 0
    | Template = 1
    | Global = 2
    | Workspace = 3
    | ProjectDirectory = 4
    | Role = 5
    | Agent = 6
    | Assignment = 7
    | Checkpoint = 8
    | Review = 9

type Fragment =
    { Kind: SectionKind
      LayerOrdinal: int option
      Version: int option
      Text: string }

[<Struct>]
type Size =
    { Utf8Bytes: int64
      Utf16Units: int64 }

type Baseline =
    | ActualPrompt of string
    | SizeOnly of Size

// All thresholds are explicitly supplied advisory limits (exceeded strictly).
// Missing observations never manufacture a comparison or a warning.
type Budgets =
    { PromptUtf8Bytes: int64 option
      PromptUtf16Units: int64 option
      EnvelopeUtf8Bytes: int64 option
      GrowthUtf8Bytes: int64 option }

let noBudgets =
    { PromptUtf8Bytes = None
      PromptUtf16Units = None
      EnvelopeUtf8Bytes = None
      GrowthUtf8Bytes = None }

type WarningReason =
    | PromptUtf8 = 0
    | PromptUtf16 = 1
    | EnvelopeUtf8 = 2
    | GrowthUtf8 = 3

type SectionMetric =
    { Kind: SectionKind
      LayerOrdinal: Nullable<int>
      Version: Nullable<int>
      Size: Size
      DuplicateLayer: bool }

type Warning =
    { Reason: WarningReason
      Observed: int64
      Threshold: int64 }

// Null prefix means unavailable, including for a size-only baseline;
// zero is an exact observation, not an unavailable marker.
type Diagnostic =
    { Sections: SectionMetric array
      Prompt: Size
      EnvelopeUtf8Bytes: Nullable<int64>
      Delta: Nullable<Size>
      CommonPrefixUtf8Bytes: Nullable<int64>
      Warnings: Warning array }

let private utf8 = UTF8Encoding(false, true)

let private validateText parameter (value: string) =
    if isNull value then invalidArg parameter "Text must not be null."
    try utf8.GetByteCount value
    with :? EncoderFallbackException ->
        invalidArg parameter "Text must contain well-formed UTF-16."

let private size parameter value =
    { Utf8Bytes = int64 (validateText parameter value)
      Utf16Units = int64 value.Length }

let private isLayer kind =
    match kind with
    | SectionKind.Global | SectionKind.Workspace | SectionKind.ProjectDirectory
    | SectionKind.Role | SectionKind.Agent | SectionKind.Assignment -> true
    | _ -> false

let private validateSize value =
    if value.Utf16Units < 0L || value.Utf8Bytes < value.Utf16Units
       || (value.Utf16Units <= Int64.MaxValue / 3L
           && value.Utf8Bytes > value.Utf16Units * 3L) then
        invalidArg "baseline" "Baseline sizes are inconsistent with well-formed UTF-8/UTF-16."

let private nullable value =
    match value with
    | Some v -> Nullable v
    | None -> Nullable()

// Fragments are already rendered, including all delimiters and whitespace.
// Each fragment must be independently well-formed Unicode. Reject a surrogate
// pair split across fragments rather than silently misattribute its byte count.
// The envelope, if supplied, is the actual serialized argument string, not an
// estimate derived from the prompt. No JSON validation or transport decisions.
let account (fragments: Fragment list) (baseline: Baseline option)
            (serializedEnvelope: string option) (budgets: Budgets) : Diagnostic =
    if obj.ReferenceEquals(fragments, null) then invalidArg "fragments" "Fragments must not be null."
    if obj.ReferenceEquals(budgets, null) then invalidArg "budgets" "Budgets must not be null."
    for threshold in [ budgets.PromptUtf8Bytes; budgets.PromptUtf16Units
                       budgets.EnvelopeUtf8Bytes; budgets.GrowthUtf8Bytes ] do
        if Option.exists (fun n -> n < 0L) threshold then
            invalidArg "budgets" "Thresholds must be nonnegative."

    let baselineSize, baselineBytes =
        match baseline with
        | None -> None, None
        | Some(ActualPrompt prompt) ->
            let measured = size "baseline" prompt
            Some measured, Some(utf8.GetBytes prompt)
        | Some(SizeOnly measured) ->
            validateSize measured
            Some measured, None
        | Some _ -> invalidArg "baseline" "Baseline must be valid."

    let envelopeBytes = serializedEnvelope |> Option.map (validateText "serializedEnvelope" >> int64)
    let seen = HashSet<SectionKind * int>()
    let mutable total = { Utf8Bytes = 0L; Utf16Units = 0L }
    let mutable prefix = 0
    let mutable matching = true
    let metrics =
        fragments
        |> List.map (fun fragment ->
            if obj.ReferenceEquals(fragment, null) then invalidArg "fragments" "Fragment must not be null."
            if not (Enum.IsDefined(typeof<SectionKind>, fragment.Kind)) then
                invalidArg "fragments" "Unknown section kind."
            if Option.exists (fun n -> n < 0) fragment.Version then
                invalidArg "fragments" "Version must be nonnegative."
            if Option.exists (fun n -> n < 0 || not (isLayer fragment.Kind)) fragment.LayerOrdinal then
                invalidArg "fragments" "Layer ordinal must be nonnegative and identify a layer kind."

            let measured = size "fragments" fragment.Text
            total <-
                { Utf8Bytes = Checked.(+) total.Utf8Bytes measured.Utf8Bytes
                  Utf16Units = Checked.(+) total.Utf16Units measured.Utf16Units }

            // Compare only actual bytes, in rendered order. Stop at first
            // mismatch or exhaustion of the baseline; no text is retained.
            match baselineBytes with
            | Some bytes when matching && prefix < bytes.Length ->
                let current = utf8.GetBytes fragment.Text
                let mutable i = 0
                while matching && i < current.Length && prefix < bytes.Length do
                    if current.[i] = bytes.[prefix] then
                        i <- i + 1
                        prefix <- prefix + 1
                    else matching <- false
            | _ -> ()

            let duplicate =
                fragment.LayerOrdinal
                |> Option.exists (fun ordinal -> not (seen.Add(fragment.Kind, ordinal)))
            { Kind = fragment.Kind
              LayerOrdinal = nullable fragment.LayerOrdinal
              Version = nullable fragment.Version
              Size = measured
              DuplicateLayer = duplicate })
        |> List.toArray

    let delta =
        baselineSize
        |> Option.map (fun prior ->
            { Utf8Bytes = total.Utf8Bytes - prior.Utf8Bytes
              Utf16Units = total.Utf16Units - prior.Utf16Units })
    let warnings =
        [| for reason, observed, threshold in
               [ WarningReason.PromptUtf8, Some total.Utf8Bytes, budgets.PromptUtf8Bytes
                 WarningReason.PromptUtf16, Some total.Utf16Units, budgets.PromptUtf16Units
                 WarningReason.EnvelopeUtf8, envelopeBytes, budgets.EnvelopeUtf8Bytes
                 WarningReason.GrowthUtf8, (delta |> Option.map (fun d -> d.Utf8Bytes)), budgets.GrowthUtf8Bytes ] do
               match observed, threshold with
               | Some value, Some limit when value > limit ->
                   yield { Reason = reason; Observed = value; Threshold = limit }
               | _ -> () |]
    { Sections = metrics
      Prompt = total
      EnvelopeUtf8Bytes = nullable envelopeBytes
      Delta = nullable delta
      CommonPrefixUtf8Bytes = nullable (baselineBytes |> Option.map (fun _ -> int64 prefix))
      Warnings = warnings }

// This serialization boundary accepts only the text-free diagnostic DTO.
let serialize (diagnostic: Diagnostic) = JsonSerializer.Serialize diagnostic
