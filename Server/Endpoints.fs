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

let listCommands (message: AgentMessage<ListCommandsCommand>) : HttpHandler =
    fun (next: HttpFunc) (ctx: HttpContext) ->
        task { return! message |> handler (fun _ -> ListCommandsCommand) next ctx }

let listProjects (message: AgentMessage<ListProjectsCommand>) : HttpHandler =
    fun (next: HttpFunc) (ctx: HttpContext) ->
        task { return! message |> handler (fun _ -> ListProjectsCommand) next ctx }

let getProjectDetails (message: AgentMessage<GetProjectDetailsCommand>) : HttpHandler =
    fun (next: HttpFunc) (ctx: HttpContext) -> task { return! message |> handler GetProjectDetailsCommand next ctx }

let listProjectDirectory (message: AgentMessage<ListDirectoryCommand>) : HttpHandler =
    fun (next: HttpFunc) (ctx: HttpContext) -> task { return! message |> handler ListDirectoryCommand next ctx }

let searchFiles (message: AgentMessage<SearchFilesCommand>) : HttpHandler =
    fun (next: HttpFunc) (ctx: HttpContext) -> task { return! message |> handler SearchFilesCommand next ctx }

let searchText (message: AgentMessage<SearchTextCommand>) : HttpHandler =
    fun (next: HttpFunc) (ctx: HttpContext) -> task { return! message |> handler SearchTextCommand next ctx }

let readFile (message: AgentMessage<ReadFileCommand>) : HttpHandler =
    fun (next: HttpFunc) (ctx: HttpContext) -> task { return! message |> handler ReadFileCommand next ctx }

let readFiles (message: AgentMessage<ReadFilesCommand>) : HttpHandler =
    fun (next: HttpFunc) (ctx: HttpContext) -> task { return! message |> handler ReadFilesCommand next ctx }

let writeFile (message: AgentMessage<WriteFileCommand>) : HttpHandler =
    fun (next: HttpFunc) (ctx: HttpContext) -> task { return! message |> handler WriteFileCommand next ctx }

let patchFile (message: AgentMessage<PatchFileCommand>) : HttpHandler =
    fun (next: HttpFunc) (ctx: HttpContext) -> task { return! message |> handler PatchFileCommand next ctx }

let runCommand (message: AgentMessage<RunCommandCommand>) : HttpHandler =
    fun (next: HttpFunc) (ctx: HttpContext) -> task { return! message |> handler RunCommandCommand next ctx }

let getGitStatus (message: AgentMessage<GitStatusCommand>) : HttpHandler =
    fun (next: HttpFunc) (ctx: HttpContext) -> task { return! message |> handler GetGitStatusCommand next ctx }

let getGitDiff (message: AgentMessage<GitDiffCommand>) : HttpHandler =
    fun (next: HttpFunc) (ctx: HttpContext) -> task { return! message |> handler GetGitDiffCommand next ctx }

let gitCommit (message: AgentMessage<GitCommitCommand>) : HttpHandler =
    fun (next: HttpFunc) (ctx: HttpContext) -> task { return! message |> handler GitCommitCommand next ctx }

let startJob (message: AgentMessage<StartJobCommand>) : HttpHandler =
    fun (next: HttpFunc) (ctx: HttpContext) -> task { return! message |> handler StartJobCommand next ctx }

let listJobs (message: AgentMessage<ListJobsCommand>) : HttpHandler =
    fun (next: HttpFunc) (ctx: HttpContext) -> task { return! message |> handler ListJobsCommand next ctx }

let getJobResult (message: AgentMessage<GetJobResultCommand>) : HttpHandler =
    fun (next: HttpFunc) (ctx: HttpContext) -> task { return! message |> handler GetJobResultCommand next ctx }

let cancelJob (message: AgentMessage<CancelJobCommand>) : HttpHandler =
    fun (next: HttpFunc) (ctx: HttpContext) -> task { return! message |> handler CancelJobCommand next ctx }
