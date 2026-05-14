module Client.AuditLog

open System
open System.Collections.Concurrent
open Common

type AuditEvent =
    { Timestamp: DateTimeOffset
      CommandName: string
      ProjectName: string option
      Permissions: PermissionLevel list
      Paths: string list
      Executable: string option
      Args: string list
      ResultSummary: string }

module private Store =
    let events = ConcurrentQueue<AuditEvent>()
    let maxEvents = 1000

let record event =
    Store.events.Enqueue event

    while Store.events.Count > Store.maxEvents do
        let mutable ignored = Unchecked.defaultof<AuditEvent>
        Store.events.TryDequeue(&ignored) |> ignore

let list () = Store.events.ToArray() |> Array.toList

let recordCommand commandName projectName permissions paths executable args resultSummary =
    { Timestamp = DateTimeOffset.UtcNow
      CommandName = commandName
      ProjectName = projectName
      Permissions = permissions
      Paths = paths
      Executable = executable
      Args = args
      ResultSummary = resultSummary }
    |> record
