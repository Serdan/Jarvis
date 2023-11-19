using System.Collections.Frozen;
using System.Collections.Immutable;
using JarvisClient.Models;
using Shared;
using Shared.Messages;

namespace JarvisClient;

using static ProjectItemKind.Cons;

/// <summary>
/// Manages project file and directory operations within the Jarvis project.
/// Provides functionalities for listing projects, getting project details, listing directory contents, 
/// opening files, writing to files, and replacing specific sections within files.
/// </summary>
public class ProjectBrowser(IFileSystem fileSystem, string projectDirectory)
{
    /// <summary>
    /// Creates an instance of the ProjectBrowser class with a specified file system and directory.
    /// </summary>
    /// <param name="fileSystem">The file system interface used for file operations.</param>
    /// <param name="directory">The directory path to the project. If null, the user will be prompted to input a path.</param>
    /// <returns>A new instance of the ProjectBrowser class.</returns>
    public static ProjectBrowser Create(IFileSystem fileSystem, string? directory = null)
    {
        while (string.IsNullOrEmpty(directory) || fileSystem.DirectoryExists(directory) is false)
        {
            Console.WriteLine("Input project directory");
            directory = Console.ReadLine()!;
        }

        return new(fileSystem, directory);
    }

    /// <summary>
    /// Retrieves a list of project names in the current project directory.
    /// </summary>
    /// <returns>An immutable array of project names.</returns>
    public ImmutableArray<string> ListProjects() =>
        fileSystem.GetDirectories(projectDirectory)
                  .Select(Path.GetFileName)
                  .ToImmutableArray()!;

    /// <summary>
    /// Retrieves detailed information about a specified project.
    /// </summary>
    /// <param name="projectName">The name of the project for which details are requested.</param>
    /// <returns>A Result containing a FrozenDictionary with project details, or an error message.</returns>
    public Result<FrozenDictionary<string, string>> GetProjectDetails(string projectName) =>
        from project in ParseProjectName(projectName)
        from fullPath in ParseDirectory(project)
        let infos = from path in fullPath
                    select from file in ProjectFiles
                           select GetFile(project, file)
        let items = from item in filter(infos)
                    select new KeyValuePair<string, string>(item.Name, fileSystem.ReadAllText(item.FullName))
        select items.ToFrozenDictionary();

    /// <summary>
    /// Lists the items in a specified directory within a project.
    /// </summary>
    /// <param name="projectName">The name of the project.</param>
    /// <param name="directoryPath">The path of the directory to list items from.</param>
    /// <returns>An immutable array of ProjectItemKind, representing the contents of the directory.</returns>
    public Result<ImmutableArray<ProjectItemKind>> ListProjectDirectory(string projectName, string directoryPath) =>
        from project in ParseProjectName(projectName)
        from items in GetItems(project, directoryPath)
        select items;

    public Result<string> OpenFile(string projectName, string filePath) =>
        from project in ParseProjectName(projectName)
        from file in GetFile(project, filePath)
        select fileSystem.ReadAllText(file.FullName);

    public Result<string> WriteFile(string projectName, string filePath, string content, FileWriteMode mode) =>
        from project in ParseProjectName(projectName)
        from file in ParseFilePath(project, filePath)
        let message = file.Exists ? "existing file" : "new file"
        from result in mode switch
        {
            FileWriteMode.Append => @try(() => fileSystem.AppendAllText(file.FullName, content), $"Appended content to {message}: {file.Name}"),
            FileWriteMode.Write => @try(() => fileSystem.WriteAllText(file.FullName, content), $"Wrote content to {message}: {file.Name}"),
            _ => error($"Unsupported mode: {mode}")
        }
        select result;

    public Result<string> ReplaceSection(string projectName, string filePath, SectionIdentifiers sectionIdentifiers, string replacementContent) =>
        from project in ParseProjectName(projectName)
        from file in ParseFilePath(project, filePath)
        //
        let content = fileSystem.ReadAllText(file.FullName)
        let startIndex = content.IndexOf(sectionIdentifiers.Start, StringComparison.Ordinal)
        let endIndex = content.IndexOf(sectionIdentifiers.End, StringComparison.Ordinal)
        //
        from foundSection in startIndex >= 0 && endIndex >= 0
            ? ok(unit)
            : error("Section identifiers not found in file.")
        //
        let newContent = content[..startIndex]
            + replacementContent
            + content[endIndex..]
        let write = fileSystem.WriteAllText(file.FullName, newContent)
        //
        select ok("Section replaced successfully.");

