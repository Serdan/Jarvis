#r "../../../Client/bin/Release/net11.0/JarvisClient.dll"
open System
open System.IO
open Client.ActivityLog

// Genuine compiled production Store; private scratch only. Linux/macOS path
// semantics intentionally fail closed. Run after the full Release baseline.
if not (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) then
    failwith "This offline Unix permission oracle is not a Windows ACL test"

let root = Path.Combine(Path.GetTempPath(), "jarvis-w22-activity-control-" + Guid.NewGuid().ToString("N"))
let path = Path.Combine(root, "activity")
let sentinel = Path.Combine(root, "sentinel.txt")
let seen = Path.Combine(path, "agent-seen.json")
Directory.CreateDirectory(root) |> ignore
let privateDir = UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
let privateFile = UnixFileMode.UserRead ||| UnixFileMode.UserWrite
try
    File.WriteAllText(sentinel, "unchanged")
    let stamp = DateTimeOffset.UtcNow
    use store = new Store({ DirectoryPath = path; RetentionDays = 14; MaxFileBytes = 4096L })
    store.SaveAgentSeen [ "Jarvis::Worker 22", stamp ]
    let reloaded = store.LoadAgentSeen() |> Map.ofList
    if reloaded["Jarvis::Worker 22"] <> stamp then failwith "valid private activity roundtrip lost state"
    if File.GetUnixFileMode(path) <> privateDir then failwith "private directory mode mismatch"
    if File.GetUnixFileMode(seen) <> privateFile then failwith "private activity file mode mismatch"
    File.Delete(seen)
    File.CreateSymbolicLink(seen, sentinel) |> ignore
    let readRejected =
        try store.LoadAgentSeen() |> ignore; false
        with :? UnauthorizedAccessException -> true
    let saveRejected =
        try store.SaveAgentSeen [ "Jarvis::Worker 22", stamp.AddMinutes(1.) ]; false
        with :? IOException -> true
    if not readRejected then failwith "unsafe link read was not rejected"
    if not saveRejected then failwith "unsafe link save was not rejected"
    if File.ReadAllText(sentinel) <> "unchanged" then failwith "negative symlink mutated target"
    printfn "PRODUCTION_ACTIVITY_CONTROL_PASS valid=1 symlink_read_rejected=1 symlink_write_rejected=1 sentinel_intact=1"
finally
    Directory.Delete(root, true)
