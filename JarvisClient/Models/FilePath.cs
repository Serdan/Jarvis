namespace JarvisClient.Models;

public record FilePath(string Name, string FullName, bool Exists)
{
    public override string ToString() => Name;
}
