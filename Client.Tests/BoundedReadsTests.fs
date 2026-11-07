module BoundedReadsTests

open System
open System.IO
open Client
open Client.IO
open Client.ProjectBrowser
open Common
open NUnit.Framework

let private root () =
    let path = Path.Combine(Path.GetTempPath(), "jarvis-bounded-reads-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(Path.Combine(path, "Project1")) |> ignore
    path

type private RealContext(path: string) =
    interface ProjectIO with
        member _.Project = { Root = ProjectDirectory path; SpecialFiles = []; FolderFilters = [] }
    interface FileIO with
        member _.File = FileOperations.impl

let private page file cursor pageBytes =
    { ProjectName = "Project1"
      FilePath = file
      Cursor = cursor
      PageBytes = Some pageBytes }

let private read file startLine endLine =
    { ProjectName = "Project1"
      FilePath = file
      StartLine = startLine
      EndLine = endLine
      IncludeLineNumbers = Some true }

[<Test>]
let hundred_MiB_text_source_can_be_inspected_without_reading_it_all () =
    let baseDir = root()
    try
        let source = Path.Combine(baseDir, "Project1", "huge.txt")
        let bytes = Array.create<byte> (1024 * 1024) 97uy
        do
            use stream = new FileStream(source, FileMode.CreateNew, FileAccess.Write)
            for _ in 1 .. 101 do stream.Write(bytes)
            stream.Flush(true)
        match readFile (read "huge.txt" None None) (RealContext baseDir) with
        | Error(ValidationError message) -> Assert.That(message, Does.Contain("ReadFilePage"))
        | other -> Assert.Fail($"Large unpaged read should be refused: {other}")
        match readFilePage (page "huge.txt" None 16384) (RealContext baseDir) with
        | Error err -> Assert.Fail($"Expected bounded page, got {err}")
        | Ok result ->
            Assert.That(result.Content.Length, Is.EqualTo(16384))
            Assert.That(result.StartByte, Is.EqualTo(0L))
            Assert.That(result.NextByte, Is.EqualTo(16384L))
            Assert.That(result.TotalBytes, Is.EqualTo(101L * 1024L * 1024L))
            Assert.That(result.NextCursor.IsSome, Is.True)
    finally Directory.Delete(baseDir, true)

[<Test>]
let UTF8_continuations_preserve_code_points_and_line_numbers () =
    let baseDir = root()
    try
        let sourceText = String.replicate 100 "café 🌍\r\n" + "last 😀"
        File.WriteAllText(Path.Combine(baseDir, "Project1", "unicode.txt"), sourceText)
        let mutable cursor = None
        let parts = ResizeArray<string>()
        let mutable pages = 0
        let mutable finalLine = 0L
        let mutable finished = false
        while not finished do
            match readFilePage (page "unicode.txt" cursor 17) (RealContext baseDir) with
            | Error err -> Assert.Fail($"Failed Unicode continuation: {err}")
            | Ok result ->
                parts.Add(result.Content)
                pages <- pages + 1
                finalLine <- result.NextLine
                cursor <- result.NextCursor
                finished <- cursor.IsNone
                Assert.That(pages, Is.LessThan(200))
        Assert.That(String.concat "" parts, Is.EqualTo(sourceText))
        Assert.That(finalLine, Is.EqualTo(101L))
        Assert.That(pages, Is.GreaterThan(1))
    finally Directory.Delete(baseDir, true)

[<Test>]
let cursor_rejects_source_changes_and_cross_file_reuse () =
    let baseDir = root()
    try
        let source = Path.Combine(baseDir, "Project1", "original.txt")
        let other = Path.Combine(baseDir, "Project1", "other.txt")
        File.WriteAllText(source, String.replicate 100 "abcd")
        File.WriteAllText(other, String.replicate 100 "abcd")
        let first =
            match readFilePage (page "original.txt" None 32) (RealContext baseDir) with
            | Ok value -> value
            | Error err -> failwithf "Cannot create first page: %A" err
        let cursor = first.NextCursor
        Assert.That(cursor.IsSome, Is.True)
        match readFilePage (page "other.txt" cursor 32) (RealContext baseDir) with
        | Error(ValidationError message) -> Assert.That(message, Does.Contain("different file"))
        | other -> Assert.Fail($"Cross-file cursor accepted: {other}")
        File.WriteAllText(source, String.replicate 101 "abcd")
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes 2.)
        match readFilePage (page "original.txt" cursor 32) (RealContext baseDir) with
        | Error(ValidationError message) -> Assert.That(message, Does.Contain("changed"))
        | other -> Assert.Fail($"Changed file cursor accepted: {other}")
    finally Directory.Delete(baseDir, true)

