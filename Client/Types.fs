namespace Client

type ProjectName = ProjectName of string

type FolderPath =
    | FolderPath of string

    member this.Value =
        match this with
        | FolderPath p -> p


type FilePath = FilePath of string
type Content = Content of string

type ProjectDirectory =
    | ProjectDirectory of string

    member this.Value =
        match this with
        | ProjectDirectory v -> v

    member this.ToFolderPath =
        match this with
        | ProjectDirectory v -> FolderPath v

type Result<'a> = Result<'a, exn>
