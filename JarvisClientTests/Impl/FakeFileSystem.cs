using JarvisClient.Models;
using Kehlet.Functional;
using Shared;

namespace JarvisClientTests.Impl;

using static ProjectItemKind.Cons;

public class FakeFileSystem : IFileSystem
{
    private readonly Dictionary<string, (ProjectItemKind Item, string Content)> files = new();

    private static (ProjectItemKind Name, string Content) CreateFile(string path, string content)
    {
        return (NewProjectFile(Path.GetFileName(path), 1, DateTimeOffset.Now, DateTimeOffset.Now), content);
    }

    private static (ProjectItemKind Item, string) CreateDirectory(string name)
    {
        return (NewProjectFolder(name), "");
    }

    public void AddFile(string path, string content)
    {
        files[path] = CreateFile(path, content);
    }

    public void AddFolder(string path, string name)
    {
        files[path] = CreateDirectory(name);
    }

    public string ReadAllText(string path)
    {
        if (files.TryGetValue(path, out var contents))
        {
            return contents.Content;
        }
        else
        {
            throw new FileNotFoundException("File not found", path);
        }
    }

    public Unit WriteAllText(string path, string contents)
    {
        files[path] = CreateFile(path, contents);
        return unit;
    }

    public bool FileExists(string path)
    {
        return files.ContainsKey(path);
    }

    public Unit CopyFile(string source, string destination, bool overwrite)
    {
        if (files.TryGetValue(source, out var sourceContent) is false)
        {
            throw new FileNotFoundException("File not found", source);
        }

        var destExists = files.ContainsKey(destination);
        if (overwrite is false && destExists)
        {
            return unit;
        }

        files[destination] = sourceContent;
        return unit;
    }

    public Unit AppendAllText(string path, string contents)
    {
        if (files.TryGetValue(path, out var content) is false)
        {
            throw new FileNotFoundException("File not found", path);
        }

        var file = files[path];
        files[path] = file with { Content = content.Content + contents };

        return unit;
    }

    public bool DirectoryExists(string path)
    {
        return files.Any(x => x.Value.Item is ProjectItemKind.ProjectFolder && x.Key.StartsWith(path));
    }

    public string[] GetFiles(string path)
    {
        return files.Where(x => x.Value.Content.StartsWith(path))
                    .Select(x => x.Value.Content)
                    .ToArray();
    }

    public string[] GetDirectories(string path)
    {
        return (from file in files
                let item = file.Value.Item as ProjectItemKind.ProjectFolder
                where item is not null
                where file.Key == path
                select file.Key + "\\" + item.Name)
            .ToArray();
    }
}
