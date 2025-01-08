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

type ListDirectoryCommand =
    { ProjectName: string
      FolderPath: string }

type ReadFileCommand =
    { ProjectName: string
      FilePath: string }

type ReadFilesCommand =
    { ProjectName: string
      FilePaths: string list }

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
    | ListDirectoryCommand of ListDirectoryCommand
    | ReadFileCommand of ReadFileCommand
    | ReadFilesCommand of ReadFilesCommand
    | WriteFileCommand of WriteFileCommand
    | ReplaceSectionCommand of TextReplaceSectionCommand
    | ReplaceCommand of TextReplaceCommand
    | InsertBeforeCommand of TextReplaceCommand
    | InsertAfterCommand of TextReplaceCommand
    | LoadPageCommand of LoadPageCommand

type AgentMessage<'a> = { Key: string; Command: 'a }
type AgentMessage = { Key: string; Command: AgentCommand }

module AgentMessage =
    let create f (message: AgentMessage<'a>) =
        { Key = message.Key
          Command = f message.Command }