[<Test>]
let range_line_numbers_remain_correct_and_large_ranges_are_rejected () =
    let baseDir = root()
    try
        let path = Path.Combine(baseDir, "Project1", "lines.txt")
        File.WriteAllText(path, "zero\none\ntwo\nthree\n")
        match readFile (read "lines.txt" (Some 2) (Some 3)) (RealContext baseDir) with
        | Ok(Content content) ->
            Assert.That(content, Is.EqualTo("2\tone" + Environment.NewLine + "3\ttwo"))
        | other -> Assert.Fail($"Expected exact line range: {other}")
        File.WriteAllText(path, String.replicate 3000 "x\n")
        match readFile (read "lines.txt" None None) (RealContext baseDir) with
        | Error(ValidationError message) -> Assert.That(message, Does.Contain("ReadFilePage"))
        | other -> Assert.Fail($"Expected bounded line return: {other}")
    finally Directory.Delete(baseDir, true)

[<Test>]
let page_rejects_missing_files_and_symlink_escapes () =
    let baseDir = root()
    let outside = Path.Combine(baseDir, "outside.txt")
    try
        File.WriteAllText(outside, "secret")
        let context = RealContext baseDir
        Assert.That(readFilePage (page "missing.txt" None 100) context |> Result.isError, Is.True)
        let link = Path.Combine(baseDir, "Project1", "escape.txt")
        try
            File.CreateSymbolicLink(link, outside) |> ignore
            match readFilePage (page "escape.txt" None 100) context with
            | Error(Client.PermissionDenied _) -> ()
            | other -> Assert.Fail($"Expected symlink rejection, got {other}")
        with :? UnauthorizedAccessException ->
            Assert.Ignore("Symbolic links unavailable in this environment.")
    finally Directory.Delete(baseDir, true)

type private CountingContext(path: string, operations: FileOperations) =
    interface ProjectIO with
        member _.Project = { Root = ProjectDirectory path; SpecialFiles = []; FolderFilters = [] }
    interface FileIO with
        member _.File = operations

[<Test>]
let file_search_stops_enumerating_after_first_requested_result () =
    let baseDir = root()
    try
        let project = Path.Combine(baseDir, "Project1")
        let hit = Path.Combine(project, "first-hit.txt")
        File.WriteAllText(hit, "needle")
        let mutable enumerated = 0
        let ops =
            { FileOperations.impl with
                GetFiles = fun _ ->
                    Ok(seq {
                        enumerated <- enumerated + 1
                        yield FilePath hit
                        for i in 1 .. 200000 do
                            enumerated <- enumerated + 1
                            yield FilePath(Path.Combine(project, $"unrelated-{i}.txt"))
                    }) }
        let cmd =
            { ProjectName = "Project1"; FolderPath = None
              Query = "hit"; MaxResults = Some 1 }
        match searchFiles cmd (CountingContext(baseDir, ops)) with
        | Ok results ->
            Assert.That(results.Length, Is.EqualTo(1))
            Assert.That(enumerated, Is.EqualTo(1),
                        "MaxResults=1 must terminate file enumeration without listing the tree.")
        | Error err -> Assert.Fail($"Expected early result: {err}")
    finally Directory.Delete(baseDir, true)

