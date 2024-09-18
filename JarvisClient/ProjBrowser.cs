using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics;
using JarvisClient.Models;
using Shared;
using Shared.Messages;
using static JarvisClient.Models.ProjectItemKind.Cons;

namespace JarvisClient;

public class ProjBrowser
{
    /// <summary>
    ///     Parses the project name and verifies its existence within the current project directory.
    /// </summary>
    /// <param name="projectName">The name of the project to be parsed.</param>
    /// <returns>A Result containing the parsed ProjectName or an error message if the project does not exist.</returns>
    private static Effect<TRuntime, ProjectName> ParseProjectName<TRuntime>(string projectName)
        where TRuntime : struct, IHasDirectory<TRuntime>, IHasContext =>
        asEffect<TRuntime, ProjectName>() <<
        (
            _ =>
                from projects in ListProjects<TRuntime>()
                select projects.Contains(projectName)
                    ? okEffect(new ProjectName(projectName))
                    : errorEffect<ProjectName>($"Unknown project name: {projectName}")
        );

    private static Effect<TRuntime, string> ParseDirectory<TRuntime>(ProjectName projectName,
        params string[] directoryPath)
        where TRuntime : struct, IHasDirectory<TRuntime>, IHasContext =>
        asEffect<TRuntime, string>() <<
        (
            runtime =>
                from fullPath in ProjDirectory.Join<TRuntime>([projectName.Name, ..directoryPath])
                from directory in runtime.Directory
                let exists = directory.Exists(fullPath)
                select exists
                    ? okEffect(fullPath)
                    : errorEffect<string>($"Directory does not exist: {directoryPath.Last()}")
        );

    private static Effect<TRuntime, FilePath> ParseFilePath<TRuntime>(ProjectName projectName, string filePath)
        where TRuntime : struct, IHasDirectory<TRuntime>, IHasContext =>
        asEffect<TRuntime, FilePath>() <<
        (
            runtime =>
                from fullPath in ProjDirectory.Join<TRuntime>(projectName.Name, filePath)
                let fileInfo = new FileInfo(fullPath)
                select from io in runtime.Directory
                    select io.Exists(fileInfo.FullName) is false
                        ? okEffect(new FilePath(fileInfo.Name, fileInfo.FullName, fileInfo.Exists))
                        : errorEffect<FilePath>("Path is a directory")
        );

    private static Effect<TRuntime, ImmutableArray<string>> GetFileNames<TRuntime>(ProjectName projectName,
        string directoryPath)
        where TRuntime : struct, IHasDirectory<TRuntime>, IHasContext =>
        asEffect<TRuntime, ImmutableArray<string>>() <<
        (
            runtime =>
                from fullPath in ParseDirectory<TRuntime>(projectName, directoryPath)
                from files in from io in runtime.Directory
                    select from file in io.GetFiles(fullPath)
                        select Path.GetFileName(file)
                select ImmutableArray.Create(files.ToArray())
        );

    private static Effect<TRuntime, ImmutableArray<string>> GetDirectoryNames<TRuntime>(ProjectName projectName,
        string directoryPath = "")
        where TRuntime : struct, IHasDirectory<TRuntime>, IHasContext =>
        asEffect<TRuntime, ImmutableArray<string>>() <<
        (
            runtime =>
                from fullPath in ParseDirectory<TRuntime>(projectName, directoryPath)
                from folders in from io in runtime.Directory
                    select from folder in io.GetDirectories(fullPath)
                        let folderName = Path.GetFileName(folder)
                        where runtime.Context.FolderFilters.All(predicate => predicate(folderName))
                        select folderName
                select ImmutableArray.Create(folders.ToArray())
        );

