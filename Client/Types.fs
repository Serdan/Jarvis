namespace Client

open System
open System.IO
open System.Threading.Tasks
open System.Text.Json.Serialization
open FsCodec.SystemTextJson
open FsToolkit.ErrorHandling
open Common

type EffectError =
    | ExceptionError of exn
    | GenericError of string
    | ValidationError of string
    | ContextError of string
    | NotFoundError of string
    | PermissionDenied of string
    | ConfirmationRequired of ConfirmationRequest
    | AggregatedErrors of EffectError list

type ProjectName = ProjectName of string
type FolderPath = FolderPath of string
type FilePath = FilePath of string

type Content = Content of string

[<RequireQualifiedAccess>]
type Content' =
    | Text of string
    | Error of EffectError

type ProjectDirectory = ProjectDirectory of string

type Url = Url of string

type Result<'a> = Result<'a, EffectError>

type FileOperations =
    { getFullPath: string -> Result<string>
      ReadAllText: FilePath -> Result<Content>
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

type ProjectData =
    { Root: ProjectDirectory
      SpecialFiles: string list
      FolderFilters: (string -> bool) list }

type WebBrowser =
    { LoadPage: Url -> TaskResult<Content, EffectError> }

type FileIO =
    abstract File: FileOperations

type ProjectIO =
    abstract Project: ProjectData

type WebIO =
    abstract Browser: WebBrowser

type PermissionApproval =
    | AllowOnce
    | AllowExactForSession
    | Deny

type PermissionIO =
    abstract PromptPermission: AgentCommand -> ConfirmationRequest -> Task<PermissionApproval>

type RuntimeConstraint<'a when 'a :> ProjectIO and 'a :> FileIO and 'a :> WebIO> = 'a

type ClientOptions = { Path: string }

[<JsonConverter(typeof<UnionConverter<ProjectItemKind>>)>]
type ProjectItemKind =
    | ProjectFile of name: string * fileSize: int64 * creationDate: DateTimeOffset * modificationDate: DateTimeOffset
    | ProjectFolder of name: string
    | ProjectFileError of name: string * errorMessage: string

module ProjectItemKind =
    let ofFileInfo (info: FileInfo) =
        try
            ProjectFile(info.Name, info.Length, DateTimeOffset info.CreationTime, DateTimeOffset info.LastWriteTime)
        with ex ->
            ProjectFileError(info.Name, ex.Message)

type IgnoreBuilder() =
    member inline _.Delay([<InlineIfLambda>] f) = f ()
    member inline _.Yield _ = ()
    member inline _.Combine((), ()) = ()
    member inline _.Zero() = ()
