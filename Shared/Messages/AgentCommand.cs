using System.Text.Json;
using System.Text.Json.Serialization;
using ExhaustiveMatching;
using static Shared.Messages.AgentCommand;

namespace Shared.Messages;

[AutoClosed]
[JsonConverter(typeof(AgentCommandJsonConverter))]
public partial record AgentCommand
{
    partial record ListProjectsCommand
    {
        public string Kind { get; } = nameof(ListProjectsCommand);
    }

    partial record GetProjectDetailsCommand(string ProjectName)
    {
        public string Kind { get; } = nameof(GetProjectDetailsCommand);
    }

    partial record ListProjectDirectoryCommand(string ProjectName, string Path)
    {
        public string Kind { get; } = nameof(ListProjectDirectoryCommand);
    }

    partial record OpenFileCommand(string ProjectName, string Path)
    {
        public string Kind { get; } = nameof(OpenFileCommand);
    }

    partial record WriteFileCommand(string ProjectName, string FilePath, string Content, FileWriteMode Mode)
    {
        public string Kind { get; } = nameof(WriteFileCommand);
    }

    partial record SectionReplaceCommand(string FilePath, SectionIdentifiers SectionIdentifiers, string ReplacementContent, bool BackupOption)
    {
        public string Kind { get; } = nameof(SectionReplaceCommand);
    }
}

file class AgentCommandJsonConverter : JsonConverter<AgentCommand>
{
    public override AgentCommand Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var jsonDoc = JsonDocument.ParseValue(ref reader);
        var root = jsonDoc.RootElement;

        // Extract the CommandType property
        if (!root.TryGetProperty("kind", out var typeProp) &&
            !root.TryGetProperty("Kind", out typeProp))
        {
            throw new JsonException("Kind property is missing.");
        }

        var commandType = typeProp.GetString();

        // Deserialize to the specific type based on CommandType
        return commandType switch
        {
            nameof(ListProjectsCommand) => JsonSerializer.Deserialize<ListProjectsCommand>(root.GetRawText(), options)!,
            nameof(GetProjectDetailsCommand) => JsonSerializer.Deserialize<GetProjectDetailsCommand>(root.GetRawText(), options)!,
            nameof(ListProjectDirectoryCommand) => JsonSerializer.Deserialize<ListProjectDirectoryCommand>(root.GetRawText(), options)!,
            nameof(OpenFileCommand) => JsonSerializer.Deserialize<OpenFileCommand>(root.GetRawText(), options)!,
            nameof(WriteFileCommand) => JsonSerializer.Deserialize<WriteFileCommand>(root.GetRawText(), options)!,
            nameof(SectionReplaceCommand) => JsonSerializer.Deserialize<SectionReplaceCommand>(root.GetRawText(), options)!,
            _ => throw new JsonException($"Unknown CommandType: {commandType}")
        };
    }

    public override void Write(Utf8JsonWriter writer, AgentCommand value, JsonSerializerOptions options)
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
