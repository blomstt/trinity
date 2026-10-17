using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trinity.Editor;

/// <summary>The editor's last project, kept in its settings.</summary>
sealed class RecentProjects
{
    public string? Last { get; set; }
}

/// <summary>
/// JSON metadata for everything the editor reads and writes, generated at build time so the
/// editor runs under NativeAOT without reflection.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true, Converters = [typeof(Vector3Converter)])]
[JsonSerializable(typeof(EditorProject))]
[JsonSerializable(typeof(EditorScene))]
[JsonSerializable(typeof(RecentProjects))]
sealed partial class EditorJson : JsonSerializerContext;

/// <summary>Vectors as [x, y, z] arrays, which read better in scene files.</summary>
sealed class Vector3Converter : JsonConverter<Vector3>
{
    public override Vector3 Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("expected [x, y, z]");
        Span<float> v = stackalloc float[3];
        var n = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (n < 3)
                v[n] = reader.GetSingle();
            n++;
        }
        return n == 3 ? new Vector3(v[0], v[1], v[2]) : Vector3.Zero;
    }

    public override void Write(Utf8JsonWriter writer, Vector3 value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.X);
        writer.WriteNumberValue(value.Y);
        writer.WriteNumberValue(value.Z);
        writer.WriteEndArray();
    }
}
