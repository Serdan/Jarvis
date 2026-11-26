module Client.Worktrees

open System
open System.IO
open System.Text.RegularExpressions
open Common
open Client.Effect
open Client.IO
open FsToolkit.ErrorHandling
open Kehlet.FSharp.IO
open Kehlet.FSharp.IO.Effect.Operators

type WorktreeInfo =
    { Path: string
      MainPath: string
      Head: string
      Branch: string option
      IsMain: bool
      IsDetached: bool
      IsLocked: bool
      IsPrunable: bool
      IsClean: bool option }

let private git projectName args =
    effect {
        // Repository-wide management commands must not execute from an
        // incidental selected linked checkout.
        let! selected = ProjectPaths.ProjectTarget.parse projectName |> Effect.ofResult
        return! ClientShell.runCommand
            { ProjectName = ProjectPaths.ProjectTarget.projectName selected
              Executable = "git"
              Args = args
              Reason = None
              WorkingDirectory = None
              TimeoutSeconds = Some 20
              MaxOutputBytes = Some(256 * 1024) }
    }

/// Decode Git's porcelain format. A blank line separates records.
let parsePorcelain (mainPath: string) (output: string) : Result<WorktreeInfo list, string> =
    let normalized = output.Replace("\r\n", "\n").Replace('\000', '\n')
    let entries = normalized.Split([| "\n\n" |], StringSplitOptions.RemoveEmptyEntries)
    let mainPath = Path.GetFullPath(mainPath).TrimEnd(Path.DirectorySeparatorChar)
    let parse (record: string) =
        let lines = record.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        let value (prefix: string) =
            lines
            |> Array.tryPick (fun line ->
                if line.StartsWith(prefix, StringComparison.Ordinal) then
                    Some(line.Substring(prefix.Length))
                else None)
        match value "worktree ", value "HEAD " with
        | Some path, Some head when not (String.IsNullOrWhiteSpace path) ->
            try
                let path = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar)
                Ok
                    { Path = path
                      MainPath = mainPath
                      Head = head
                      Branch = value "branch " |> Option.map (fun s -> s.Replace("refs/heads/", ""))
                      IsMain = String.Equals(path, mainPath, (if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase else StringComparison.Ordinal))
                      IsDetached = Array.contains "detached" lines
                      IsLocked = lines |> Array.exists (fun line -> line = "locked" || line.StartsWith("locked ", StringComparison.Ordinal))
                      IsPrunable = lines |> Array.exists (fun line -> line = "prunable" || line.StartsWith("prunable ", StringComparison.Ordinal))
                      IsClean = None }
            with ex -> Error($"Invalid Git worktree path: {ex.Message}")
        | _ -> Error "Malformed git worktree --porcelain record."
    entries
    |> Array.fold (fun state entry ->
        match state, parse entry with
        | Ok items, Ok item -> Ok(item :: items)
        | Error e, _ -> Error e
        | _, Error e -> Error e) (Ok [])
    |> Result.map List.rev

// Internal registration discovery does not inspect unrelated checkout files.
let private listRegistrations projectName =
    effect {
        let! result = git projectName [ "worktree"; "list"; "--porcelain" ]
        if result.ExitCode <> 0 || result.TimedOut || result.Truncated then
            return! Client.ValidationError($"Cannot inspect Git worktrees: {result.StdErr}") |> Effect.ofError
        else
            let firstPath =
                result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                |> Array.tryPick (fun line ->
                    if line.StartsWith("worktree ", StringComparison.Ordinal) then Some(line.Substring(9))
                    else None)
            return!
                match firstPath with
                | None -> Client.ValidationError "Git did not return a main worktree." |> Effect.ofError
                | Some mainPath ->
                    match parsePorcelain mainPath result.StdOut with
                    | Ok items -> fun _ -> Ok items
                    | Error message -> Client.ValidationError message |> Effect.ofError
    }

let private statusFor projectName (entry: WorktreeInfo) rt =
    if not (Directory.Exists entry.Path) then entry
    else
        match git projectName [ "-C"; entry.Path; "status"; "--porcelain" ] rt with
        | Ok status when status.ExitCode = 0 && not status.TimedOut && not status.Truncated ->
            { entry with IsClean = Some(String.IsNullOrWhiteSpace status.StdOut) }
        | _ -> entry

