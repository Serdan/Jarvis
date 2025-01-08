using Shared;

namespace JarvisClient.Models;

public class ProjectDirectory(string value)
{
    private readonly string value = Path.GetFullPath(value);

    public bool Contains(string path)
    {
        return path.StartsWith(value);
    }

    /// <summary>
    ///     Combines multiple string paths into a single path, ensuring it is a valid path within the project directory.
    /// </summary>
    /// <param name="paths">An array of path segments to be combined.</param>
    /// <returns>A Result containing the combined full path or an error message if the path is invalid.</returns>
    public Result<string> Join(params string[] paths)
    {
        return from path in @try(() => string.Join(Path.DirectorySeparatorChar, (string[])[value, .. CleanPaths(paths)]))
               from fullPath in @try(() => Path.GetFullPath(path))
               select Contains(fullPath)
                   ? ok(fullPath)
                   : error("Invalid path");
    }

    public IEnumerable<string> GetDirectories(IFileSystem fileSystem)
    {
        return fileSystem.GetDirectories(value);
    }

    public override string ToString()
    {
        return nameof(ProjectDirectory);
    }

    private static IEnumerable<string> CleanPaths(IEnumerable<string> paths)
    {
        return paths.Select(x => x.Trim('/', '\\'))
                    .Where(x => !string.IsNullOrWhiteSpace(x));
    }
}
