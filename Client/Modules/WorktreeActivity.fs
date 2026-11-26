module internal Client.WorktreeActivity

open System
open System.IO
open System.Threading
open System.Collections.Generic

/// A declared checkout resource, independent of the public job summary.
/// Unresolved selectors are resolved only when a removal is being considered.
type Scope =
    | NoCheckout
    | Checkout of ProjectPaths.ProjectTarget
    | Paths of string list
    | ProjectWorktrees of ProjectPaths.ProjectId
    | Unknown of ProjectPaths.ProjectId option

type RemovalTarget =
    { Project: ProjectPaths.ProjectId
      Path: string
      ResolveCheckout: ProjectPaths.ProjectTarget -> Client.Result<string> }

let private gate = obj()
let private uses = Dictionary<string, Scope>(StringComparer.Ordinal)
let private removals = Dictionary<string, RemovalTarget>(StringComparer.Ordinal)
let private current = AsyncLocal<string>()

let private lease dispose =
    { new IDisposable with member _.Dispose() = dispose() }

let enter id =
    let prior = current.Value
    current.Value <- id
    lease (fun () -> current.Value <- prior)

let private comparison =
    if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase else StringComparison.Ordinal

let private overlaps (left: string) (right: string) =
    try
        if not (Path.IsPathFullyQualified left && Path.IsPathFullyQualified right) then true
        else
            let normalize path = Path.TrimEndingDirectorySeparator(Path.GetFullPath path)
            let a, b = normalize left, normalize right
            let contains (root: string) (path: string) =
                let prefix = if Path.EndsInDirectorySeparator root then root else root + string Path.DirectorySeparatorChar
                path.Equals(root, comparison) || path.StartsWith(prefix, comparison)
            contains a b || contains b a
    with _ -> true

let private conflicts target = function
    | NoCheckout -> false
    | Paths paths -> List.isEmpty paths || List.exists (overlaps target.Path) paths
    | ProjectWorktrees project -> target.Project = project
    | Unknown project -> project |> Option.forall ((=) target.Project)
    | Checkout selected ->
        match target.ResolveCheckout selected with
        | Ok path -> overlaps target.Path path
        | Error _ -> true // No resolved filesystem identity: cannot prove disjointness.

let private removalConflict scope =
    removals.Values |> Seq.exists (fun target -> conflicts target scope)

/// Register before scheduling a tool or starting a process; fast and queued
/// tools count just like promoted tools. The lease ends only at terminal execution.
let admit id scope =
    lock gate (fun () ->
        if removalConflict scope then
            Error(Client.ValidationError "A conflicting worktree removal is in progress.")
        elif uses.ContainsKey id then
            Error(Client.ContextError "Duplicate worktree activity identifier.")
        else
            uses.Add(id, scope)
            Ok(lease (fun () -> lock gate (fun () -> uses.Remove id |> ignore))))

/// Capture the exact source root once normal command preparation resolves it.
/// The unresolved claim already prevents removal from racing this resolution.
let resolveCurrent sourceRoot =
    lock gate (fun () ->
        match current.Value with
        | null -> Ok ()
        | id ->
            match uses.TryGetValue id with
            | true, Checkout _ ->
                let scope = Paths [ sourceRoot ]
                if removalConflict scope then
                    Error(Client.ValidationError "A conflicting worktree removal is in progress.")
                else
                    uses[id] <- scope
                    Ok ()
            | _ -> Ok ())

/// Direct dispatch has the same admission boundary as promoted dispatch.
let admitDirect scope =
    if not (String.IsNullOrEmpty current.Value) then Ok(lease ignore)
    else
        let id = Guid.NewGuid().ToString("N")
        admit id scope
        |> Result.map (fun admission ->
            let context = enter id
            lease (fun () -> context.Dispose(); admission.Dispose()))

let private blockers target =
    seq {
        for KeyValue(id, scope) in uses do
            if id <> current.Value && conflicts target scope then yield id
        for KeyValue(id, removal) in removals do
            if id <> current.Value && overlaps target.Path removal.Path then yield id
    }
    |> Set.ofSeq
    |> Set.count

let count target = lock gate (fun () -> blockers target)

/// Check and reserve under one gate, then release the gate while Git executes.
let reserveRemoval target =
    lock gate (fun () ->
        let n = blockers target
        if n <> 0 then Error n
        else
            let id = if String.IsNullOrEmpty current.Value then Guid.NewGuid().ToString("N") else current.Value
            removals.Add(id, target)
            Ok(lease (fun () -> lock gate (fun () -> removals.Remove id |> ignore))))
