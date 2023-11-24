using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Numerics;
using JarvisClient.Models;
using Shared;
using Shared.Messages;
using static Kehlet.Functional.ResultUnion<System.IO.FileInfo>;

#pragma warning disable CS8509 // The switch expression does not handle all possible values of its input type (it is not exhaustive).

namespace JarvisClient;

using static ProjectItemKind.Cons;

/// <summary>
/// Manages project file and directory operations within the Jarvis project.
/// Provides functionalities for listing projects, getting project details, listing directory contents, 
/// opening files, writing to files, and replacing specific sections within files.
/// </summary>
public class ProjectBrowser(IFileSystem fileSystem, string projectDirectory)
{
    private ProjectDirectory projectDirectory = new(projectDirectory);

    public string ProjectDirectory
    {
        set => projectDirectory = new(value);
    }

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
        projectDirectory.GetDirectories(fileSystem)
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

    /// <summary>
    /// Opens and reads the content of a specified file in a project.
    /// </summary>
    /// <param name="projectName">The name of the project containing the file.</param>
    /// <param name="filePath">The path to the file to be opened.</param>
    /// <returns>The content of the file as a string.</returns>
    public Result<string> OpenFile(string projectName, string filePath) =>
        from project in ParseProjectName(projectName)
        from file in GetFile(project, filePath)
        select fileSystem.ReadAllText(file.FullName);

    /// <summary>
    /// Writes content to a specified file within a project, with a specified mode (append or overwrite).
    /// </summary>
    /// <param name="projectName">The name of the project containing the file.</param>
    /// <param name="filePath">The path to the file where content will be written.</param>
    /// <param name="content">The content to be written to the file.</param>
    /// <param name="mode">The file writing mode (append or overwrite).</param>
    /// <returns>A message indicating the success of the operation.</returns>
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

    /// <summary>
    /// Replaces a specific section within a file in a project based on provided section identifiers.
    /// </summary>
    /// <param name="projectName">The name of the project containing the file.</param>
    /// <param name="filePath">The path to the file to be modified.</param>
    /// <param name="sectionIdentifiers">Identifiers defining the start and end of the section to be replaced.</param>
    /// <param name="replacementContent">The content to replace the identified section with.</param>
    /// <returns>A message indicating the success of the section replacement.</returns>
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
            + content[(endIndex + sectionIdentifiers.End.Length)..]
        let write = fileSystem.WriteAllText(file.FullName, newContent)
        //
        select ok(newContent);

    /// <summary>
    /// Replaces a specific string within a file in a project.
    /// </summary>
    /// <param name="projectName">The name of the project containing the file.</param>
    /// <param name="filePath">The path to the file to be modified.</param>
    /// <param name="search">The string to search for within the file.</param>
    /// <param name="replacement">The string to replace the search string with.</param>
    /// <returns>A message indicating the success of the string replacement.</returns>
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
        let newContent = content[..index] + replacement + content[(index + search.Length)..]
        let write = fileSystem.WriteAllText(file.FullName, newContent)
        select ok(newContent);

    public Result<string> InsertBefore(string projectName, string filePath, string search, string content) =>
        from project in ParseProjectName(projectName)
        from file in ParseFilePath(project, filePath)
        //
        let existing = fileSystem.ReadAllText(file.FullName)
        let index = existing.IndexOf(search, StringComparison.Ordinal)
        //
        from found in index >= 0
            ? ok(unit)
            : error("Search string not found in file.")
        //
        let newContent = existing[..index] + content + existing[index..]
        let write = fileSystem.WriteAllText(file.FullName, newContent)
        select ok(newContent);

    public Result<string> InsertAfter(string projectName, string filePath, string search, string content) =>
        from project in ParseProjectName(projectName)
        from file in ParseFilePath(project, filePath)
        //
        let existing = fileSystem.ReadAllText(file.FullName)
        let index = existing.IndexOf(search, StringComparison.Ordinal)
        //
        from found in index >= 0
            ? ok(unit)
            : error("Search string not found in file.")
        let newContent = existing[..(index + search.Length)] + content + existing[(index + search.Length)..]
        let write = fileSystem.WriteAllText(file.FullName, newContent)
        select ok(newContent);

