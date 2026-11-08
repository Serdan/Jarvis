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
            Assert.That(results.Matches.Length, Is.EqualTo(1))
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
            Assert.That(results.Matches.Length, Is.EqualTo(1))
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
        | Ok result ->
            Assert.That(result.StopReason, Is.EqualTo(SearchStopReason.DirectoryVisitLimit))
            Assert.That(result.Matches, Is.Empty)
            Assert.That(result.DirectoriesVisited, Is.EqualTo(1))
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

[<Test>]
let cursor_cannot_be_tampered_with_to_forge_offsets_or_line_numbers () =
    let baseDir = root()
    try
        File.WriteAllText(Path.Combine(baseDir, "Project1", "cursor.txt"), String.replicate 100 "abc\n")
        let first =
            match readFilePage (page "cursor.txt" None 16) (RealContext baseDir) with
            | Ok value -> value
            | Error err -> failwithf "Unexpected first-page error: %A" err
        let cursor = Option.get first.NextCursor
        let separator = cursor.IndexOf('.')
        Assert.That(separator, Is.GreaterThan(2))
        // Tamper with the serialized offset/line payload, keeping the HMAC.
        let at = 3
        let substitute = if cursor[at] = 'A' then "B" else "A"
        let forged = cursor.Substring(0, at) + substitute + cursor.Substring(at + 1)
        match readFilePage (page "cursor.txt" (Some forged) 16) (RealContext baseDir) with
        | Error(ValidationError message) ->
            Assert.That(message, Does.Contain("Invalid read-page cursor"))
        | other -> Assert.Fail($"Tampered offset cursor was accepted: {other}")
    finally Directory.Delete(baseDir, true)

[<Test>]
let early_range_read_and_text_search_work_in_large_files () =
    let baseDir = root()
    try
        let file = Path.Combine(baseDir, "Project1", "early.txt")
        do
            use stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write)
            let intro = System.Text.Encoding.UTF8.GetBytes("needle\nsecond\n")
            stream.Write(intro)
            let chunk = Array.create<byte> (1024 * 1024) 120uy
            for _ in 1 .. 10 do stream.Write(chunk)
        match readFile (read "early.txt" (Some 1) (Some 2)) (RealContext baseDir) with
        | Ok(Content text) ->
            Assert.That(text, Is.EqualTo("1\tneedle" + Environment.NewLine + "2\tsecond"))
        | other -> Assert.Fail($"Narrow line range wrongly refused because whole file is large: {other}")
        let search =
            { ProjectName = "Project1"; FolderPath = None; Query = "needle"
              IncludeGlobs = []; ExcludeGlobs = []; MaxResults = Some 1 }
        match searchText search (RealContext baseDir) with
        | Ok matches ->
            Assert.That(matches.Matches.Length, Is.EqualTo(1))
            Assert.That(matches.Matches.Head.Line, Is.EqualTo(1))
        | Error err -> Assert.Fail($"Early text match in large source should succeed: {err}")
    finally Directory.Delete(baseDir, true)

[<Test>]
let extremely_long_lines_are_rejected_without_unbounded_strings () =
    let baseDir = root()
    try
        File.WriteAllText(Path.Combine(baseDir, "Project1", "long.txt"), String.replicate (1024 * 1024) "x")
        match readFile (read "long.txt" (Some 1) (Some 1)) (RealContext baseDir) with
        | Error(ValidationError message) -> Assert.That(message, Does.Contain("ReadFilePage"))
        | other -> Assert.Fail($"Long line should exceed a line-allocation cap: {other}")
        let search =
            { ProjectName = "Project1"; FolderPath = None; Query = "absent"
              IncludeGlobs = []; ExcludeGlobs = []; MaxResults = Some 1 }
        match searchText search (RealContext baseDir) with
        | Ok result -> Assert.That(result.StopReason, Is.EqualTo(SearchStopReason.TextScanLimit))
        | other -> Assert.Fail($"Long line should report incomplete search, not silently skip: {other}")
    finally Directory.Delete(baseDir, true)

[<Test>]
let legacy_direct_and_range_reads_reject_JSON_escape_amplification () =
    let baseDir = root()
    try
        File.WriteAllText(Path.Combine(baseDir, "Project1", "escapes.txt"),
                          String.replicate (230 * 1024) "\u0001")
        let context = RealContext baseDir
        match readFile (read "escapes.txt" None None) context with
        | Error(ValidationError message) -> Assert.That(message, Does.Contain("serialized"))
        | other -> Assert.Fail($"Expected exact JSON byte guard in direct read: {other}")
        match readFile (read "escapes.txt" (Some 1) (Some 1)) context with
        | Error(ValidationError message) -> Assert.That(message, Does.Contain("serialized"))
        | other -> Assert.Fail($"Expected exact JSON byte guard in range read: {other}")
    finally Directory.Delete(baseDir, true)

