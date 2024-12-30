module Client.UserClient

open System.Text.Json
open System.Threading.Tasks
open Client.Effect
open Client.IO
open Client.SignalR
open Common
open Common.SignalR
open FsToolkit.ErrorHandling
open Microsoft.AspNetCore.SignalR.Client

let receiveMessage message = printfn $"%s{message}"

let serialize<'a> (value: 'a) =
    try
        JsonSerializer.Serialize value |> Ok
    with e ->
        e |> GenericError |> Error

let serialize'<'a> = Result.bind serialize<'a> >> ValueTask<_>

let receiveCommand (hub: HubConnection) rt (correlationId: string) (command: AgentCommand) =
    task {
        let! response =
            match command with
            | ListProjectsCommand -> ProjectBrowser.listProjects rt |> serialize'
            | OpenProjectCommand cmd -> rt |> ProjectBrowser.getProjectDetails cmd.ProjectName |> serialize'
            | ListProjectDirectoryCommand cmd ->
                rt
                |> ProjectBrowser.listProjectDirectory cmd.ProjectName cmd.FolderPath
                |> serialize'
            | ReadFileCommand cmd -> rt |> ProjectBrowser.openFile cmd.ProjectName cmd.FilePath |> serialize'
            | WriteFileCommand cmd ->
                rt
                |> ProjectBrowser.writeFile cmd.ProjectName cmd.FilePath cmd.Content cmd.FileWriteMode
                |> serialize'
            | TextReplaceSectionCommand cmd ->
                rt
                |> ProjectBrowser.replaceSection cmd.ProjectName cmd.FilePath cmd.SectionIdentifiers cmd.Content
                |> serialize'
            | TextReplaceCommand cmd ->
                ProjectBrowser.replaceText cmd.ProjectName cmd.FilePath cmd.Search cmd.Content rt
                |> serialize'
            | TextInsertBeforeCommand cmd ->
                rt
                |> ProjectBrowser.insertBefore cmd.ProjectName cmd.FilePath cmd.Search cmd.Content
                |> serialize'
            | TextInsertAfterCommand cmd ->
                rt
                |> ProjectBrowser.insertAfter cmd.ProjectName cmd.FilePath cmd.Search cmd.Content
                |> serialize'
            | LoadPageCommand cmd ->
                task {
                    let! result = Web.loadPage (Url cmd.Url) rt
                    return Result.bind serialize result
                }
                |> ValueTask<Result<_, _>>

        let respond message =
            hub.invokeAsync<IHubService> _.SendClientResponse(correlationId, message)

        let! _ =
            match response with
            | Ok result ->
                printfn "Command executed. Sending response."
                respond result
            | Error error ->
                let str = EffectError.toString error
                printfn "Command failed."
                printfn $"%s{str}"
                respond str

        ()
    }
    :> Task

type RuntimeConstraint<'a when 'a :> ProjectIO and 'a :> FileIO and 'a :> WebIO> = 'a

type Client<'a when RuntimeConstraint<'a>>(hub: HubConnection, rt: 'a) =
    interface IClientService with
        member this.ReceiveCommand(correlationId, command) =
            receiveCommand hub rt correlationId command

        member this.ReceiveMessage(message) = printfn $"%s{message}"