let listWorktrees (cmd: ListWorktreesCommand) =
    effect {
        let! entries = listRegistrations cmd.ProjectName
        return! fun rt -> entries |> List.map (fun entry -> statusFor cmd.ProjectName entry rt) |> Ok
    }

let private getRegistration projectName worktreePath =
    effect {
        let! entries = listRegistrations projectName
        let target =
            try Some(Path.GetFullPath(worktreePath: string).TrimEnd(Path.DirectorySeparatorChar))
            with _ -> None
        let comparison =
            if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase else StringComparison.Ordinal
        return!
            match target |> Option.bind (fun path -> entries |> List.tryFind (fun entry -> String.Equals(entry.Path, path, comparison))) with
            | Some entry -> fun _ -> Ok entry
            | None -> Client.NotFoundError "Worktree is not registered with this project." |> Effect.ofError
    }

let getWorktree (cmd: GetWorktreeCommand) =
    effect {
        let! entry = getRegistration cmd.ProjectName cmd.WorktreePath
        return! fun rt -> Ok(statusFor cmd.ProjectName entry rt)
    }

let private safeName name =
    not (String.IsNullOrWhiteSpace name)
    && name <> "."
    && name <> ".."
    && Regex.IsMatch(name, "^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$")

let private safeRef name =
    not (String.IsNullOrWhiteSpace name)
    && not (name.StartsWith("-", StringComparison.Ordinal))
    && not (name.Contains("..", StringComparison.Ordinal))
    && not (name.Contains('\000'))

let createWorktree (cmd: CreateWorktreeCommand) =
    effect {
        if not (safeName cmd.Name) then
            return! Client.ValidationError "Worktree name must be one safe flat directory component." |> Effect.ofError
        elif not (safeRef cmd.Branch && safeRef cmd.BaseRef) then
            return! Client.ValidationError "Invalid branch or base ref." |> Effect.ofError
        else
            let! entries = listRegistrations cmd.ProjectName
            let main = entries |> List.find (fun item -> item.IsMain) |> fun item -> DirectoryInfo(item.Path)
            let parent = Path.Combine(main.Parent.FullName, "." + main.Name + "-worktrees")
            let path = Path.Combine(parent, cmd.Name)
            if Directory.Exists(path) || File.Exists(path) || (Directory.Exists(parent) && (File.GetAttributes(parent).HasFlag(FileAttributes.ReparsePoint))) then
                return! Client.ValidationError "Worktree target already exists or the container is a link." |> Effect.ofError
            else
                let! result = git cmd.ProjectName [ "worktree"; "add"; "-b"; cmd.Branch; path; cmd.BaseRef ]
                if result.ExitCode <> 0 || result.TimedOut then
                    return! Client.ValidationError($"Git worktree creation failed: {result.StdErr}") |> Effect.ofError
                else
                    return! getWorktree { ProjectName = cmd.ProjectName; WorktreePath = path }
    }

type WorktreeRemovalInspection =
    { Worktree: WorktreeInfo
      TargetRef: string
      IsClean: bool
      IsIntegrated: bool
      RunningJobCount: int
      CanRemove: bool
      Reasons: string list }

let private removalTarget projectName (entry: WorktreeInfo) =
    effect {
        let! selected = ProjectPaths.ProjectTarget.parse projectName |> Effect.ofResult
        return! fun rt ->
            Ok
                ({ Project = ProjectPaths.ProjectTarget.projectId selected
                   Path = entry.Path
                   ResolveCheckout = fun target ->
                       ProjectPaths.resolveProjectContextForTarget target rt
                       |> Result.map (fun project ->
                           let (FolderPath root) = ProjectPaths.ResolvedProject.sourceRoot project
                           root) } : WorktreeActivity.RemovalTarget)
    }

