using Kehlet.Functional;

namespace Shared;

public class FileSystem : IFileSystem
{
    public string ReadAllText(string path) => File.ReadAllText(path);

    public Unit WriteAllText(string path, string contents)
    {
        File.WriteAllText(path, contents);
        return unit;
    }

    public bool FileExists(string path) => File.Exists(path);

    public Unit CopyFile(string source, string destination, bool overwrite)
    {
        File.Copy(source, destination, overwrite);
        return unit;
    }

    public Unit AppendAllText(string path, string contents)
    {
        File.AppendAllText(path, contents);
        return unit;
    }

    public bool DirectoryExists(string path) => Directory.Exists(path);
    public string[] GetFiles(string path) => Directory.GetFiles(path);
    public string[] GetDirectories(string path) => Directory.GetDirectories(path);
}
