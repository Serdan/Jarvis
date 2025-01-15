module Client.Tests.Fake.FileSystem

open System
open FSharp.UMX

type Directory =
    | File of name: string * content: string
    | Directory of name: string * children: Directory list

module Directory =
    let (|Name|) item =
        match item with
        | File(name, _)
        | Directory(name, _) -> name

    let (|HasName|_|) (Name search) (Name item) = search = item

    let (|FileHasName|_|) (search: string) (item: Directory) =
        match item with
        | File(name, _) -> search = name
        | _ -> false

    let (|FolderHasName|_|) (search: string) (item: Directory) =
        match item with
        | Directory(name, _) -> search = name
        | _ -> false

    let fileHasName search item =
        match item with
        | FileHasName search -> true
        | _ -> false

    let folderHasName search item =
        match item with
        | FolderHasName search -> true
        | _ -> false

    module private Utility =

        [<TailCall>]
        let rec updateItemCore (items: Directory list) item result =
            match items with
            | [] -> item :: result |> List.rev
            | HasName item :: rest -> (item :: result |> List.rev) @ rest
            | head :: rest -> updateItemCore rest item (head :: result)

        let updateItem item items = updateItemCore items item []

        let rec addFile (pathParts: string list) (content: string) (directory: Directory) : Directory =
            let isFolder folderName =
                function
                | Directory(name, _) when name = folderName -> true
                | _ -> false

            match pathParts, directory with
            | [], _ -> directory
            | [ fileName ], Directory(name, children) ->
                let updatedChildren = children |> updateItem (File(fileName, content))

                Directory(name, updatedChildren)
            | folderName :: remainingPath, Directory(name, children) ->
                let child = children |> List.tryFind (isFolder folderName)

                let updatedChild =
                    match child with
                    | Some(Directory(folderName, subChildren)) ->
                        // Recurse into the existing folder
                        addFile remainingPath content (Directory(folderName, subChildren))
                    | _ ->
                        // Create a new folder and recurse into it
                        addFile remainingPath content (Directory(folderName, []))

                let updatedChildren = children |> updateItem updatedChild

                Directory(name, updatedChildren)
            | _, _ -> failwith "Invalid path or directory structure"

    let private splitPath (path: string) =
        path.Split([| '/'; '\\' |], StringSplitOptions.RemoveEmptyEntries)
        |> List.ofArray

    let parseDirectory (path: string) =
        let rec core paths =
            match paths with
            | [] -> Directory("root", [])
            | [ path ] -> Directory(path, [])
            | path :: rest -> Directory(path, [ core rest ])

        path |> splitPath |> core

    let addFile (path: string) (content: string) (directory: Directory) : Directory =
        let pathParts = splitPath path
        Utility.addFile pathParts content directory

    let findFile path directory =
        let rec core pathParts (directory: Directory) =
            match pathParts, directory with
            | [ fileName ], Directory(_, children) -> children |> List.tryFind (fileHasName fileName)
            | folderName :: remainingPath, Directory(_, children) ->
                children
                |> List.tryFind (folderHasName folderName)
                |> Option.bind (function
                    | Directory(_, subChildren) -> core remainingPath (Directory(folderName, subChildren))
                    | _ -> None)
            | _, _ -> None

        let pathParts = splitPath path
        core pathParts directory

    let findDirectory path directory =
        let rec core pathParts (directory: Directory) =
            match pathParts, directory with
            | [], _ -> Some directory
            | folderName :: remainingPath, Directory(_, children) ->
                children
                |> List.tryFind (folderHasName folderName)
                |> Option.bind (function
                    | Directory(_, subChildren) -> core remainingPath (Directory(folderName, subChildren))
                    | _ -> None)
            | _, _ -> None

        let pathParts = splitPath path
        core pathParts directory

type Container = { mutable Directory: Directory }

let instance = { Directory = Directory("root", []) }

let addFile path content =
    instance.Directory <- instance.Directory |> Directory.addFile path content