    private static AsyncEffect<TRuntime, FileInfo> GetFile<TRuntime>(ProjectName projectName, params string[] path)
        where TRuntime : struct, IHasFile<TRuntime>, IHasContext =>
        asAsyncEffect<TRuntime, FileInfo>() <<
        (
            runtime =>
                from fullPath in ProjDirectory.Join<TRuntime>([projectName.Name, ..path]).ToAsync()
                from file in runtime.File
                let exists = file.Exists(fullPath)
                select exists
                    ? okAsyncEffect(new FileInfo(fullPath))
                    : errorAsyncEffect<FileInfo>($"File does not exist: {path.Last()}")
        );

    private static AsyncEffect<TRuntime, ImmutableArray<ProjectItemKind>> GetItems<TRuntime>(ProjectName projectName,
        string path)
        where TRuntime : struct, IHasFileSystem<TRuntime>, IHasContext =>
        asAsyncEffect<TRuntime, ImmutableArray<ProjectItemKind>>() <<
        (
            _ =>
                from folderNames in from names in GetDirectoryNames<TRuntime>(projectName, path).ToAsync()
                    select from name in names
                        select NewProjectFolder(name)
                from fileNames in from names in GetFileNames<TRuntime>(projectName, path).ToAsync()
                    let items = from name in names
                        select from file in GetFile<TRuntime>(projectName, path, name)
                            select NewProjectFile(file)
                    select filter(items)
                select ImmutableArray.Create<ProjectItemKind>([..folderNames, ..fileNames])
        );

    public static AsyncEffect<TRuntime, string> OpenFile<TRuntime>(string projectName, string filePath)
        where TRuntime : struct, IHasFileSystem<TRuntime>, IHasContext =>
        asAsyncEffect<TRuntime, string>() <<
        (
            runtime =>
                from project in ParseProjectName<TRuntime>(projectName).ToAsync()
                from file in GetFile<TRuntime>(project, filePath)
                select from io in runtime.File
                    select io.ReadAllTextAsync(file.FullName)
        );

    public static AsyncEffect<TRuntime, Unit> WriteFile<TRuntime>(string projectName, string filePath, string content,
        FileWriteMode mode)
        where TRuntime : struct, IHasFileSystem<TRuntime>, IHasContext =>
        asAsyncEffect<TRuntime, Unit>() <<
        (
            runtime =>
                from project in ParseProjectName<TRuntime>(projectName).ToAsync()
                from file in ParseFilePath<TRuntime>(project, filePath).ToAsync()
                let message = file.Exists ? "existing file" : "new file"
                select from io in runtime.File
                    select mode switch
                    {
                        FileWriteMode.Append => io.AppendAllTextAsync(file.FullName, content),
                        FileWriteMode.Write => io.WriteAllTextAsync(file.FullName, content),
                        _ => throw new InvalidOperationException($"Unsupported mode: {mode}")
                    }
        );

    public static AsyncEffect<TRuntime, string> ReplaceSection<TRuntime>(string projectName, string filePath,
        SectionIdentifiers sectionIdentifiers, string replacementContent)
        where TRuntime : struct, IHasFileSystem<TRuntime>, IHasContext =>
        asAsyncEffect<TRuntime, string>() <<
        (
            runtime =>
                from project in ParseProjectName<TRuntime>(projectName).ToAsync()
                from file in ParseFilePath<TRuntime>(project, filePath).ToAsync()
                select from io in runtime.File
                    from content in asyncEffect(() => io.ReadAllTextAsync(file.FullName).Select(ok))
                        .WithRuntime<TRuntime>()
                    let startIndex = content.IndexOf(sectionIdentifiers.Start, StringComparison.Ordinal)
                    let endIndex = content.IndexOf(sectionIdentifiers.End, StringComparison.Ordinal)
                    from foundSection in startIndex >= 0 && endIndex >= 0
                        ? okAsyncEffect(unit).WithRuntime<TRuntime>()
                        : errorAsyncEffect<Unit>("Section identifiers not found in file.")
                            .WithRuntime<TRuntime>()
                    let newContent = content[..startIndex]
                                     + replacementContent
                                     + content[(endIndex + sectionIdentifiers.End.Length)..]
                    let write = io.WriteAllTextAsync(file.FullName, newContent)
                    select okAsyncEffect(newContent)
        );

