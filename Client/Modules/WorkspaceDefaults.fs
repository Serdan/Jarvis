namespace Client

open System
open System.IO
open System.Runtime.InteropServices

module WorkspaceDefaults =
    let choose isLinux userHome directoryExists =
        if isLinux && not (String.IsNullOrWhiteSpace userHome) then
            let projects = Path.Combine(userHome, "Projects")
            if directoryExists projects then projects else userHome
        else
            ""

    let current () =
        let userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        choose
            (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            userHome
            Directory.Exists
