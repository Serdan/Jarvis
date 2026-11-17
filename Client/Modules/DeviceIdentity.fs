namespace Client

open System
open System.IO
open System.Text
open Common.SignalR

/// A durable machine identity is not a transport address. Every process gets
/// its own route, retained across reconnects but never reused on a restart.
module DeviceIdentity =
    type ClientInstanceIdentity =
        { MachineId: string
          InstallationId: string
          InstanceId: string
          RouteId: string }

    let private identityPath () =
        let root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
        Path.Combine(root, "Jarvis", "device-id")

    // Local application data is deliberately separate from roaming/config
    // data: copying just device-id to another installation is detectable.
    let private installationPath () =
        let root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        Path.Combine(root, "Jarvis", "installation-id")

    let private readValidated (path: string) =
        if File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) then
            invalidOp $"Identity file is a symbolic link: {path}"
        let value = File.ReadAllText(path, UTF8Encoding(false, true)).Trim()
        match Guid.TryParse(value) with
        | true, guid when guid <> Guid.Empty -> guid.ToString("D")
        | _ ->
            invalidOp $"Invalid Jarvis identity in {path}. Inspect and repair this file explicitly; it will not be overwritten automatically."

    /// Atomic create-or-read: no process can observe a partially written
    /// permanent identity, and a loser of the first-start race reads the winner.
    let getOrCreateAt path =
        let folder = Path.GetDirectoryName(Path.GetFullPath(path))
        Directory.CreateDirectory(folder) |> ignore
        use _guard = SharedFileLock.acquire (path + ".lock") (TimeSpan.FromSeconds 30.)
        if not (File.Exists(path)) then
            let temporary = Path.Combine(folder, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp")
            let writeTemporary () =
                use stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                use writer = new StreamWriter(stream, UTF8Encoding(false))
                writer.Write(Guid.NewGuid().ToString("D"))
                writer.Flush()
                stream.Flush(true)
            try
                writeTemporary() // Close handles before rename (required on Windows).
                try File.Move(temporary, path)
                with :? IOException when File.Exists(path) -> ()
            finally
                if File.Exists(temporary) then File.Delete(temporary)
        readValidated path

    /// Legacy stable machine identifier; keep the original device-id file.
    let getOrCreate () = getOrCreateAt (identityPath ())

    let createInstanceAt machinePath localInstallationPath =
        let machine = getOrCreateAt machinePath
        let installation = getOrCreateAt localInstallationPath
        let instance = Guid.NewGuid().ToString("D")
        { MachineId = machine
          InstallationId = installation
          InstanceId = instance
          RouteId = DeviceRouteIdentity.create machine installation instance }

    let createInstance () = createInstanceAt (identityPath ()) (installationPath ())