[<Test>]
let multi_file_reads_account_for_JSON_escaped_bytes () =
    let baseDir = root()
    try
        File.WriteAllText(Path.Combine(baseDir, "Project1", "one.txt"),
                          String.replicate (100 * 1024) "\u0001")
        File.WriteAllText(Path.Combine(baseDir, "Project1", "two.txt"),
                          String.replicate (100 * 1024) "\u0001")
        let cmd = { ProjectName = "Project1"; FilePaths = [ "one.txt"; "two.txt" ] }
        match readFiles cmd (RealContext baseDir) with
        | Ok results ->
            match results |> Seq.toList with
            | [ Content'.Text _; Content'.Error(ValidationError message) ] ->
                Assert.That(message, Does.Contain("Aggregate"))
            | other -> Assert.Fail($"Expected escaped text budget to reject second file: {other}")
        | Error err -> Assert.Fail($"Expected per-file budget result: {err}")
    finally Directory.Delete(baseDir, true)

[<Test>]
let text_search_counts_the_actual_JSON_escaped_preview_size () =
    let baseDir = root()
    try
        let file = Path.Combine(baseDir, "Project1", "target.txt")
        File.WriteAllText(file, "needle")
        let preview = String.replicate 200 "\u0001"
        let ops =
            { FileOperations.impl with
                SearchText = fun _ _ limit ->
                    Ok [ for i in 1 .. min limit 500 -> (i, 1, preview) ] }
        let search =
            { ProjectName = "Project1"; FolderPath = None; Query = "needle"
              IncludeGlobs = []; ExcludeGlobs = []; MaxResults = Some 500 }
        match searchText search (CountingContext(baseDir, ops)) with
        | Ok result ->
            Assert.That(result.StopReason, Is.EqualTo(SearchStopReason.OutputByteLimit))
            Assert.That(result.Matches.Length, Is.GreaterThan(1),
                        "The structured result should retain valid partial matches.")
        | other -> Assert.Fail($"Expected JSON-escaped preview limit, got: {other}")
    finally Directory.Delete(baseDir, true)

[<Test>]
let directory_listing_bounds_the_actual_JSON_escaped_filename_size () =
    let baseDir = root()
    try
        let name = String.replicate 1800 "\u0001"
        let source = Path.Combine(baseDir, "Project1", "fake.txt")
        let ops =
            { FileOperations.impl with
                GetFiles = fun (FolderPath folder) ->
                    if folder = baseDir then FileOperations.impl.GetFiles(FolderPath folder)
                    else Ok(seq { for _ in 1 .. 10000 do yield FilePath source })
                getFileName = fun _ -> name
                GetFileInfo = fun _ -> Error(NotFoundError "fake entry") }
        match listDirectory { ProjectName = "Project1"; FolderPath = "" } (CountingContext(baseDir, ops)) with
        | Error(ValidationError message) ->
            Assert.That(message, Does.Contain("serialized response"))
        | other -> Assert.Fail($"Expected JSON-escaped filename limit, got: {other}")
    finally Directory.Delete(baseDir, true)

type private FilteredContext(path: string, operations: FileOperations) =
    interface ProjectIO with
        member _.Project =
            { Root = ProjectDirectory path
              SpecialFiles = []
              FolderFilters = [fun name -> name <> "skip"] }
    interface FileIO with
        member _.File = operations

[<Test>]
let filtered_directories_are_still_counted_as_enumerated_work () =
    let baseDir = root()
    try
        let project = Path.Combine(baseDir, "Project1")
        let mutable produced = 0
        let ops =
            { FileOperations.impl with
                GetFiles = fun _ -> Ok Seq.empty
                getChildFolders = fun (FolderPath folder) ->
                    if folder = baseDir then
                        FileOperations.impl.getChildFolders(FolderPath folder)
                    else
                        Ok(seq {
                            for _ in 1 .. 100000 do
                                produced <- produced + 1
                                yield FolderPath(Path.Combine(project, "skip"))
                        }) }
        let cmd = { ProjectName = "Project1"; FolderPath = None
                    Query = "never"; MaxResults = Some 1 }
        match searchFiles cmd (FilteredContext(baseDir, ops)) with
        | Ok result ->
            Assert.That(result.StopReason, Is.EqualTo(SearchStopReason.EntryVisitLimit))
            Assert.That(produced, Is.LessThan(14500))
        | other -> Assert.Fail($"Filtered tree traversal was not bounded: {other}")
    finally Directory.Delete(baseDir, true)

