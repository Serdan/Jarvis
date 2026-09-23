namespace Client

open System
open System.IO

module DeviceIdentity =
    let private deviceIdPath () =
        let root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
        let directory = Path.Combine(root, "Jarvis")
        Directory.CreateDirectory(directory) |> ignore
        Path.Combine(directory, "device-id")

    let getOrCreate () =
        let path = deviceIdPath ()

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
