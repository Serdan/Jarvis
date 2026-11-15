namespace Client

open System
open System.IO

/// Centralizes Client-owned user storage. Production always uses OS known
/// folders. Client.Tests may install a process-wide, scoped disposable root
/// before creating any Client persistence objects; no environment variable
/// alone can activate this override.
module internal ClientKnownFolders =
    let private gate = obj()
    let mutable private testRoot: string option = None

    let private resolve child knownFolder =
        lock gate (fun () ->
            match testRoot with
            | Some root -> Path.Combine(root, child)
            | None -> Environment.GetFolderPath knownFolder)

    let applicationData () =
        resolve "config" Environment.SpecialFolder.ApplicationData

    let localApplicationData () =
        resolve "data" Environment.SpecialFolder.LocalApplicationData

    let userProfile () =
        resolve "home" Environment.SpecialFolder.UserProfile

    /// The NUnit SetUpFixture validates the root, all ancestors and required
    /// layout before installation. The override is internal/friend-visible,
    /// not a production configuration knob. It cannot be installed twice.
    let installForTests (root: string) : IDisposable =
        if String.IsNullOrWhiteSpace root || not (Path.IsPathFullyQualified root) then
            invalidArg (nameof root) "Test root must be an absolute path."
        let fullRoot = Path.GetFullPath root
        lock gate (fun () ->
            if testRoot.IsSome then invalidOp "Client test known folders are already overridden."
            testRoot <- Some fullRoot)
        let mutable disposed = false
        { new IDisposable with
            member _.Dispose() =
                lock gate (fun () ->
                    if not disposed then
                        disposed <- true
                        testRoot <- None) }
