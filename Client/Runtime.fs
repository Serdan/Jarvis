namespace Client

open System.Net.Http
open System.Threading.Tasks
open Client.IO
open Client.ConsoleTui
open Common

type Runtime(root: string, tui: ConsoleTui) =
    member _.httpClient = new HttpClient()
    member _.Tui = tui

    new(root: string) = Runtime(root, ConsoleTui())

    interface ProjectIO with
        member _.Project = ProjectOperations.impl (ProjectDirectory root)

    interface FileIO with
        member _.File = FileOperations.impl

    interface WebIO with
        member _.Browser = WebOperations.impl

    interface PermissionIO with
        member _.PromptPermission command request = tui.PromptPermission command request
