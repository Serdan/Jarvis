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
    | LedgerFailureError of Common.LedgerFailureInfo

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
      ReadAllBytes: FilePath -> Result<byte[]>
      ReadLines: FilePath -> int -> int option -> Result<(int * string) list>
      SearchText: FilePath -> string -> int -> Result<(int * int * string) list>
      WriteAllText: FilePath -> Content -> Result<unit>
      CreateDirectory: FolderPath -> Result<unit>
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

// A single filesystem transaction owns the read, precondition check, edit
// and atomic publication. The edit callback returns the typed command result.
type TextEditOptions =
    { SourceRoot: FolderPath
      CreateParents: bool
      DryRun: bool }

// Append preserves the existing file's raw bytes (including BOMs and legacy
// encodings) instead of decoding and re-encoding the original contents.
type TextFileChange =
    | ReplaceText of string
    | AppendUtf8Text of string

type FileIO =
    abstract File: FileOperations
    abstract EditText<'T>:
        path: FilePath * options: TextEditOptions *
        edit: (string option -> Result<TextFileChange * 'T>) -> Result<'T>

type ProjectIO =
    abstract Project: ProjectData

type WebIO =
    abstract Browser: WebBrowser

type PermissionApproval =
    | AllowOnce
    | AllowExactForSession
    | AllowExecutableForSession
    | Deny

type TrustLevel =
    | NoTrust
    | PartialTrust
    | FullTrust

module TrustLevel =
    let parse (value: string) =
        match value |> Option.ofObj |> Option.map (fun x -> x.Trim().ToLowerInvariant()) with
        | Some "none" -> Ok NoTrust
        | Some "partial" -> Ok PartialTrust
        | Some "full" -> Ok FullTrust
        | Some value -> Error $"Unknown trust level: {value}. Use none, partial, or full."
        | None -> Ok PartialTrust

    let toDisplayName =
        function
        | NoTrust -> "none"
        | PartialTrust -> "partial"
        | FullTrust -> "full"

type PermissionIO =
    abstract TrustLevel: TrustLevel
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


// Local TUI contracts for authenticated operator input. The implementation
// is deliberately not exposed through agent-attributed MCP commands.
type OperatorChallengeEntry =
    { ProjectName: string
      ChallengeId: string
      ConditionId: string
      IssuerAgentId: string
      Title: string
      TaskId: string
      Question: string
      ChallengeStatus: string
      ConditionStatus: string
      Version: int
      Evidence: string
      CreatedAtUtc: string
      UpdatedAtUtc: string
      RespondedBy: string
      RespondedAtUtc: string
      ResponseEventSequence: int64 }

type OperatorChallengeReplyFailure =
    | StaleOperatorReply of string
    | OtherOperatorReplyFailure of string

type OperatorChallengeDraft =
    { Target: OperatorChallengeEntry
      Text: string
      OperationId: string }

module OperatorChallengePresentation =
    let isActionable (item: OperatorChallengeEntry) =
        (item.ChallengeStatus = "Pending" || item.ChallengeStatus = "Blocked")
        && item.ConditionStatus = "Pending"

    let pendingCount (entries: OperatorChallengeEntry list) =
        entries |> List.filter isActionable |> List.length

    let sameCondition (left: OperatorChallengeEntry) (right: OperatorChallengeEntry) =
        left.ProjectName = right.ProjectName
        && left.ChallengeId = right.ChallengeId
        && left.ConditionId = right.ConditionId

    // Recover only after a canonical reload proves that the same unanswered
    // condition is active on a newer version. Unknown writes retain their ID.
    let updatedActionableVersion oldEntry candidate =
        candidate
        |> Option.filter (fun current ->
            sameCondition oldEntry current
            && isActionable current
            && current.Version > oldEntry.Version)

    let rebaseAfterDefinitiveStale (draft: OperatorChallengeDraft) current newOperationId =
        updatedActionableVersion draft.Target current
        |> Option.filter (fun _ ->
            not (String.IsNullOrWhiteSpace newOperationId)
            && newOperationId <> draft.OperationId)
        |> Option.map (fun latest ->
            { draft with Target = latest; OperationId = newOperationId })

    let provenanceLine (item: OperatorChallengeEntry) =
        if item.ConditionStatus = "Satisfied"
           && not (String.IsNullOrWhiteSpace item.RespondedBy)
           && not (String.IsNullOrWhiteSpace item.RespondedAtUtc)
           && item.ResponseEventSequence > 0L then
            Some(sprintf "Answered by %s at %s (event #%d)"
                     item.RespondedBy item.RespondedAtUtc item.ResponseEventSequence)
        else None

type OperatorChallengeReply =
    { ProjectName: string
      ChallengeId: string
      ConditionId: string
      ExpectedVersion: int
      Response: string
      OperationId: string }

type OperatorChallengeSnapshot =
    { Items: OperatorChallengeEntry list
      Warnings: string list }
