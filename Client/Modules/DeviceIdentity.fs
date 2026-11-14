namespace Client

open System
open System.IO

module DeviceIdentity =
    let private deviceIdPath () =
        let root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
        let directory = Path.Combine(root, "Jarvis")
        Path.Combine(directory, "device-id")

    /// Forensic reads must not create, repair or follow a changed device ID.
    /// Missing or invalid identity makes historical evidence unverifiable.
    let tryReadExistingAt path =
        try
            if not (File.Exists path)
               || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)
               || FileInfo(path).Length > 128L then None
            else
                let existing = File.ReadAllText(path).Trim()
                match Guid.TryParse existing with
                | true, parsed when parsed <> Guid.Empty -> Some(parsed.ToString("D"))
                | _ -> None
        with _ -> None

    let tryReadExisting () =
        tryReadExistingAt (deviceIdPath())

    let getOrCreate () =
        let path = deviceIdPath ()
        Directory.CreateDirectory(Path.GetDirectoryName(path)) |> ignore

        if File.Exists(path) then
            let existing = File.ReadAllText(path).Trim()

            match Guid.TryParse(existing) with
            | true, parsed -> parsed.ToString("D")
            | false, _ ->
                let created = Guid.NewGuid().ToString("D")
                File.WriteAllText(path, created)
                created
        else
            let created = Guid.NewGuid().ToString("D")
            File.WriteAllText(path, created)
            created
