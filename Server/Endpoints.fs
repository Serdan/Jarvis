module Server.Endpoints

open Common
open Giraffe
open Microsoft.AspNetCore.Http
open Server.Services

let private sendCommandToUser (ctx: HttpContext) message =
    ctx.GetService<ClientService>().SendCommandToUser message

let getResult command ctx =
    AgentMessage.create command >> sendCommandToUser ctx

let jsonify next ctx result = json result next ctx

let handler command next ctx message =
    task {
        let! result = getResult command ctx message
        return! jsonify next ctx result
    }

let listProjects (message: AgentMessage<ListProjectsCommand>) : HttpHandler =
    fun (next: HttpFunc) (ctx: HttpContext) ->
        task { return! message |> handler (fun _ -> ListProjectsCommand) next ctx }

let openProject (message: AgentMessage<OpenProjectCommand>) : HttpHandler =
    fun (next: HttpFunc) (ctx: HttpContext) -> task { return! message |> handler OpenProjectCommand next ctx }

let listProjectDirectory (message: AgentMessage<ListDirectoryCommand>) : HttpHandler =
    fun (next: HttpFunc) (ctx: HttpContext) -> task { return! message |> handler ListDirectoryCommand next ctx }

let readFile (message: AgentMessage<ReadFileCommand>) : HttpHandler =
    fun (next: HttpFunc) (ctx: HttpContext) -> task { return! message |> handler ReadFileCommand next ctx }

let readFiles (message: AgentMessage<ReadFilesCommand>) : HttpHandler =
    fun (next: HttpFunc) (ctx: HttpContext) -> task { return! message |> handler ReadFilesCommand next ctx }

let writeFile (message: AgentMessage<WriteFileCommand>) : HttpHandler =
    fun (next: HttpFunc) (ctx: HttpContext) -> task { return! message |> handler WriteFileCommand next ctx }

let textReplaceSection (message: AgentMessage<TextReplaceSectionCommand>) : HttpHandler =
    fun (next: HttpFunc) (ctx: HttpContext) -> task { return! message |> handler ReplaceSectionCommand next ctx }

let textReplace (message: AgentMessage<TextReplaceCommand>) : HttpHandler =
    fun (next: HttpFunc) (ctx: HttpContext) -> task { return! message |> handler ReplaceCommand next ctx }

let textInsertBefore (message: AgentMessage<TextReplaceCommand>) : HttpHandler =
    fun (next: HttpFunc) (ctx: HttpContext) -> task { return! message |> handler InsertBeforeCommand next ctx }

let textInsertAfter (message: AgentMessage<TextReplaceCommand>) : HttpHandler =
    fun (next: HttpFunc) (ctx: HttpContext) -> task { return! message |> handler InsertAfterCommand next ctx }

let runUnitTests: HttpHandler = fun (f: HttpFunc) (ctx: HttpContext) -> f ctx

let readTodo: HttpHandler = fun (f: HttpFunc) (ctx: HttpContext) -> f ctx

let loadPage: HttpHandler = fun (f: HttpFunc) (ctx: HttpContext) -> f ctx
