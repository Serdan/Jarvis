namespace JarvisClient.Models;

[AutoClosed(true)]
public partial record ProjectItemKind
{
    partial record ProjectFile(string Name, long FileSize, DateTimeOffset CreationDate, DateTimeOffset ModificationDate);

    partial record ProjectFolder(string Name);

    partial record ProjectFileError(string Name, string ErrorMessage);

    static partial class Cons
    {
        public static ProjectItemKind NewProjectFile(FileInfo info) =>
            NewProjectFile(info.Name, info.Length, info.CreationTime, info.LastWriteTime);
    }
}
