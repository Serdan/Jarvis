module Client.Path

open System.IO

let combine a b = Path.Combine(a, b)
let combineAll (segments: string seq) = segments |> Seq.toArray |> Path.Combine

let getFullPath path =
    try
        Path.GetFullPath path |> Ok
    with e ->
        Error e
