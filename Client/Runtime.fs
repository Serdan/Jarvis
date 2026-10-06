namespace Client

open System.Net.Http
open System.Threading.Tasks
open Client.IO
open Client.ConsoleTui
open Common

type Runtime(root: string, tui: ConsoleTui, trustLevel: TrustLevel, maxImportBytes: int64) =
    member _.httpClient = new HttpClient()
    member _.Tui = tui
    member _.MaxImportBytes = maxImportBytes

    new(root: string, tui: ConsoleTui, trustLevel: TrustLevel) =
        Runtime(root, tui, trustLevel, AgentProtocol.defaultMaxImportBytes)

    new(root: string) = Runtime(root, ConsoleTui(), PartialTrust, AgentProtocol.defaultMaxImportBytes)

    interface ProjectIO with
        member _.Project = ProjectOperations.impl (ProjectDirectory root)

    interface FileIO with
        member _.File = FileOperations.impl

    interface WebIO with
        member _.Browser = WebOperations.impl

    interface PermissionIO with
        member _.TrustLevel = trustLevel
        member _.PromptPermission command request = tui.PromptPermission command request
