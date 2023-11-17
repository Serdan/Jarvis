namespace Shared;

public interface IFileSystem
{
    string ReadAllText(string path);
    void WriteAllText(string path, string contents);
    bool FileExists(string path);
    void CopyFile(string source, string destination, bool overwrite);
    // Add other necessary file system operations
    void AppendAllText(string path, string contents);
    void Copy(string sourceFileName, string destFileName, bool overwrite);
}