[<Test>]
let text_search_stops_enumerating_after_first_match () =
    let baseDir = root()
    try
        let project = Path.Combine(baseDir, "Project1")
        let hit = Path.Combine(project, "target.txt")
        File.WriteAllText(hit, "needle text")
        let mutable enumerated = 0
        let ops =
            { FileOperations.impl with
                GetFiles = fun _ ->
                    Ok(seq {
                        enumerated <- enumerated + 1
                        yield FilePath hit
                        for i in 1 .. 200000 do
                            enumerated <- enumerated + 1
                            yield FilePath(Path.Combine(project, $"unrelated-{i}.txt"))
                    }) }
        let cmd =
            { ProjectName = "Project1"; FolderPath = None
              Query = "needle"; IncludeGlobs = []; ExcludeGlobs = []
              MaxResults = Some 1 }
        match searchText cmd (CountingContext(baseDir, ops)) with
        | Ok results ->
            Assert.That(results.Length, Is.EqualTo(1))
            Assert.That(enumerated, Is.EqualTo(1))
        | Error err -> Assert.Fail($"Expected early text match: {err}")
    finally Directory.Delete(baseDir, true)

[<Test>]
let giant_tree_stops_at_directory_budget_and_reports_incomplete_result () =
    let baseDir = root()
    try
        let project = Path.Combine(baseDir, "Project1")
        let mutable enumerated = 0
        let ops =
            { FileOperations.impl with
                GetFiles = fun _ -> Ok Seq.empty
                getChildFolders = fun (FolderPath folder) ->
                    if folder = baseDir then
                        FileOperations.impl.getChildFolders (FolderPath folder)
                    else
                        Ok(seq {
                            for i in 1 .. 100000 do
                                enumerated <- enumerated + 1
                                yield FolderPath(Path.Combine(project, $"nested-{i}"))
                        }) }
        let cmd =
            { ProjectName = "Project1"; FolderPath = None
              Query = "never-a-match"; MaxResults = Some 1 }
        match searchFiles cmd (CountingContext(baseDir, ops)) with
        | Error(ValidationError message) ->
            Assert.That(message, Does.Contain("incomplete"))
            Assert.That(message, Does.Contain("directory traversal"))
            Assert.That(enumerated, Is.LessThan(2100))
        | other -> Assert.Fail(sprintf "Expected explicit traversal exhaustion, got %A (enumerated=%d)" other enumerated)
    finally Directory.Delete(baseDir, true)

[<Test>]
let giant_single_directory_listing_fails_before_building_its_entire_result () =
    let baseDir = root()
    try
        let project = Path.Combine(baseDir, "Project1")
        let mutable visited = 0
        let ops =
            { FileOperations.impl with
                GetFiles = fun _ ->
                    Ok(seq {
                        for i in 1 .. 200000 do
                            visited <- visited + 1
                            yield FilePath(Path.Combine(project, $"file-{i}.txt"))
                    }) }
        match listDirectory { ProjectName = "Project1"; FolderPath = "" } (CountingContext(baseDir,ops)) with
        | Error(ValidationError message) ->
            Assert.That(message, Does.Contain("exceeds"))
            Assert.That(visited, Is.LessThan(2100))
        | other -> Assert.Fail($"Expected bounded directory failure, got {other}")
    finally Directory.Delete(baseDir, true)

[<Test>]
let multiple_files_have_an_independent_aggregate_response_cap () =
    let baseDir = root()
    try
        let project = Path.Combine(baseDir, "Project1")
        File.WriteAllText(Path.Combine(project, "a.txt"), String.replicate (480 * 1024) "a")
        File.WriteAllText(Path.Combine(project, "b.txt"), String.replicate (480 * 1024) "b")
        let command = { ProjectName = "Project1"; FilePaths = [ "a.txt"; "b.txt" ] }
        match readFiles command (RealContext baseDir) with
        | Error err -> Assert.Fail($"Expected structured per-file budget responses: {err}")
        | Ok values ->
            match values |> Seq.toList with
            | [ Content'.Text first; Content'.Error(ValidationError message) ] ->
                Assert.That(first.Length, Is.EqualTo(480 * 1024))
                Assert.That(message, Does.Contain("Aggregate"))
            | other -> Assert.Fail($"Expected explicit second-file budget error, got {other}")
    finally Directory.Delete(baseDir, true)
