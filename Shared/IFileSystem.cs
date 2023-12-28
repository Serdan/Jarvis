using Kehlet.Functional;

namespace Shared;

public interface IFileSystem
{
    Result<string> ReadAllText(string path);
    Result<Unit> WriteAllText(string path, string contents);
    bool FileExists(string path);
    Result<Unit> CopyFile(string source, string destination, bool overwrite);
    Result<Unit> AppendAllText(string path, string contents);
    bool DirectoryExists(string path);
    string[] GetFiles(string path);
    string[] GetDirectories(string path);
}
