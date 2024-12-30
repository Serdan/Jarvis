namespace Client

open System.IO
open Client.Effect
open FsToolkit.ErrorHandling

type ProjectName = ProjectName of string
type FolderPath = FolderPath of string
type FilePath = FilePath of string
type Content = Content of string

type ProjectDirectory =
    | ProjectDirectory of string

    member this.ToFolderPath =
        match this with
        | ProjectDirectory v -> FolderPath v

type Url = Url of string

type Result<'a> = Result<'a, EffectError>

type FileOperations =
    { ReadAllText: FilePath -> Result<Content>
      WriteAllText: FilePath -> Content -> Result<unit>
      parseFile: string -> Result<FilePath>
      CopyFile: FilePath -> FilePath -> bool -> Result<unit>
      AppendAllText: FilePath -> Content -> Result<unit>
      parseFolder: string -> Result<FolderPath>
      GetFiles: FolderPath -> Result<FilePath seq>
      getChildFolders: FolderPath -> Result<FolderPath seq>
      GetFileInfo: FilePath -> Result<FileInfo>
      GetFolderName: FolderPath -> string
      getFileName: FilePath -> string }

type FileIO =
    abstract File: FileOperations

type ProjectData =
    { Root: ProjectDirectory
      SpecialFiles: string list
      FolderFilters: (string -> bool) list }

type ProjectIO =
    abstract Project: ProjectData

type ClientOptions = { Path: string }

type WebBrowser =
    { LoadPage: Url -> TaskResult<Content, EffectError> }

type WebIO =
    abstract Browser: WebBrowser
