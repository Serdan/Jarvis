using System.Text.Json;
using System.Text.Json.Serialization;

namespace JarvisClient.Models;

[AutoClosed]
[JsonConverter(typeof(ProjectItemKindJsonConverter))]
public partial record ProjectItemKind
{
    partial record ProjectFile(string Name, long FileSize, DateTimeOffset CreationDate, DateTimeOffset ModificationDate)
    {
        public string Kind { get; } = nameof(ProjectFile);

        public static ProjectFile From(FileInfo fileInfo) =>
            new(fileInfo.Name, fileInfo.Length, fileInfo.CreationTime, fileInfo.LastWriteTime);
    }

    partial record ProjectFolder(string Name)
    {
        public string Kind { get; } = nameof(ProjectFolder);
    }

    partial record ProjectFileError(string Name, string ErrorMessage)
    {
        public string Kind { get; } = nameof(ProjectFileError);
    }
}

public class ProjectItemKindJsonConverter : JsonConverter<ProjectItemKind>
{
    public override ProjectItemKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var jsonDoc = JsonDocument.ParseValue(ref reader);
        var root = jsonDoc.RootElement;

        // Extract the CommandType property
        if (!root.TryGetProperty("kind", out var typeProp))
        {
            throw new JsonException("Kind property is missing.");
        }

        var commandType = typeProp.GetString();

        // Deserialize to the specific type based on CommandType
        return commandType switch
        {
            nameof(ProjectItemKind.ProjectFile) => JsonSerializer.Deserialize<ProjectItemKind.ProjectFile>(root.GetRawText(), options)!,
            nameof(ProjectItemKind.ProjectFolder) => JsonSerializer.Deserialize<ProjectItemKind.ProjectFolder>(root.GetRawText(), options)!,
            _ => throw new JsonException($"Unknown CommandType: {commandType}")
        };
    }

    public override void Write(Utf8JsonWriter writer, ProjectItemKind value, JsonSerializerOptions options)
    {
        var type = value.GetType();

        writer.WriteStartObject();

        // Serialize properties
        foreach (var prop in type.GetProperties())
        {
            writer.WritePropertyName(prop.Name);
            JsonSerializer.Serialize(writer, prop.GetValue(value), prop.PropertyType, options);
        }

        writer.WriteEndObject();
    }
}
