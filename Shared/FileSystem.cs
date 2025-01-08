using Kehlet.Functional;

namespace Shared;

public class FileSystem : IFileSystem
{
    public Result<string> ReadAllText(string path)
    {
        try
        {
            return ok(File.ReadAllText(path));
        }
        catch (Exception e)
        {
            return error(e);
        }
    }

    public Result<Unit> WriteAllText(string path, string contents)
    {
        try
        {
            File.WriteAllText(path, contents);
            return ok(unit);
        }
        catch (Exception e)
        {
            return error(e);
        }

    }

    public bool FileExists(string path) => File.Exists(path);

    public Result<Unit> CopyFile(string source, string destination, bool overwrite)
    {
        try
        {
            File.Copy(source, destination, overwrite);
            return ok(unit);
        }
        catch (Exception e)
        {
            return error(e);
        }

    }

    public Result<Unit> AppendAllText(string path, string contents)
    {
        try
        {
            File.AppendAllText(path, contents);
            return ok(unit);
        }
        catch (Exception e)
        {
            return error(e);
        }
    }

    public bool DirectoryExists(string path) => Directory.Exists(path);
    public string[] GetFiles(string path) => Directory.GetFiles(path);
    public string[] GetDirectories(string path) => Directory.GetDirectories(path);
}
