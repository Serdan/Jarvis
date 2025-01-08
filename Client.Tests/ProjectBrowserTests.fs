module ProjectBrowserTests

open System
open System.IO
open Client
open Client.Effect
open Client.ProjectBrowser
open Common
open NUnit.Framework
open FsUnitTyped

type Directory =
    | File of name: string * content: string
    | Directory of name: string * children: Directory list

module Directory =
    module private Utility =
        [<TailCall>]
        let rec updateItemCore (items: Directory list) item result =
            let (|Name|) =
                function
                | File(name, _)
                | Directory(name, _) -> name

            let (|HasName|_|) (Name search) (Name item) = search = item

            match items with
            | [] -> item :: result |> List.rev
            | HasName item :: rest -> (item :: result |> List.rev) @ rest
            | head :: rest -> updateItemCore rest item (head :: result)

    let updateItem item items = Utility.updateItemCore items item []

    let splitPath (path: string) =
        path.Split([| '/'; '\\' |], StringSplitOptions.RemoveEmptyEntries)
        |> List.ofArray

    let parseDirectory (path: string) =
        let rec core paths =
            match paths with
            | [] -> Directory("root", [])
            | [ path ] -> Directory(path, [])
            | path :: rest -> Directory(path, [ core rest ])

        path |> splitPath |> core

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

    let addFile' (path: string) content directory =
        let pathParts = splitPath path
        addFile pathParts content directory


[<Test>]
let ``Test dir`` () =
    let folder = Directory.parseDirectory "/root/"
    printfn $"{folder}"

    let folder = Directory.addFile' "/Project1/file.md" "content" folder
    let folder = Directory.addFile' "Project1/file2.md" "content2" folder
    printfn $"{folder}"

    ()

let files = Directory("root", [])

// Fake file operations with test data
let fakeFileOperations =
    { getFullPath = _.Replace('\\', '/') >> Ok
      ReadAllText =
        fun (FilePath filePath) ->
            match filePath with
            | "/fake/projects/Project1/readme.md" -> Ok(Content "Project 1 Readme")
            | "/fake/projects/Project1/todo.md" -> Ok(Content "Project 1 Todo")
            | _ -> Error(NotFoundError "File not found")

      WriteAllText =
        fun (FilePath filePath) (Content content) ->
            printfn $"Writing content to %s{filePath}: %s{content}"
            Ok()

      parseFile =
        fun path ->
            if path = "/fake/projects/Project1/readme.md" then
                Ok(FilePath path)
            else
                Error(NotFoundError $"File does not exist: {path}")

      CopyFile =
        fun (FilePath source) (FilePath destination) (overwrite: bool) ->
            printfn $"Copying file from %s{source} to %s{destination} (overwrite: %b{overwrite})"
            Ok()

      AppendAllText =
        fun (FilePath filePath) (Content content) ->
            printfn $"Appending content to %s{filePath}: %s{content}"
            Ok()

      parseFolder =
        fun path ->
            match path with
            | "/fake/projects/Project1" -> Ok(FolderPath path)
            | "/fake/projects" -> Ok(FolderPath path)
            | _ -> Error(NotFoundError path)

      GetFiles =
        fun (FolderPath folderPath) ->
            match folderPath with
            | "/fake/projects/Project1" ->
                Ok(
                    Seq.ofList
                        [ FilePath "/fake/projects/Project1/readme.md"
                          FilePath "/fake/projects/Project1/todo.md" ]
                )
            | _ -> Error(NotFoundError folderPath)

      getChildFolders =
        fun (FolderPath folderPath) ->
            match folderPath with
            | "/fake/projects" ->
                Ok(Seq.ofList [ FolderPath "/fake/projects/Project1"; FolderPath "/fake/projects/Project2" ])
            | _ -> Error(NotFoundError "Folder not found")

      GetFileInfo =
        fun (FilePath filePath) ->
            match filePath with
            | "/fake/projects/Project1/readme.md" -> Ok(FileInfo filePath)
            | _ -> Error(NotFoundError "File not found")

      GetFolderName = fun (FolderPath folderPath) -> Path.GetFileName folderPath
      getFileName = fun (FilePath filePath) -> Path.GetFileName filePath }

// Fake project data
let fakeProjectData =
    { Root = ProjectDirectory("/fake/projects")
      SpecialFiles = [ "readme.md"; "todo.md" ]
      FolderFilters = [ (fun folder -> folder <> "bin"); (fun folder -> folder <> "obj") ] }

type FakeContext() =
    interface ProjectIO with
        member this.Project = fakeProjectData

    interface FileIO with
        member this.File = fakeFileOperations

let fakeContext = FakeContext()

[<Test>]
let ``listProjects returns existing projects`` () =
    let result = listProjects fakeContext
    let expected = seq [ ProjectName "Project1"; ProjectName "Project2" ] |> Ok
    expected |> shouldEqual result

[<Test>]
let ``getProjectDetails retrieves special files`` () =
    let result = openProject { ProjectName = "Project1" } fakeContext

    let ct = Content ""

    match result with
    | Ok details ->
        details |> shouldContain ("readme.md", ct)
        details |> shouldContain ("todo.md", ct)
    | Error e -> Assert.Fail($"Expected Ok, but got Error: {EffectError.toString e}")

[<Test>]
let ``replaceSection updates section in file`` () =
    let sectionIdentifiers =
        { SectionIdentifiers.Start = "# Start Config"
          End = "# End Config" }

    let cmd =
        { ProjectName = "Project1"
          FilePath = "/fake/projects/Project1/readme.md"
          SectionIdentifiers = sectionIdentifiers
          Content = "Updated section content" }

    let result = replaceSection cmd fakeContext

    result |> shouldEqual (Ok(Content ""))

[<Test>]
let ``writeFile should write new content to file`` () =
    let filePath = FilePath "/fake/projects/Project1/newfile.md"
    let content = Content "New content written"

    let result = fakeFileOperations.WriteAllText filePath content
    result |> shouldEqual (Ok())

[<Test>]
let ``appendToFile should add content to existing file`` () =
    let filePath = FilePath "/fake/projects/Project1/todo.md"
    let content = Content "Appended content"

    let result = fakeFileOperations.AppendAllText filePath content
    result |> shouldEqual (Ok())

[<Test>]
let ``insertBefore should add content before search text`` () =
    let cmd =
        { ProjectName = "Project1"
          FilePath = "/fake/projects/Project1/readme.md"
          Search = "# Section Header"
          Content = "Inserted content before header" }

    let result = insertBefore cmd fakeContext

    result |> shouldEqual (Ok(Content ""))

[<Test>]
let ``insertAfter should add content after search text`` () =
    let cmd =
        { ProjectName = "Project1"
          FilePath = "/fake/projects/Project1/readme.md"
          Search = "# Section Footer"
          Content = "Inserted content after footer" }

    let result = insertAfter cmd fakeContext
    result |> shouldEqual (Ok(Content ""))

[<Test>]
let ``replaceText should modify specific text in file`` () =
    let cmd =
        { ProjectName = "Project1"
          FilePath = "/fake/projects/Project1/readme.md"
          Search = "Old Text"
          Content = "New Text" }

    let result = replaceText cmd fakeContext
    result |> shouldEqual (Ok(Content ""))
