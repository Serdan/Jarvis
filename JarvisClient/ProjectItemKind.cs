using System.Text.Json;
using System.Text.Json.Serialization;

namespace JarvisClient;

[AutoClosed]
[JsonConverter(typeof(ProjectItemKindJsonConverter))]
public abstract partial record ProjectItemKind
{
    public sealed partial record File(string Name)
    {
        public string Kind { get; } = nameof(File);
    }

    public sealed partial record Folder(string Name)
    {
        public string Kind { get; } = nameof(Folder);
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
            nameof(ProjectItemKind.File) => JsonSerializer.Deserialize<ProjectItemKind.File>(root.GetRawText(), options)!,
            nameof(ProjectItemKind.Folder) => JsonSerializer.Deserialize<ProjectItemKind.Folder>(root.GetRawText(), options)!,
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
