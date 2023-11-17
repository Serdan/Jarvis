using System.Collections.Frozen;
using System.Collections.Immutable;
using JarvisClient.Models;
using Shared;
using Shared.AlgebraicTypes;
using Shared.Messages;

namespace JarvisClient;

using static ProjectItemKind.Cons;
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

    public ImmutableArray<string> ListProjects() =>
        Directory.GetDirectories(projectDirectory)
                 .Select(Path.GetFileName)
                 .ToImmutableArray()!;

    public Result<FrozenDictionary<string, string>> GetProjectDetails(string projectName) =>
        from project in ParseProjectName(projectName)
        from fullPath in ParseDirectory(project, project.Name)
        let infos = from path in fullPath
                    select from file in ProjectFiles
                           let filePath = Combine(path, file)
                           select GetFile(project, filePath)
        let items = from item in filter(infos)
                    select new KeyValuePair<string, string>(item.Name, FileIO.ReadAllText(item.FullName))
        select items.ToFrozenDictionary();

    public Result<ImmutableArray<ProjectItemKind>> ListProjectDirectory(string projectName, string directoryPath) =>
        from project in ParseProjectName(projectName)
        from items in GetItems(project, directoryPath)
        select items;

    public Result<string> OpenFile(string projectName, string filePath) =>
        from project in ParseProjectName(projectName)
        from file in GetFile(project, filePath)
        select FileIO.ReadAllText(file.FullName);

    public Result<string> WriteFile(string projectName, string filePath, string content, FileWriteMode mode) =>
        from project in ParseProjectName(projectName)
        from file in ParseFilePath(project, filePath)
        let message = file.Exists ? "existing file" : "new file"
        from result in mode switch
        {
            FileWriteMode.Append => @try(() => FileIO.AppendAllText(file.FullName, content), $"Appended content to {message}: {file.Name}"),
            FileWriteMode.Write => @try(() => FileIO.WriteAllText(file.FullName, content), $"Wrote content to {message}: {file.Name}"),
            _ => Error($"Unsupported mode: {mode}")
        }
        select result;

    public Result<string> ReplaceSection(string filePath, SectionIdentifiers sectionIdentifiers, string replacementContent, bool backupOption)
    {
        try
        {
            var fileContent = FileIO.ReadAllText(filePath);
            var startIndex = fileContent.IndexOf(sectionIdentifiers.Start, StringComparison.Ordinal);
            var endIndex = fileContent.IndexOf(sectionIdentifiers.End, startIndex, StringComparison.Ordinal);

            if (startIndex == -1 || endIndex == -1)
            {
                return Error("Section identifiers not found in file.");
            }

            if (backupOption)
            {
                FileIO.Copy(filePath, filePath + ".bak", true);
            }

            var newContent = fileContent.Substring(0, startIndex)
                + replacementContent
                + fileContent.Substring(endIndex + sectionIdentifiers.End.Length);

            FileIO.WriteAllText(filePath, newContent);
            return Ok("Section replaced successfully.");
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }

    private Result<ProjectName> ParseProjectName(string projectName) =>
        from projects in Ok(ListProjects())
        let exists = projects.Contains(projectName)
        select exists
            ? Ok(new ProjectName(projectName))
            : Error($"Unknown project name: {projectName}");

    private Result<ImmutableArray<ProjectItemKind>> GetItems(ProjectName projectName, string path) =>
        from folderNames in GetDirectoryNames(projectName, path)
        let folderItems = folderNames.Select(ProjectFolder)
        from fileNames in GetFileNames(projectName, path)
        let fileItems = from fileName in fileNames
                        let fileInfo = GetFile(projectName, Combine(path, fileName))
                        select fileInfo.Match(
                            ok: ProjectItemKind.ProjectFile.From,
                            error: error => ProjectFileError(fileName, error.Message)
                        )
        select ImmutableArray.Create<ProjectItemKind>([..folderItems, ..fileItems]);

    private Result<ImmutableArray<string>> GetDirectoryNames(ProjectName projectName, string directoryPath = "") =>
        from fullPath in ParseDirectory(projectName, directoryPath)
        let folders = from path in fullPath
                      select from folder in Directory.GetDirectories(path)
                             select Path.GetFileName(folder)
        select ImmutableArray.Create(folders.ToArray());

    private Result<ImmutableArray<string>> GetFileNames(ProjectName projectName, string directoryPath) =>
        from fullPath in ParseDirectory(projectName, directoryPath)
        let files = from path in fullPath
                    select from file in Directory.GetFiles(path)
                           select Path.GetFileName(file)
        select ImmutableArray.Create(files.ToArray());

    /// <summary>
    /// Returns the full path. It's important this isn't leaked to the agent.
    /// </summary>
    /// <param name="projectName"></param>
    /// <param name="directoryPath"></param>
    /// <returns></returns>
    private Result<HiddenString> ParseDirectory(ProjectName projectName, string directoryPath) =>
        from fullPath in Combine(projectDirectory, projectName.Name, directoryPath)
        let exists = Directory.Exists(fullPath)
        select exists
            ? Ok(new HiddenString(fullPath))
            : Error($"Directory does not exist: {directoryPath}");

    /// <summary>
    /// Returns FileInfo containing the full path. It's important this isn't leaked to the agent.
    /// </summary>
    /// <param name="projectName"></param>
    /// <param name="path"></param>
    /// <returns></returns>
    private Result<FileInfo> GetFile(ProjectName projectName, string path) =>
        from fullPath in Combine(projectDirectory, projectName.Name, path)
        let exists = FileIO.Exists(fullPath)
        select exists
            ? Ok(new FileInfo(fullPath))
            : Error("File does not exist");

    private Result<FilePath> ParseFilePath(ProjectName projectName, string filePath) =>
        from fullPath in Combine(projectDirectory, projectName.Name, filePath)
        from info in @try(() => new FileInfo(fullPath))
        select Directory.Exists(info.FullName) is false
            ? Ok(new FilePath(info.Name, info.FullName, info.Exists))
            : Error("Path is a directory");

    private static readonly ImmutableArray<string> ProjectFiles =
        ImmutableArray.Create<string>(["readme.md", "notes.md", "todo.md"]);

    private static string Combine(string left, string path) =>
        left.TrimEnd('/', '\\') + "/" + path.TrimStart('/', '\\', '.');

    private Result<string> Combine(params string[] paths)
    {
        var path = string.Join(Path.DirectorySeparatorChar, paths);
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(projectDirectory) ? Ok(fullPath) : Error("Invalid path");
    }
}
