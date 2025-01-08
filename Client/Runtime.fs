namespace Client

open System.Net.Http
open Client.IO

type Runtime(root: string) =
    member _.httpClient = new HttpClient()

    interface ProjectIO with
        member this.Project = ProjectOperations.impl (ProjectDirectory root)

    interface FileIO with
        member this.File = FileOperations.impl

    interface WebIO with
        member this.Browser = WebOperations.impl