    public static AsyncEffect<TRuntime, string> Replace<TRuntime>(string projectName, string filePath, string search,
        string replacement)
        where TRuntime : struct, IHasFileSystem<TRuntime>, IHasContext =>
        asAsyncEffect<TRuntime, string>() <<
        (
            runtime =>
                from project in ParseProjectName<TRuntime>(projectName).ToAsync()
                from file in ParseFilePath<TRuntime>(project, filePath).ToAsync()
                select from io in runtime.File
                    from content in asyncEffect(() => io.ReadAllTextAsync(file.FullName).Select(ok))
                        .WithRuntime<TRuntime>()
                    let index = content.IndexOf(search, StringComparison.Ordinal)
                    from found in index >= 0
                        ? okAsyncEffect(unit).WithRuntime<TRuntime>()
                        : errorAsyncEffect<Unit>("Search string not found in file.").WithRuntime<TRuntime>()
                    let newContent = content[..index] + replacement + content[(index + search.Length)..]
                    let write = io.WriteAllTextAsync(file.FullName, newContent)
                    select okAsyncEffect(newContent)
        );

    public static AsyncEffect<TRuntime, string> InsertBefore<TRuntime>(string projectName, string filePath,
        string search, string content)
        where TRuntime : struct, IHasFileSystem<TRuntime>, IHasContext =>
        asAsyncEffect<TRuntime, string>() <<
        (
            runtime =>
                from project in ParseProjectName<TRuntime>(projectName).ToAsync()
                from file in ParseFilePath<TRuntime>(project, filePath).ToAsync()
                select from io in runtime.File
                    from existing in asyncEffect(() => io.ReadAllTextAsync(file.FullName).Select(ok))
                        .WithRuntime<TRuntime>()
                    let index = existing.IndexOf(search, StringComparison.Ordinal)
                    from found in index >= 0
                        ? okAsyncEffect(unit).WithRuntime<TRuntime>()
                        : errorAsyncEffect<Unit>("Search string not found in file.").WithRuntime<TRuntime>()
                    let newContent = existing[..index] + content + existing[index..]
                    let write = io.WriteAllTextAsync(file.FullName, newContent)
                    select okAsyncEffect(newContent)
        );

    public static AsyncEffect<TRuntime, string> InsertAfter<TRuntime>(string projectName, string filePath,
        string search, string content)
        where TRuntime : struct, IHasFileSystem<TRuntime>, IHasContext =>
        asAsyncEffect<TRuntime, string>() <<
        (
            runtime =>
                from project in ParseProjectName<TRuntime>(projectName).ToAsync()
                from file in ParseFilePath<TRuntime>(project, filePath).ToAsync()
                select from io in runtime.File
                    from existing in asyncEffect(() => io.ReadAllTextAsync(file.FullName).Select(ok))
                        .WithRuntime<TRuntime>()
                    let index = existing.IndexOf(search, StringComparison.Ordinal)
                    from found in index >= 0
                        ? okAsyncEffect(unit).WithRuntime<TRuntime>()
                        : errorAsyncEffect<Unit>("Search string not found in file.").WithRuntime<TRuntime>()
                    let newContent = existing[..(index + search.Length)] + content +
                                     existing[(index + search.Length)..]
                    let write = io.WriteAllTextAsync(file.FullName, newContent)
                    select okAsyncEffect(newContent)
        );

