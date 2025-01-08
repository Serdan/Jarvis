module Client.IO.WebOperations

open System.Net.Http
open Client
open FsToolkit.ErrorHandling

let load (Url url) =
    taskResult {
        try
            use client = new HttpClient()

            let! result = client.GetStringAsync(url)
            return! result |> Content |> Ok
        with e ->
            return! e |> ExceptionError |> Error
    }

let impl = { LoadPage = load }
