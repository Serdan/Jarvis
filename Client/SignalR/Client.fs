module Client.SignalR.Client

open System.Text.Json
open System.Threading.Tasks
open Client
open Client.Effect
open Client.IO
open Common
open FsToolkit.ErrorHandling

let receiveMessage message = printfn $"%s{message}"

let private serialize<'a> (value: 'a) =
    try
        JsonSerializer.Serialize value |> Ok
    with e ->
        e |> ExceptionError |> Error

let private serialize'<'a> = Result.bind serialize<'a> >> ValueTask<_>

let receiveCommand rt (command: AgentCommand) =
    task {
        let! response =
            match command with
            | ListProjectsCommand -> rt |> ProjectBrowser.listProjects |> serialize'
            | OpenProjectCommand cmd -> rt |> ProjectBrowser.openProject cmd |> serialize'
            | ListDirectoryCommand cmd -> rt |> ProjectBrowser.listDirectory cmd |> serialize'
            | ReadFileCommand cmd -> rt |> ProjectBrowser.readFile cmd |> serialize'
            | ReadFilesCommand cmd -> rt |> ProjectBrowser.readFiles cmd |> serialize'
            | WriteFileCommand cmd -> rt |> ProjectBrowser.writeFile cmd |> serialize'
            | ReplaceSectionCommand cmd -> rt |> ProjectBrowser.replaceSection cmd |> serialize'
            | ReplaceCommand cmd -> rt |> ProjectBrowser.replaceText cmd |> serialize'
            | InsertBeforeCommand cmd -> rt |> ProjectBrowser.insertBefore cmd |> serialize'
            | InsertAfterCommand cmd -> rt |> ProjectBrowser.insertAfter cmd |> serialize'
            | LoadPageCommand cmd ->
                cmd.Url
                |> Url
                |> WebOperations.load
                |> Task.map serialize
                |> ValueTask<Result<_, _>>

        match response with
        | Ok _ -> printfn "Command executed. Sending response."
        | Error err ->
            let str = EffectError.toString err
            printfn "Command failed."
            printfn $"%s{str}"

        return response
    }
