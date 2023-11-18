using Kehlet.Functional;

namespace Shared;

public interface IFileSystem
{
    string ReadAllText(string path);
    Unit WriteAllText(string path, string contents);
    bool FileExists(string path);
    Unit CopyFile(string source, string destination, bool overwrite);
    // Add other necessary file system operations
    Unit AppendAllText(string path, string contents);
    bool DirectoryExists(string path);
    string[] GetFiles(string path);
    string[] GetDirectories(string path);
}
