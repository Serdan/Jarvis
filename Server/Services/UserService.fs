namespace Server.Services

open System.Collections.Concurrent

module ConcurrentDictionary =
    let inline tryRemove (key: 'a) (dictionary: ConcurrentDictionary<'a, _>) =
        match dictionary.TryRemove(key) with
        | true, value -> ValueSome value
        | false, _ -> ValueNone

    let removeValues (value: 'b) (dictionary: ConcurrentDictionary<'a, 'b>) =
        dictionary
        |> Seq.filter (fun kvp -> value = kvp.Value)
        |> Seq.iter (fun kvp -> tryRemove kvp.Key dictionary |> ignore)

    let inline tryGetValue key (dictionary: ConcurrentDictionary<_, _>) =
        match dictionary.TryGetValue(key) with
        | true, value -> ValueSome value
        | false, _ -> ValueNone

type UserService() =
    let users = ConcurrentDictionary<string, string>()

    member this.Add(key, connectionId) = users[key] <- connectionId

    member this.Remove(connectionId) =
        ConcurrentDictionary.removeValues connectionId users

    member this.GetConnectionId(key) =
        users |> ConcurrentDictionary.tryGetValue key
