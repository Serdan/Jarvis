using System.Collections.Frozen;
using System.Collections.Immutable;
using Shared;

namespace JarvisClient;

using static ProjectItemKind.Cons;
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
        from path in ParseDirectory(project)
        from items in Ok(from file in ProjectFiles.Select(x => Combine(path, x)).Select(GetFile).Apply(filter)
                         select new KeyValuePair<string, string>(file.Name, FileIO.ReadAllText(file.FullName)))
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
        from folders in GetDirectoryNames(path)
        let folderItems = folders.Select(Folder)
        from files in GetFileNames(path)
        let fileItems = files.Select(File)
        select ImmutableArray.Create<ProjectItemKind>([..folderItems, ..fileItems]);

    private Result<ImmutableArray<string>> GetDirectoryNames(string path = "") =>
        from fullPath in ParseDirectory(path)
        let folders = Directory.GetDirectories(fullPath).Select(Path.GetFileName)
        select ImmutableArray.Create(folders.ToArray());

    private Result<ImmutableArray<string>> GetFileNames(string path) =>
        from fullPath in ParseDirectory(path)
        let files = Directory.GetFiles(fullPath).Select(Path.GetFileName)
        select ImmutableArray.Create(files.ToArray());

    private Result<string> ParseDirectory(string path) =>
        from fullPath in Ok(Combine(projectDirectory, path))
        let exists = Directory.Exists(fullPath)
        select exists
            ? Ok(fullPath)
            : Error($"Directory does not exist: {path}");

    private Option<FileInfo> GetFile(string path) =>
        from fullPath in Some(Path.Combine(projectDirectory, path))
        let exists = FileIO.Exists(fullPath)
        select exists
            ? Some(new FileInfo(fullPath))
            : None;

    private static readonly ImmutableArray<string> ProjectFiles =
        ImmutableArray.Create<string>(["readme.md", "notes.md", "todo.md"]);

    private static string Combine(string left, string right)
    {
        return left.TrimEnd('/', '\\') + "/" + right.TrimStart('/', '\\', '.');
    }
}
