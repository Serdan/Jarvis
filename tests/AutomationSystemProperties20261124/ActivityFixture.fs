module SystemPropertyActivityFixture

open System
open System.Collections.Generic
open System.Security.Claims
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Http.Features
open Microsoft.AspNetCore.SignalR
open Microsoft.Extensions.Logging.Abstractions
open Common
open Common.SignalR
open Server.Services

// Reuse the same authenticated-Hub boundary as the accepted coupled Health
// fixture. Principal injection is explicit; the full JWT/HTTP ingress is
// independently covered by AutomationHealthHosted20261123.
type private Context(connection: string, user: string) =
    inherit HubCallerContext()
    let items = Dictionary<obj,obj>() :> IDictionary<obj,obj>
    let features = FeatureCollection() :> IFeatureCollection
    let principal = ClaimsPrincipal(ClaimsIdentity([Claim("sub", user)], "disposable-test-issuer"))
    override _.ConnectionId = connection
    override _.UserIdentifier = user
    override _.User = principal
    override _.Items = items
    override _.Features = features
    override _.ConnectionAborted = CancellationToken.None
    override _.Abort() = ()

type ActivityFixture(path: string) =
    let mutable store = new OptionalFeedbackStore(path)
    let mutable users = UserService()
    let mutable tracker = ClientResponseTracker()
    let hub user device =
        let connection = user + "-" + device
        users.TransportConnected(user, connection)
        users.Register(user,
            { DeviceId = device; DeviceName = device; ProtocolVersion = AgentProtocol.version
              ClientVersion = "disposable-system-property"; SupportedCommands = [] }, connection) |> ignore
        let result = new HubService(users, tracker, store, NullLogger<HubService>.Instance)
        result.Context <- Context(connection, user)
        result
    let buildHubs () = [|hub "alice" "one"; hub "alice" "two"; hub "foreign" "one"|]
    let mutable hubs = buildHubs ()
    member _.Read(client, project, agent) =
        task {
            try
                let! value = hubs[client].GetAgentActivity(project, agent)
                return Ok value
            with ex -> return Error ex.Message
        }
    member this.Record(project, agent, seen) =
        store.RecordAgentActivity("alice", project, agent, seen).GetAwaiter().GetResult()
        let deadline = System.Diagnostics.Stopwatch.StartNew()
        let mutable observed = None
        while observed.IsNone && deadline.ElapsedMilliseconds < 5000L do
            match this.Read(0, project, agent).GetAwaiter().GetResult() with
            | Ok value -> observed <- Some value
            | Error _ -> Thread.Sleep 20
        observed |> Option.defaultWith (fun () -> failwith "No qualifying SQLite activity after bounded wait")
    member _.Restart() =
        (store :> IDisposable).Dispose()
        store <- new OptionalFeedbackStore(path)
        users <- UserService()
        tracker <- ClientResponseTracker()
        hubs <- buildHubs ()
    interface IDisposable with
        member _.Dispose() = (store :> IDisposable).Dispose()
