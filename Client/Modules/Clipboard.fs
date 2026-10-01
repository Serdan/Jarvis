module Client.Clipboard

open System
open System.Diagnostics

type ClipboardInput =
    | StandardInput
    | FinalArgument

type ClipboardCommand =
    { Executable: string
      Args: string list
      Input: ClipboardInput }

let commandCandidates () =
    if OperatingSystem.IsWindows() then
        [ { Executable = "clip.exe"; Args = []; Input = StandardInput } ]
    elif OperatingSystem.IsMacOS() then
        [ { Executable = "pbcopy"; Args = []; Input = StandardInput } ]
    else
        [ { Executable = "qdbus6"
            Args =
                [ "org.kde.klipper"
                  "/klipper"
                  "org.kde.klipper.klipper.setClipboardContents" ]
            Input = FinalArgument }
          { Executable = "wl-copy"; Args = []; Input = StandardInput }
          { Executable = "xclip"; Args = [ "-selection"; "clipboard" ]; Input = StandardInput }
          { Executable = "xsel"; Args = [ "--clipboard"; "--input" ]; Input = StandardInput } ]

let private tryCommand (text: string) (candidate: ClipboardCommand) =
    try
        let startInfo = ProcessStartInfo()
        startInfo.FileName <- candidate.Executable
        startInfo.UseShellExecute <- false
        startInfo.RedirectStandardInput <- candidate.Input = StandardInput
        startInfo.RedirectStandardError <- true
        startInfo.CreateNoWindow <- true

        candidate.Args
        |> List.iter startInfo.ArgumentList.Add

        match candidate.Input with
        | FinalArgument -> startInfo.ArgumentList.Add text
        | StandardInput -> ()

        use child = new Process()
        child.StartInfo <- startInfo

        if not (child.Start()) then
            Error $"{candidate.Executable} did not start."
        else
            match candidate.Input with
            | StandardInput ->
                child.StandardInput.Write text
                child.StandardInput.Close()
            | FinalArgument -> ()

            if child.WaitForExit(500) then
                if child.ExitCode = 0 then
                    Ok candidate.Executable
                else
                    let error = child.StandardError.ReadToEnd().Trim()
                    if String.IsNullOrWhiteSpace error then
                        Error $"{candidate.Executable} exited with code {child.ExitCode}."
                    else
                        Error $"{candidate.Executable}: {error}"
            else
                // X11 clipboard helpers may intentionally remain alive as selection owners.
                Ok candidate.Executable
    with ex ->
        Error $"{candidate.Executable}: {ex.Message}"

let copyText text =
    let rec loop errors candidates =
        match candidates with
        | [] ->
            let detail =
                errors
                |> List.rev
                |> String.concat "; "

            Error(
                if String.IsNullOrWhiteSpace detail then
                    "No clipboard helper is available."
                else
                    $"No clipboard helper succeeded: {detail}")
        | candidate :: rest ->
            match tryCommand text candidate with
            | Ok provider -> Ok provider
            | Error error -> loop (error :: errors) rest

    loop [] (commandCandidates())