    public static AsyncEffect<TRuntime, string> RunUnitTests<TRuntime>(string projectName, string filePath)
        where TRuntime : struct, IHasDirectory<TRuntime>, IHasContext, IHasProcessIO<TRuntime> =>
        asAsyncEffect<TRuntime, string>() <<
        (
            runtime =>
                from project in ParseProjectName<TRuntime>(projectName).ToAsync()
                from file in ParseFilePath<TRuntime>(project, filePath).ToAsync()
                let processInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"test {file.FullName}",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
                select from process in runtime.ProcessIO(processInfo).ToAsync()
                    from output in asyncEffect(() => process.StandardOutput.ReadToEndAsync())
                    from exist in asyncEffect(process.WaitForExitAsync().ToUnit)
                    select process.ExitCode is 0
                        ? okAsyncEffect(output)
                        : errorAsyncEffect<string>($"Tests failed with exit code {process.ExitCode}.\nOutput:\n{output}")
        );

    /// <summary>
    ///     Retrieves a list of project names in the current project directory.
    /// </summary>
    /// <returns>An immutable array of project names.</returns>
    public static Effect<TRuntime, ImmutableArray<string>> ListProjects<TRuntime>()
        where TRuntime : struct, IHasDirectory<TRuntime>, IHasContext =>
        asEffect<TRuntime, ImmutableArray<string>>() <<
        (
            _ =>
                from directories in ProjDirectory.GetDirectories<TRuntime>()
                select directories.ToImmutableArray()
        );

    public static AsyncEffect<TRuntime, FrozenDictionary<string, string>> GetProjectDetails<TRuntime>(
        string projectName)
        where TRuntime : struct, IHasFileSystem<TRuntime>, IHasContext =>
        asAsyncEffect<TRuntime, FrozenDictionary<string, string>>() <<
        (
            runtime =>
                from project in ParseProjectName<TRuntime>(projectName).ToAsync()
                from fullPath in ParseDirectory<TRuntime>(project).ToAsync()
                let fileInfoResults = from file in runtime.Context.ProjectFiles
                    select GetFile<TRuntime>(project, file)
                let itemsEffect = filter(
                    from fileInfoEffect in fileInfoResults
                    select from fileInfo in fileInfoEffect
                        from content in from io in runtime.File select io.ReadAllTextAsync(fileInfo.FullName)
                        select new KeyValuePair<string, string>(fileInfo.Name, content)
                )
                select from items in itemsEffect
                    select items.ToFrozenDictionary()
        );

    public static AsyncEffect<TRuntime, ImmutableArray<ProjectItemKind>> ListProjectDirectory<TRuntime>(
        string projectName, string directoryPath)
        where TRuntime : struct, IHasFileSystem<TRuntime>, IHasContext =>
        asAsyncEffect<TRuntime, ImmutableArray<ProjectItemKind>>() <<
        (
            _ =>
                from project in ParseProjectName<TRuntime>(projectName).ToAsync()
                select GetItems<TRuntime>(project, directoryPath)
        );
}

public class ProjDirectory(string value)
{
    private readonly string value = value;

    public static Effect<TRuntime, bool> Contains<TRuntime>(string path)
        where TRuntime : struct, IHasContext =>
        effect((TRuntime runtime) => ok(path.StartsWith(runtime.Context.ProjectDirectory.value)));

    public static Effect<TRuntime, string> Join<TRuntime>(params string[] paths)
        where TRuntime : struct, IHasContext =>
        effect((TRuntime runtime) =>
        {
            var path = string.Join(Path.DirectorySeparatorChar,
                (string[]) [runtime.Context.ProjectDirectory.value, ..CleanPaths(paths)]);
            var fullPath = Path.GetFullPath(path);
            return from contains in Contains<TRuntime>(fullPath)
                select contains
                    ? okEffect(fullPath)
                    : errorEffect<string>("Invalid path");
        });

    public static Effect<TRuntime, string[]> GetDirectories<TRuntime>()
        where TRuntime : struct, IHasDirectory<TRuntime>, IHasContext =>
        effect((TRuntime runtime) => from io in runtime.Directory
            select io.GetDirectories(runtime.Context.ProjectDirectory.value));

    private static IEnumerable<string> CleanPaths(IEnumerable<string> paths) =>
        paths.Select(x => x.Trim('/', '\\'))
             .Where(x => !string.IsNullOrWhiteSpace(x));
}
