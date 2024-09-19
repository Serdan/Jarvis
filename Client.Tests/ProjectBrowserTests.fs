module ProjectBrowserTests

open System
open System.IO
open Client
open Client.ProjectBrowser
open NUnit.Framework
open FsUnitTyped

// Fake file operations with test data
let fakeFileOperations =
    { ReadAllText =
        fun (FilePath filePath) ->
            match filePath with
            | "/fake/projects/Project1/readme.md" -> Ok(Content "Project 1 Readme")
            | "/fake/projects/Project1/todo.md" -> Ok(Content "Project 1 Todo")
            | _ -> Error(Exception "File not found")

      WriteAllText =
        fun (FilePath filePath) (Content content) ->
            printfn $"Writing content to %s{filePath}"
            Ok()

      parseFile =
        fun path ->
            if path = "/fake/projects/Project1/readme.md" then
                Ok(FilePath path)
            else
                Error(Exception $"File does not exist: {path}")

      CopyFile =
        fun (FilePath source) (FilePath destination) (overwrite: bool) ->
            printfn $"Copying file from %s{source} to %s{destination} (overwrite: %b{overwrite})"
            Ok()

      AppendAllText =
        fun (FilePath filePath) (Content content) ->
            printfn $"Appending content to %s{filePath}"
            Ok()

      parseFolder =
        fun path ->
            match path with
            | "/fake/projects/Project1" -> Ok(FolderPath path)
            | "/fake/projects" -> Ok(FolderPath path)
            | _ -> Error(Exception "Folder does not exist: {path}")

      GetFiles =
        fun (FolderPath folderPath) ->
            match folderPath with
            | "/fake/projects/Project1" ->
                Ok(
                    Seq.ofList
                        [ FilePath "/fake/projects/Project1/readme.md"
                          FilePath "/fake/projects/Project1/todo.md" ]
                )
            | _ -> Error(Exception "Folder not found")

      getChildFolders =
        fun (FolderPath folderPath) ->
            match folderPath with
            | "/fake/projects" ->
                Ok(Seq.ofList [ FolderPath "/fake/projects/Project1"; FolderPath "/fake/projects/Project2" ])
            | _ -> Error(Exception "Folder not found")

      GetFileInfo =
        fun (FilePath filePath) ->
            match filePath with
            | "/fake/projects/Project1/readme.md" -> Ok(FileInfo filePath)
            | _ -> Error(Exception "File not found")

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
let ``getFiles returns files in folder`` () =
    let result = fakeFileOperations.GetFiles(FolderPath "/fake/projects/Project1")

    let expected =
        seq
            [ FilePath "/fake/projects/Project1/readme.md"
              FilePath "/fake/projects/Project1/todo.md" ]
        |> Ok

    expected |> shouldEqual result

[<Test>]
let ``ReadAllText should return content for existing file`` () =
    let result =
        fakeFileOperations.ReadAllText(FilePath "/fake/projects/Project1/readme.md")

    let expected = Ok(Content "Project 1 Readme")
    result |> shouldEqual expected

[<Test>]
let ``ReadAllText should fail for non-existing file`` () =
    (fun () ->
        let result =
            fakeFileOperations.ReadAllText(FilePath "/fake/projects/Project1/nonexistent.md")

        match result with
        | Ok _ -> failwith "Expected error, but got success"
        | Error ex -> raise ex)
    |> shouldFail<Exception>

[<Test>]
let ``parseFile should return file path for existing file`` () =
    let result = fakeFileOperations.parseFile "/fake/projects/Project1/readme.md"
    result |> shouldEqual (Ok(FilePath "/fake/projects/Project1/readme.md"))

[<Test>]
let ``parseFile should return error for non-existing file`` () =
    (fun () ->
        let result =
            fakeFileOperations.parseFile "/fake/projects/NonexistentProject/readme.md"

        match result with
        | Ok _ -> failwith "Expected error, but got success"
        | Error ex -> raise ex)
    |> shouldFail<Exception>

[<Test>]
let ``parseFolder should return folder path for existing folder`` () =
    let result = fakeFileOperations.parseFolder "/fake/projects/Project1"

    match result with
    | Ok(FolderPath path) -> path |> shouldEqual "/fake/projects/Project1"
    | Error e -> Assert.Fail($"Expected Ok, but got Error: {e.Message}")

[<Test>]
let ``parseFolder should return error for non-existing folder`` () =
    (fun () ->
        let result = fakeFileOperations.parseFolder "/fake/projects/NonexistentFolder"

        match result with
        | Ok _ -> failwith "Expected error, but got success"
        | Error ex -> raise ex)
    |> shouldFail<Exception>

[<Test>]
let ``getChildFolders should return child folders for existing folder`` () =
    let result = fakeFileOperations.getChildFolders (FolderPath "/fake/projects")

    match result with
    | Ok folders ->
        folders
        |> Seq.map (fun (FolderPath path) -> path)
        |> shouldEqual (seq [ "/fake/projects/Project1"; "/fake/projects/Project2" ])
    | Error e -> Assert.Fail($"Expected Ok, but got Error: {e.Message}")

[<Test>]
let ``getFileName should return file name from file path`` () =
    let result =
        fakeFileOperations.getFileName (FilePath "/fake/projects/Project1/readme.md")

    result |> shouldEqual "readme.md"
