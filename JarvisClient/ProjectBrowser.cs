using System.Collections.Frozen;
using System.Collections.Immutable;
using Shared;

#pragma warning disable CS8509 // The switch expression does not handle all possible values of its input type (it is not exhaustive).

namespace JarvisClient;

using static OptionUnion<FileInfo>;
using static ProjectItemKind.Cons;
using static ProjectItemKind;
using static Unions.Result;
using FileIO = File;

public class ProjectBrowser(string projectDirectory)
{
    public static ProjectBrowser Create(string? directory = null)
    {
        while (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            Console.WriteLine("Input project directory");
            directory = Console.ReadLine()!;
        }

        return new(directory);
    }

    public Result<ImmutableArray<string>> ListProjects() =>
        GetDirectoryNames();

    public Result<FrozenDictionary<string, string>> GetProjectDetails(string projectName) =>
        from project in ParseProjectName(projectName)
        from fullPath in ParseDirectory(project)
        let infos = from path in fullPath
                    select ProjectFiles.Select(x => Combine(path, x)).Select(GetFile).Apply(filter)
        let items = from item in infos
                    select new KeyValuePair<string, string>(item.Name, FileIO.ReadAllText(item.FullName))
        select items.ToFrozenDictionary();

    public Result<ImmutableArray<ProjectItemKind>> ListProjectDirectory(string projectName, string path) =>
        from project in ParseProjectName(projectName)
        from items in GetItems(Combine(project, path))
        select items;

    public Result<string> OpenFile(string projectName, string path) =>
        from project in ParseProjectName(projectName)
        from file in GetFile(Combine(project, path)).ToResult("File not found")
        select file.OpenText().ReadToEnd();

    private Result<string> ParseProjectName(string projectName) =>
        union(GetDirectoryNames()) switch
        {
            Ok(ImmutableArray<string> projects) =>
                projects.Contains(projectName)
                    ? Ok(projectName)
                    : Error("Unknown project name"),
            Error(var error) => Error(error),
            _ => Error($"Unknown error while parsing project name: {projectName}")
        };

    private Result<ImmutableArray<ProjectItemKind>> GetItems(string path) =>
        from folderNames in GetDirectoryNames(path)
        let folderItems = folderNames.Select(Folder)
        from fileNames in GetFileNames(path)
        let fileItems = from fileName in fileNames
                        let fileInfo = GetFile(Combine(path, fileName))
                        select union2(fileInfo) switch
                        {
                            Some(var info) => new File(fileName)
                            {
                                FileSize = info.Length,
                                CreationDate = info.CreationTime,
                                ModificationDate = info.LastWriteTime
                            },
                            None => new File(fileName)
                        }
        select ImmutableArray.Create<ProjectItemKind>([..folderItems, ..fileItems]);

    private Result<ImmutableArray<string>> GetDirectoryNames(string directoryPath = "") =>
        from fullPath in ParseDirectory(directoryPath)
        let folders = from path in fullPath
                      select from folder in Directory.GetDirectories(path)
                             select Path.GetFileName(folder)
        select ImmutableArray.Create(folders.ToArray());

    private Result<ImmutableArray<string>> GetFileNames(string directoryPath) =>
        from fullPath in ParseDirectory(directoryPath)
        let files = from path in fullPath
                    select from file in Directory.GetFiles(path)
                           select Path.GetFileName(file)
        select ImmutableArray.Create(files.ToArray());

    /// <summary>
    /// Returns the full path. It's important this isn't leaked to the agent.
    /// </summary>
    /// <param name="directoryPath"></param>
    /// <returns></returns>
    private Result<HiddenString> ParseDirectory(string directoryPath) =>
        from fullPath in Ok(Combine(projectDirectory, directoryPath))
        let exists = Directory.Exists(fullPath)
        select exists
            ? Ok(new HiddenString(fullPath))
            : Error($"Directory does not exist: {directoryPath}");

    /// <summary>
    /// Returns FileInfo containing the full path. It's important this isn't leaked to the agent.
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    private Option<FileInfo> GetFile(string path) =>
        from fullPath in some(Path.Combine(projectDirectory, path))
        let exists = FileIO.Exists(fullPath)
        select exists
            ? some(new FileInfo(fullPath))
            : none;

    private static readonly ImmutableArray<string> ProjectFiles =
        ImmutableArray.Create<string>(["readme.md", "notes.md", "todo.md"]);

    private static string Combine(string left, string right)
    {
        return left.TrimEnd('/', '\\') + "/" + right.TrimStart('/', '\\', '.');
    }
}
