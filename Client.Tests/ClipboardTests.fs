module ClipboardTests

open System
open Client
open FsUnitTyped
open NUnit.Framework

[<Test>]
let ``linux clipboard candidates prefer KDE Klipper before external Wayland helpers`` () =
    if OperatingSystem.IsLinux() then
        let candidates = Clipboard.commandCandidates()
        candidates.Head.Executable |> shouldEqual "qdbus6"
        candidates.Head.Input |> shouldEqual Clipboard.FinalArgument
        candidates.Head.Args
        |> shouldEqual
            [ "org.kde.klipper"
              "/klipper"
              "org.kde.klipper.klipper.setClipboardContents" ]
        candidates[1].Executable |> shouldEqual "wl-copy"
