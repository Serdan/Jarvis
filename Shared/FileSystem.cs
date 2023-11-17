namespace Shared;

public class FileSystem : IFileSystem
{
    public string ReadAllText(string path) => File.ReadAllText(path);
    public void WriteAllText(string path, string contents) => File.WriteAllText(path, contents);
    public bool FileExists(string path) => File.Exists(path);
    public void CopyFile(string source, string destination, bool overwrite) => File.Copy(source, destination, overwrite);
    public void AppendAllText(string path, string contents) => File.AppendAllText(path, contents);
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public string[] GetFiles(string path) => Directory.GetFiles(path);
    public string[] GetDirectories(string path) => Directory.GetDirectories(path);
}
