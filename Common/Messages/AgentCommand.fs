namespace Common

open System.Text.Json.Serialization
open FsCodec.SystemTextJson

type SectionIdentifiers = { Start: string; End: string }

[<JsonConverter(typeof<TypeSafeEnumConverter<FileWriteMode>>)>]
type FileWriteMode =
    | Append
    | Write

type ListProjectsCommand = struct end

type OpenProjectCommand = { ProjectName: string }

type ListProjectDirectoryCommand =
    { ProjectName: string
      FolderPath: string }

type ReadFileCommand =
    { ProjectName: string
      FilePath: string }

type WriteFileCommand =
    { ProjectName: string
      FilePath: string
      Content: string
      FileWriteMode: FileWriteMode }

type TextReplaceSectionCommand =
    { ProjectName: string
      FilePath: string
      SectionIdentifiers: SectionIdentifiers
      Content: string }

type TextReplaceCommand =
    { ProjectName: string
      FilePath: string
      Search: string
      Content: string }

type LoadPageCommand = { Url: string }

[<JsonConverter(typeof<UnionConverter<AgentCommand>>)>]
type AgentCommand =
    | ListProjectsCommand
    | OpenProjectCommand of OpenProjectCommand
    | ListProjectDirectoryCommand of ListProjectDirectoryCommand
    | ReadFileCommand of ReadFileCommand
    | WriteFileCommand of WriteFileCommand
    | TextReplaceSectionCommand of TextReplaceSectionCommand
    | TextReplaceCommand of TextReplaceCommand
    | TextInsertBeforeCommand of TextReplaceCommand
    | TextInsertAfterCommand of TextReplaceCommand
    | LoadPageCommand of LoadPageCommand

type AgentMessage<'a> = { Key: string; Command: 'a }
type AgentMessage = { Key: string; Command: AgentCommand }

module AgentMessage =
    let create f (message: AgentMessage<'a>) =
        { Key = message.Key
          Command = f message.Command }
