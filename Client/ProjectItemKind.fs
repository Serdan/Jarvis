namespace Client

open System
open System.IO
open System.Text.Json.Serialization
open FsCodec.SystemTextJson

[<JsonConverter(typeof<UnionConverter<ProjectItemKind>>)>]
type ProjectItemKind =
    | ProjectFile of name: string * fileSize: int64 * creationDate: DateTimeOffset * modificationDate: DateTimeOffset
    | ProjectFolder of name: string
    | ProjectFileError of name: string * errorMessage: string

module ProjectItemKind =
    let ofFileInfo (info: FileInfo) =
        ProjectFile(info.Name, info.Length, DateTimeOffset info.CreationTime, DateTimeOffset info.LastWriteTime)
