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
    ClientShell.runCommand
        { ProjectName = projectName
          Executable = "git"
          Args = args
          Reason = None
          WorkingDirectory = None
          TimeoutSeconds = Some 20
          MaxOutputBytes = Some(256 * 1024) }

/// Decode Git's porcelain format. A blank line separates records.
let parsePorcelain (mainPath: string) (output: string) : Result<WorktreeInfo list, string> =
    let normalized = output.Replace("\r\n", "\n").Replace('\000', '\n')
    let entries = normalized.Split([| "\n\n" |], StringSplitOptions.RemoveEmptyEntries)
    let mainPath = Path.GetFullPath(mainPath).TrimEnd(Path.DirectorySeparatorChar)
    let parse (record: string) =
        let lines = record.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        let value prefix =
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

let listWorktrees (cmd: ListWorktreesCommand) =
    effect {
        let! result = git cmd.ProjectName [ "worktree"; "list"; "--porcelain" ]
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
                    | Ok items ->
                        fun rt ->
                            items
                            |> List.map (fun item ->
                                if not (Directory.Exists item.Path) then item
                                else
                                    match git cmd.ProjectName [ "-C"; item.Path; "status"; "--porcelain" ] rt with
                                    | Ok status when status.ExitCode = 0 && not status.TimedOut && not status.Truncated ->
                                        { item with IsClean = Some(String.IsNullOrWhiteSpace status.StdOut) }
                                    | _ -> item)
                            |> Ok
                    | Error message -> Client.ValidationError message |> Effect.ofError
    }

let getWorktree (cmd: GetWorktreeCommand) =
    effect {
        let! entries = listWorktrees { ProjectName = cmd.ProjectName }
        let target =
            try Some(Path.GetFullPath(cmd.WorktreePath).TrimEnd(Path.DirectorySeparatorChar))
            with _ -> None
        let comparison =
            if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase else StringComparison.Ordinal
        return!
            match target |> Option.bind (fun path -> entries |> List.tryFind (fun entry -> String.Equals(entry.Path, path, comparison))) with
            | Some entry -> fun _ -> Ok entry
            | None -> Client.NotFoundError "Worktree is not registered with this project." |> Effect.ofError
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
            let! entries = listWorktrees { ProjectName = cmd.ProjectName }
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

let inspectRemoval (cmd: InspectWorktreeRemovalCommand) =
    effect {
        let! entry = getWorktree { ProjectName = cmd.ProjectName; WorktreePath = cmd.WorktreePath }
        let! target =
            match cmd.TargetRef with
            | Some r when safeRef r -> fun _ -> Ok r
            | Some _ -> Client.ValidationError "Invalid integration target ref." |> Effect.ofError
            | None ->
                effect {
                    let! entries = listWorktrees { ProjectName = cmd.ProjectName }
                    let main = entries |> List.find (fun item -> item.IsMain)
                    let! branch = git cmd.ProjectName [ "-C"; main.Path; "branch"; "--show-current" ]
                    return branch.StdOut.Trim()
                }
        let! status = git cmd.ProjectName [ "-C"; entry.Path; "status"; "--porcelain" ]
        let! integrated = git cmd.ProjectName [ "merge-base"; "--is-ancestor"; entry.Head; target ]
        let! allJobs = JobManager.listJobs { ProjectName = None; IncludeCompleted = false }
        let jobs =
            allJobs.Jobs |> List.filter (fun job ->
                job.ProjectName = cmd.ProjectName
                || job.ProjectName.StartsWith(cmd.ProjectName + "::", StringComparison.Ordinal))
        let isClean = status.ExitCode = 0 && not status.TimedOut && not status.Truncated && String.IsNullOrWhiteSpace status.StdOut
        let isIntegrated = not (String.IsNullOrWhiteSpace target) && integrated.ExitCode = 0 && not integrated.TimedOut
        let reasons =
            [ if entry.IsMain then "Main worktree"
              if entry.IsLocked then "Worktree is locked"
              if entry.IsPrunable then "Worktree registration is prunable"
              if not isClean then "Worktree has changes or cannot be inspected"
              if not isIntegrated then "HEAD is not verified integrated into target"
              if not jobs.IsEmpty then "Jarvis project has active jobs" ]
        return
            { Worktree = entry
              TargetRef = target
              IsClean = isClean
              IsIntegrated = isIntegrated
              RunningJobCount = jobs.Length
              CanRemove = reasons.IsEmpty
              Reasons = reasons }
    }

let removeWorktree (cmd: RemoveWorktreeCommand) =
    effect {
        let! inspection =
            inspectRemoval
                { ProjectName = cmd.ProjectName
                  WorktreePath = cmd.WorktreePath
                  TargetRef = cmd.TargetRef }
        if not inspection.CanRemove then
            return! Client.ValidationError(String.concat "; " inspection.Reasons) |> Effect.ofError
        else
            let! removal = git cmd.ProjectName [ "worktree"; "remove"; inspection.Worktree.Path ]
            if removal.ExitCode <> 0 || removal.TimedOut then
                return! Client.ValidationError($"Worktree removal failed: {removal.StdErr}") |> Effect.ofError
            else
                let parent = Directory.GetParent(inspection.Worktree.Path)
                let! entries = listWorktrees { ProjectName = cmd.ProjectName }
                let main = entries |> List.find (fun item -> item.IsMain) |> fun item -> DirectoryInfo(item.Path)
                let expected = Path.Combine(main.Parent.FullName, "." + main.Name + "-worktrees")
                if parent <> null && parent.FullName = expected && Directory.Exists(expected) && Array.isEmpty (Directory.GetFileSystemEntries(expected)) then
                    Directory.Delete(expected)
                return inspection.Worktree
    }