let private inspectEntry (cmd: InspectWorktreeRemovalCommand) (entry: WorktreeInfo) resource =
    effect {
        let! target =
            match cmd.TargetRef with
            | Some r when safeRef r -> fun _ -> Ok r
            | Some _ -> Client.ValidationError "Invalid integration target ref." |> Effect.ofError
            | None ->
                effect {
                    let! branch = git cmd.ProjectName [ "-C"; entry.MainPath; "branch"; "--show-current" ]
                    return
                        if branch.ExitCode = 0 && not branch.TimedOut && not branch.Truncated then branch.StdOut.Trim()
                        else ""
                }
        let! status = git cmd.ProjectName [ "-C"; entry.Path; "status"; "--porcelain" ]
        let! integrated = git cmd.ProjectName [ "merge-base"; "--is-ancestor"; entry.Head; target ]
        let jobs = WorktreeActivity.count resource
        let clean =
            if status.ExitCode = 0 && not status.TimedOut && not status.Truncated then
                Some(String.IsNullOrWhiteSpace status.StdOut)
            else None
        let isClean = clean = Some true
        let isIntegrated = not (String.IsNullOrWhiteSpace target) && integrated.ExitCode = 0 && not integrated.TimedOut
        let reasons =
            [ if entry.IsMain then "Main worktree"
              if entry.IsLocked then "Worktree is locked"
              if entry.IsPrunable then "Worktree registration is prunable"
              if not isClean then "Worktree has changes or cannot be inspected"
              if not isIntegrated then "HEAD is not verified integrated into target"
              if jobs <> 0 then "Jarvis has active jobs using this worktree or with unresolved resource provenance" ]
        return
            { Worktree = { entry with IsClean = clean }
              TargetRef = target
              IsClean = isClean
              IsIntegrated = isIntegrated
              RunningJobCount = jobs
              CanRemove = reasons.IsEmpty
              Reasons = reasons }
    }

let inspectRemoval (cmd: InspectWorktreeRemovalCommand) =
    fun rt ->
        match WorktreeActivity.admitDirect (WorktreeActivity.Paths [ cmd.WorktreePath ]) with
        | Error error -> Error error
        | Ok admission ->
            use _admission = admission
            (effect {
                let! entry = getRegistration cmd.ProjectName cmd.WorktreePath
                let! resource = removalTarget cmd.ProjectName entry
                return! inspectEntry cmd entry resource
            }) rt

let private removeReserved (cmd: RemoveWorktreeCommand) resource =
    effect {
        // Registration and all Git preconditions must be read again while
        // holding the exclusive removal reservation.
        let! entry = getRegistration cmd.ProjectName cmd.WorktreePath
        let! inspection =
            inspectEntry
                { ProjectName = cmd.ProjectName
                  WorktreePath = cmd.WorktreePath
                  TargetRef = cmd.TargetRef }
                entry resource
        if not inspection.CanRemove then
            return! Client.ValidationError(String.concat "; " inspection.Reasons) |> Effect.ofError
        else
            let! removal = git cmd.ProjectName [ "worktree"; "remove"; inspection.Worktree.Path ]
            if removal.ExitCode <> 0 || removal.TimedOut then
                return! Client.ValidationError($"Worktree removal failed: {removal.StdErr}") |> Effect.ofError
            else
                let parent = Directory.GetParent(inspection.Worktree.Path)
                let main = DirectoryInfo(inspection.Worktree.MainPath)
                let expected = Path.Combine(main.Parent.FullName, "." + main.Name + "-worktrees")
                let () =
                    if parent <> null && parent.FullName = expected && Directory.Exists(expected) && Array.isEmpty (Directory.GetFileSystemEntries(expected)) then
                        Directory.Delete(expected)
                return inspection.Worktree
    }

let removeWorktree (cmd: RemoveWorktreeCommand) =
    fun rt ->
        match WorktreeActivity.admitDirect (WorktreeActivity.Paths [ cmd.WorktreePath ]) with
        | Error error -> Error error
        | Ok admission ->
            use _admission = admission
            let resource =
                (effect {
                    let! entry = getRegistration cmd.ProjectName cmd.WorktreePath
                    return! removalTarget cmd.ProjectName entry
                }) rt
            match resource with
            | Error error -> Error error
            | Ok resource ->
                match WorktreeActivity.reserveRemoval resource with
                | Error count ->
                    Error(Client.ValidationError($"Jarvis has {count} active job(s) using this worktree or with unresolved resource provenance."))
                | Ok reservation ->
                    use _reservation = reservation
                    removeReserved cmd resource rt