    public Result<string> Replace(string projectName, string filePath, string search, string replacement) =>
        from project in ParseProjectName(projectName)
        from file in ParseFilePath(project, filePath)
        //
        let content = fileSystem.ReadAllText(file.FullName)
        let index = content.IndexOf(search, StringComparison.Ordinal)
        //
        from found in index >= 0
            ? ok(unit)
            : error("Search string not found in file.")
        //
        let newContent = content[..index] + replacement + content[(index + replacement.Length)..]
        let write = fileSystem.WriteAllText(file.FullName, newContent)
        select ok("Search string replaced successfully.");

    private Result<ProjectName> ParseProjectName(string projectName) =>
        from projects in ok(ListProjects())
        let exists = projects.Contains(projectName)
        select exists
            ? ok(new ProjectName(projectName))
            : error($"Unknown project name: {projectName}");

    private Result<ImmutableArray<ProjectItemKind>> GetItems(ProjectName projectName, string path) =>
        from folderNames in GetDirectoryNames(projectName, path)
        let folderItems = folderNames.Select(ProjectFolder)
        from fileNames in GetFileNames(projectName, path)
        let fileItems = from fileName in fileNames
                        let fileInfo = GetFile(projectName, path, fileName)
                        select fileInfo.Match(
                            ok: ProjectItemKind.ProjectFile.From,
                            error: error => ProjectFileError(fileName, error.Message)
                        )
        select ImmutableArray.Create<ProjectItemKind>([..folderItems, ..fileItems]);

    private Result<ImmutableArray<string>> GetDirectoryNames(ProjectName projectName, string directoryPath = "") =>
        from fullPath in ParseDirectory(projectName, directoryPath)
        let folders = from path in fullPath
                      select from folder in fileSystem.GetDirectories(path)
                             let folderName = Path.GetFileName(folder)
                             where FolderFilters.All(predicate => predicate(folderName))
                             select folderName
        select ImmutableArray.Create(folders.ToArray());

    private Result<ImmutableArray<string>> GetFileNames(ProjectName projectName, string directoryPath) =>
        from fullPath in ParseDirectory(projectName, directoryPath)
        let files = from path in fullPath
                    select from file in fileSystem.GetFiles(path)
                           select Path.GetFileName(file)
        select ImmutableArray.Create(files.ToArray());

    /// <summary>
    /// Returns the full path. It's important this isn't leaked to the agent.
    /// </summary>
    /// <param name="projectName"></param>
    /// <param name="directoryPath"></param>
    /// <returns></returns>
    private Result<HiddenString> ParseDirectory(ProjectName projectName, params string[] directoryPath) =>
        from fullPath in Combine(projectDirectory, [projectName.Name, ..directoryPath]).Select(FunctionalConsole.WriteLine)
        let exists = fileSystem.DirectoryExists(fullPath)
        select exists
            ? ok(new HiddenString(fullPath))
            : error($"Directory does not exist: {directoryPath.Last()}");

    /// <summary>
    /// Returns FileInfo containing the full path. It's important this isn't leaked to the agent.
    /// </summary>
    /// <param name="projectName"></param>
    /// <param name="path"></param>
    /// <returns></returns>
    private Result<FileInfo> GetFile(ProjectName projectName, params string[] path) =>
        from fullPath in Combine(projectDirectory, [projectName.Name, ..path])
        let exists = fileSystem.FileExists(fullPath)
        select exists
            ? ok(new FileInfo(fullPath))
            : error($"File does not exist: {path.Last()}");

    private Result<FilePath> ParseFilePath(ProjectName projectName, string filePath) =>
        from fullPath in Combine(projectDirectory, projectName.Name, filePath)
        from info in @try(() => new FileInfo(fullPath))
        select fileSystem.DirectoryExists(info.FullName) is false
            ? ok(new FilePath(info.Name, info.FullName, info.Exists))
            : error("Path is a directory");

    private static Result<string> Combine(string projectDirectory, params string[] paths) =>
        from path in @try(() => string.Join(Path.DirectorySeparatorChar, [projectDirectory, ..paths]))
        from fullPath in @try(() => Path.GetFullPath(path))
        select fullPath.StartsWith(projectDirectory)
            ? ok(fullPath)
            : error("Invalid path");

    private static readonly ImmutableArray<string> ProjectFiles =
        ImmutableArray.Create<string>(["readme.md", "notes.md", "todo.md", "bob_notes.txt"]);

    private static readonly ImmutableArray<Func<string, bool>> FolderFilters =
        ImmutableArray.Create<Func<string, bool>>([
            s => s.StartsWith('.'),
            s => s == "bin",
            s => s == "obj"
        ]);
}
