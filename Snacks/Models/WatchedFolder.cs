using System.Text.Json;
using System.Text.Json.Serialization;

namespace Snacks.Models;

/// <summary>
///     A watched directory with optional per-folder encoding overrides.
///     Replaces the plain string entries previously used in <see cref="AutoScanConfig.Directories"/>.
/// </summary>
public sealed class WatchedFolder
{
    /// <summary> Absolute path of the watched directory. </summary>
    public string Path { get; set; } = "";

    /// <summary>
    ///     Optional encoding settings that override the global defaults for files in this folder.
    ///     Null fields inherit from the global <see cref="EncoderOptions"/>.
    /// </summary>
    public EncoderOptionsOverride? EncodingOverrides { get; set; }
}

/// <summary>
///     JSON converter that deserializes both the legacy format (plain string array)
///     and the new format (object array) for <see cref="AutoScanConfig.Directories"/>.
///     Always serializes in the new object format.
/// </summary>
public sealed class WatchedFolderListConverter : JsonConverter<List<WatchedFolder>>
{
    public override List<WatchedFolder> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var list = new List<WatchedFolder>();

        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Expected start of array for Directories.");

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
                break;

            if (reader.TokenType == JsonTokenType.String)
            {
                // Legacy format: plain path string
                list.Add(new WatchedFolder { Path = reader.GetString() ?? "" });
            }
            else if (reader.TokenType == JsonTokenType.StartObject)
            {
                // New format: WatchedFolder object
                var folder = JsonSerializer.Deserialize<WatchedFolder>(ref reader, options);
                if (folder != null)
                    list.Add(folder);
            }
            else
            {
                throw new JsonException($"Unexpected token {reader.TokenType} in Directories array.");
            }
        }

        return list;
    }

    public override void Write(Utf8JsonWriter writer, List<WatchedFolder> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var folder in value)
            JsonSerializer.Serialize(writer, folder, options);
        writer.WriteEndArray();
    }
}