    /// <summary>
    /// Executes unit tests for a given project and returns the results.
    /// </summary>
    /// <param name="projectName">The name of the project for which to run tests.</param>
    /// <param name="filePath"></param>
    /// <returns>A Result containing the test results or an error message if the test execution fails.</returns>
    public Result<string> RunUnitTests(string projectName, string filePath) =>
        from project in ParseProjectName(projectName)
        from file in ParseFilePath(project, filePath)
        from processInfo in ok(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"test {file.FullName}",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        })
        from process in @try(() => Process.Start(processInfo))
        from output in @try(() => process.StandardOutput.ReadToEnd())
        from _ in @try(process.WaitForExit, unit)
        select process.ExitCode is 0
            ? ok(output)
            : error($"Tests failed with exit code {process.ExitCode}.\nOutput: {output}");

    /// <summary>
    /// Parses the project name and verifies its existence within the current project directory.
    /// </summary>
    /// <param name="projectName">The name of the project to be parsed.</param>
    /// <returns>A Result containing the parsed ProjectName or an error message if the project does not exist.</returns>
    private Result<ProjectName> ParseProjectName(string projectName) =>
        from projects in ok(ListProjects())
        let exists = projects.Contains(projectName)
        select exists
            ? ok(new ProjectName(projectName))
            : error($"Unknown project name: {projectName}");

    /// <summary>
    /// Retrieves the items (folders and files) from a specified path within a project.
    /// </summary>
    /// <param name="projectName">The project name as a ProjectName object.</param>
    /// <param name="path">The directory path from which items are to be retrieved.</param>
    /// <returns>An ImmutableArray of ProjectItemKind representing the items in the specified path.</returns>
    private Result<ImmutableArray<ProjectItemKind>> GetItems(ProjectName projectName, string path) =>
        from folderNames in GetDirectoryNames(projectName, path)
        let folderItems = folderNames.Select(NewProjectFolder)
        from fileNames in GetFileNames(projectName, path)
        let fileItems = from fileName in fileNames
                        let fileInfo = GetFile(projectName, path, fileName)
                        select union(fileInfo) switch
                        {
                            Ok(var info) => NewProjectFile(info),
                            Error(var error) => NewProjectFileError(fileName, error.Message)
                        }
        select ImmutableArray.Create<ProjectItemKind>([..folderItems, ..fileItems]);

    /// <summary>
    /// Retrieves the names of directories from a specified path within a project.
    /// </summary>
    /// <param name="projectName">The project name as a ProjectName object.</param>
    /// <param name="directoryPath">The directory path from which directory names are to be retrieved.</param>
    /// <returns>An ImmutableArray of strings representing the names of directories in the specified path.</returns>
    private Result<ImmutableArray<string>> GetDirectoryNames(ProjectName projectName, string directoryPath = "") =>
        from fullPath in ParseDirectory(projectName, directoryPath)
        let folders = from path in fullPath
                      select from folder in fileSystem.GetDirectories(path)
                             let folderName = Path.GetFileName(folder)
                             where FolderFilters.All(predicate => predicate(folderName))
                             select folderName
        select ImmutableArray.Create(folders.ToArray());

    /// <summary>
    /// Retrieves the names of files from a specified directory path within a project.
    /// </summary>
    /// <param name="projectName">The project name as a ProjectName object.</param>
    /// <param name="directoryPath">The directory path from which file names are to be retrieved.</param>
    /// <returns>An ImmutableArray of strings representing the names of files in the specified directory path.</returns>
    private Result<ImmutableArray<string>> GetFileNames(ProjectName projectName, string directoryPath) =>
        from fullPath in ParseDirectory(projectName, directoryPath)
        let files = from path in fullPath
                    select from file in fileSystem.GetFiles(path)
                           select Path.GetFileName(file)
        select ImmutableArray.Create(files.ToArray());

    /// <summary>
    /// Parses the directory path within a project and ensures its existence.
    /// </summary>
    /// <param name="projectName">The project name as a ProjectName object.</param>
    /// <param name="directoryPath">The directory path(s) to be parsed and verified.</param>
    /// <returns>A Result containing a HiddenString representing the full path, or an error message if the directory does not exist.</returns>
    private Result<HiddenString> ParseDirectory(ProjectName projectName, params string[] directoryPath) =>
        from fullPath in projectDirectory.Join([projectName.Name, ..directoryPath])
        let exists = fileSystem.DirectoryExists(fullPath)
        select exists
            ? ok(new HiddenString(fullPath))
            : error($"Directory does not exist: {directoryPath.Last()}");

    /// <summary>
    /// Retrieves file information for a specified file path within a project.
    /// </summary>
    /// <param name="projectName">The project name as a ProjectName object.</param>
    /// <param name="path">The file path(s) for which information is to be retrieved.</param>
    /// <returns>A Result containing FileInfo or an error message if the file does not exist.</returns>
    private Result<FileInfo> GetFile(ProjectName projectName, params string[] path) =>
        from fullPath in projectDirectory.Join([projectName.Name, ..path])
        let exists = fileSystem.FileExists(fullPath)
        select exists
            ? ok(new FileInfo(fullPath))
            : error($"File does not exist: {path.Last()}");

    /// <summary>
    /// Parses a file path within a project and verifies its existence.
    /// </summary>
    /// <param name="projectName">The project name as a ProjectName object.</param>
    /// <param name="filePath">The file path to be parsed and verified.</param>
    /// <returns>A Result containing a FilePath object or an error message if the file path is invalid or does not exist.</returns>
    private Result<FilePath> ParseFilePath(ProjectName projectName, string filePath) =>
        from fullPath in projectDirectory.Join(projectName.Name, filePath)
        from info in @try(() => new FileInfo(fullPath))
        select fileSystem.DirectoryExists(info.FullName) is false
            ? ok(new FilePath(info.Name, info.FullName, info.Exists))
            : error("Path is a directory");

    private static readonly ImmutableArray<string> ProjectFiles =
        ImmutableArray.Create<string>(["readme.md", "notes.md", "todo.md", "bob_notes.txt"]);

    private static readonly ImmutableArray<Func<string, bool>> FolderFilters =
        ImmutableArray.Create<Func<string, bool>>([
            s => !s.StartsWith('.'),
            s => s != "bin",
            s => s != "obj"
        ]);
}