[<Test>]
let search_rejects_matching_symlink_directories_without_exposing_results () =
    let baseDir = root()
    let outside = Path.Combine(baseDir, "outside")
    Directory.CreateDirectory(outside) |> ignore
    try
        let link = Path.Combine(baseDir, "Project1", "linked-dir")
        try
            Directory.CreateSymbolicLink(link, outside) |> ignore
        with
        | :? UnauthorizedAccessException -> Assert.Ignore("Symlink creation not permitted.")
        | :? PlatformNotSupportedException -> Assert.Ignore("Symlink creation not supported.")
        let cmd =
            { ProjectName = "Project1"; FolderPath = None
              Query = "linked-dir"; MaxResults = Some 1 }
        match searchFiles cmd (RealContext baseDir) with
        | Ok result -> Assert.That(result.Matches, Is.Empty)
        | Error(Client.PermissionDenied _) -> ()
        | other -> Assert.Fail($"Matching symbolic-link directory unexpectedly returned: {other}")
    finally Directory.Delete(baseDir, true)

[<Test>]
let search_returns_a_typed_and_serializable_partial_response () =
    let baseDir = root()
    try
        let project = Path.Combine(baseDir, "Project1")
        File.WriteAllText(Path.Combine(project, "a.md"), "hello")
        File.WriteAllText(Path.Combine(project, "b.md"), "hello")
        let cmd =
            { ProjectName = "Project1"; FolderPath = None
              Query = ".md"; MaxResults = Some 1 }
        match searchFiles cmd (RealContext baseDir) with
        | Error err -> Assert.Fail($"Expected structured partial result: {err}")
        | Ok result ->
            Assert.That(result.Matches.Length, Is.EqualTo(1))
            Assert.That(result.StopReason, Is.EqualTo(SearchStopReason.ResultLimit))
            Assert.That(result.FilesVisited, Is.EqualTo(1))
            let json = System.Text.Json.JsonSerializer.Serialize(result)
            use document = System.Text.Json.JsonDocument.Parse(json)
            Assert.That(document.RootElement.GetProperty("Matches").GetArrayLength(), Is.EqualTo(1))
            Assert.That(document.RootElement.GetProperty("StopReason").ValueKind,
                        Is.Not.EqualTo(System.Text.Json.JsonValueKind.Undefined))
    finally Directory.Delete(baseDir, true)

[<Test>]
let search_validates_globs_before_compiling_regex_patterns () =
    let baseDir = root()
    try
        let cmd =
            { ProjectName = "Project1"; FolderPath = None
              Query = "hello"; MaxResults = Some 1
              IncludeGlobs = [ String.replicate 600 "*" ]
              ExcludeGlobs = [] }
        match searchText cmd (RealContext baseDir) with
        | Error(ValidationError message) -> Assert.That(message, Does.Contain("glob budget"))
        | other -> Assert.Fail($"Glob input was accepted above its work budget: {other}")
    finally Directory.Delete(baseDir, true)

[<Test>]
let page_reader_rejects_invalid_UTF8_without_bypassing_binary_export () =
    let baseDir = root()
    try
        File.WriteAllBytes(Path.Combine(baseDir, "Project1", "invalid.bin"),
                           [| 0xFFuy; 0xFEuy; 0xFFuy; 0xFFuy; 0xFFuy; 0xFEuy |])
        match readFilePage (page "invalid.bin" None 64) (RealContext baseDir) with
        | Error(ValidationError message) ->
            Assert.That(message, Does.Contain("UTF-8"))
            Assert.That(message, Does.Contain("export_file"))
        | other -> Assert.Fail($"Invalid UTF-8 unexpectedly accepted: {other}")
    finally Directory.Delete(baseDir, true)

type private SpecialFilesContext(path: string, files: string list) =
    interface ProjectIO with
        member _.Project =
            { Root = ProjectDirectory path
              SpecialFiles = files
              FolderFilters = [] }
    interface FileIO with
        member _.File = FileOperations.impl

[<Test>]
let project_special_files_are_aggregately_bounded_before_response_serialization () =
    let baseDir = root()
    try
        let project = Path.Combine(baseDir, "Project1")
        File.WriteAllText(Path.Combine(project, "one.md"), String.replicate (410 * 1024) "x")
        File.WriteAllText(Path.Combine(project, "two.md"), String.replicate (410 * 1024) "y")
        let cmd: GetProjectDetailsCommand = { ProjectName = "Project1" }
        match getProjectDetails cmd (SpecialFilesContext(baseDir, [ "one.md"; "two.md" ])) with
        | Error(ValidationError message) ->
            Assert.That(message, Does.Contain("serialized response budget"))
        | other -> Assert.Fail($"Eager project details bypassed aggregate budget: {other}")
    finally Directory.Delete(baseDir, true)

[<Test>]
let project_special_file_resolution_does_not_leave_selected_checkout () =
    let baseDir = root()
    try
        File.WriteAllText(Path.Combine(baseDir, "outside.md"), "private")
        let cmd: GetProjectDetailsCommand = { ProjectName = "Project1" }
        match getProjectDetails cmd (SpecialFilesContext(baseDir, [ "../outside.md" ])) with
        | Ok entries -> Assert.That(entries, Is.Empty)
        | Error(Client.PermissionDenied _) -> ()
        | other -> Assert.Fail($"Project details permitted traversal outside checkout: {other}")
    finally Directory.Delete(baseDir, true)
