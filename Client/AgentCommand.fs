namespace Client

open System.Text.Json.Serialization
open FsCodec.SystemTextJson

type SectionIdentifiers = { Start: string; End: string }

[<JsonConverter(typeof<TypeSafeEnumConverter<FileWriteMode>>)>]
type FileWriteMode =
    | Append
    | Write

[<JsonConverter(typeof<UnionConverter<AgentCommand>>)>]
type AgentCommand =
    | ListProjectsCommand
    | GetProjectDetailsCommand of projectName: string
    | ListProjectDirectoryCommand of projectName: string * folderPath: string
    | OpenFileCommand of projectName: string * filePath: string
    | WriteFileCommand of projectName: string * filePath: string * content: string * fileWriteMode: FileWriteMode
    | TextReplaceSectionCommand of
        projectName: string *
        filePath: string *
        sectionIdentifiers: SectionIdentifiers *
        content: string
    | TextReplaceCommand of projectName: string * filePath: string * search: string * content: string
    | TextInsertBeforeCommand of projectName: string * filePath: string * search: string * content: string
    | TextInsertAfterCommand of projectName: string * filePath: string * search: string * content: string
    | LoadPageCommand of url: string
