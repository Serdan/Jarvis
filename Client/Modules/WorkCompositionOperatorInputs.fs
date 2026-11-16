module Client.WorkCompositionOperatorInputs

open System
open System.Text
open System.Text.Json
open Jarvis.WorkQueues

// Small, explicitly shaped JSON input for the MCP operator surface.
// Arbitrary canonical acceptance evidence is intentionally not expressible.
[<CLIMutable>]
type CompositionMutationInput =
    { Action: string
      UnitId: string
      Kind: string
      ParentId: string
      Title: string
      Goal: string
      Scope: string
      AcceptanceCriteria: string array
      Constraints: string array
      LinkedTaskId: string
      ExpectedSpecVersion: int
      PrerequisiteId: string
      Reason: string }

module WorkCompositionOperatorInputs =
    let private optionalText (value: string) =
        if String.IsNullOrWhiteSpace value then None else Some value

    let private strings (items: string array) =
        if isNull items then Set.empty else Set.ofArray items

    let private spec (input: CompositionMutationInput) =
        { Title = input.Title
          Goal = input.Goal
          Scope = input.Scope
          AcceptanceCriteria = strings input.AcceptanceCriteria
          Constraints = strings input.Constraints
          LinkedTaskId = optionalText input.LinkedTaskId }

    let parseMutation (json: string) : Result<WorkCompositionMutation, string> =
        if String.IsNullOrWhiteSpace json then
            Error "A work-composition mutation JSON object is required."
        elif Encoding.UTF8.GetByteCount json > 65536 then
            Error "Work-composition mutation JSON exceeds the 64 KiB limit."
        else
            try
                let options = JsonSerializerOptions()
                options.PropertyNameCaseInsensitive <- true
                let input = JsonSerializer.Deserialize<CompositionMutationInput>(json, options)
                if isNull (box input) then
                    Error "A work-composition mutation JSON object is required."
                elif String.IsNullOrWhiteSpace input.UnitId then
                    Error "unitId is required."
                else
                    let action = if isNull input.Action then "" else input.Action.Trim().ToLowerInvariant()
                    match action with
                    | "create" ->
                        match (if isNull input.Kind then "" else input.Kind.Trim().ToLowerInvariant()) with
                        | "project" -> Ok(WorkCompositionMutation.Create(input.UnitId, WorkUnitKind.Project, optionalText input.ParentId, spec input))
                        | "system" -> Ok(WorkCompositionMutation.Create(input.UnitId, WorkUnitKind.System, optionalText input.ParentId, spec input))
                        | "feature" -> Ok(WorkCompositionMutation.Create(input.UnitId, WorkUnitKind.Feature, optionalText input.ParentId, spec input))
                        | _ -> Error "kind must be Project, System, or Feature."
                    | "revise" ->
                        Ok(WorkCompositionMutation.Revise(input.UnitId, input.ExpectedSpecVersion, spec input))
                    | "move" ->
                        Ok(WorkCompositionMutation.Move(input.UnitId, optionalText input.ParentId))
                    | "require" ->
                        Ok(WorkCompositionMutation.Require(input.UnitId, input.PrerequisiteId))
                    | "unrequire" ->
                        Ok(WorkCompositionMutation.Unrequire(input.UnitId, input.PrerequisiteId))
                    | "cancel" ->
                        Ok(WorkCompositionMutation.Cancel(input.UnitId, input.Reason))
                    | "reopen" ->
                        Ok(WorkCompositionMutation.Reopen input.UnitId)
                    | "accept" ->
                        Error "Acceptance requires a separately verified integrated submission."
                    | _ ->
                        Error "action must be Create, Revise, Move, Require, Unrequire, Cancel, or Reopen."
            with :? JsonException as ex ->
                Error("Invalid work-composition JSON: " + ex.Message)
