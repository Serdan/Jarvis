module Client.ProjectColorAssignments

open System
open System.Collections.Generic

type ProjectColorAssignments<'T>(palette: 'T array) =
    let assignments = Dictionary<string, 'T>(StringComparer.Ordinal)
    let mutable nextIndex = 0

    do
        if isNull palette || palette.Length = 0 then
            invalidArg (nameof palette) "Project color palette must contain at least one color."

    member _.Get(projectName: string) =
        lock assignments (fun () ->
            match assignments.TryGetValue projectName with
            | true, color -> color
            | false, _ ->
                let color = palette[nextIndex % palette.Length]
                nextIndex <- nextIndex + 1
                assignments.Add(projectName, color)
                color)
