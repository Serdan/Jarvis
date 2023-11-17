namespace JarvisClient.Models;

public record ProjectName(string Name)
{
    public override string ToString() => Name;
}
