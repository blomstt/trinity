using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trinity.Scenes;

/// <summary>
/// One thing in a scene: a model placed in the world, optionally playing an animation clip and running a
/// <see cref="Scenes.Script"/>. The same type is saved in scene files, edited by Trinity Editor and run by games.
/// </summary>
public sealed class Node
{
    public string Name { get; set; } = "Node";
    public string Model { get; set; } = ""; // relative to the project root
    public Vector3 Position { get; set; }
    public Vector3 Rotation { get; set; } // degrees, X then Y then Z
    public float Scale { get; set; } = 1;
    public string Clip { get; set; } = "";
    public float Speed { get; set; } = 1;
    public bool Playing { get; set; } = true;

    /// <summary>Class name of the script attached, if any.</summary>
    public string? Script { get; set; }

    /// <summary>The script's field values set in the editor; fields not listed keep the script's defaults.</summary>
    public Dictionary<string, JsonElement>? Fields { get; set; }

    /// <summary>Seconds into the clip; advanced while <see cref="Playing"/>.</summary>
    [JsonIgnore]
    public float Time;

    /// <summary>The running script, once the scene has started.</summary>
    [JsonIgnore]
    public Script? Behaviour { get; internal set; }

    public Matrix4x4 Transform =>
        Matrix4x4.CreateScale(Scale)
        * Matrix4x4.CreateRotationX(float.DegreesToRadians(Rotation.X))
        * Matrix4x4.CreateRotationY(float.DegreesToRadians(Rotation.Y))
        * Matrix4x4.CreateRotationZ(float.DegreesToRadians(Rotation.Z))
        * Matrix4x4.CreateTranslation(Position);

    /// <summary>A copy of the saved state, without the running script.</summary>
    public Node Clone() =>
        new()
        {
            Name = Name,
            Model = Model,
            Position = Position,
            Rotation = Rotation,
            Scale = Scale,
            Clip = Clip,
            Speed = Speed,
            Playing = Playing,
            Script = Script,
            Fields = Fields == null ? null : new Dictionary<string, JsonElement>(Fields),
        };
}

/// <summary>Where a scene's camera starts: orbiting <see cref="Target"/>, angles in degrees.</summary>
public sealed class SceneCamera
{
    public Vector3 Target { get; set; } = new(0, 1, 0);
    public float Yaw { get; set; } = 45;
    public float Pitch { get; set; } = 20;
    public float Distance { get; set; } = 8;
}

/// <summary>A scene's light and look: the sun, the sky, shadows, fog and exposure. Angles in degrees.</summary>
public sealed class SceneEnvironment
{
    /// <summary>Clockwise from north (-Z).</summary>
    public float SunAzimuth { get; set; } = 220;
    /// <summary>Above the horizon; negative is night.</summary>
    public float SunElevation { get; set; } = 50;
    public float SunIlluminance { get; set; } = 6;
    public float Haze { get; set; } = 1;
    public bool Shadows { get; set; } = true;
    public float ShadowDistance { get; set; } = 80;
    /// <summary>Exponential fog: how quickly it thickens with distance (per metre); 0 for none.</summary>
    public float FogDensity { get; set; } = 0.002f;
    /// <summary>How fast fog thins with height (per metre); 0 for even fog at every height.</summary>
    public float FogHeightFalloff { get; set; } = 0.05f;
    /// <summary>Exposure compensation in photographic stops: +1 is twice as bright.</summary>
    public float Exposure { get; set; }
    /// <summary>"AgX" or "ACES".</summary>
    public string ToneMapper { get; set; } = "AgX";

    public void ApplyTo(Rendering.RenderSettings s)
    {
        s.SunDirection = Rendering.RenderSettings.SunAt(SunAzimuth, SunElevation);
        s.SunIlluminance = SunIlluminance;
        s.Haze = Haze;
        s.Shadows = Shadows;
        s.ShadowDistance = ShadowDistance;
        s.FogDensity = FogDensity;
        s.FogHeightFalloff = FogHeightFalloff;
        s.ExposureStops = Exposure;
        s.ToneMapper = ToneMapper.Equals("ACES", StringComparison.OrdinalIgnoreCase) ? Rendering.ToneMapper.Aces : Rendering.ToneMapper.AgX;
    }
}

/// <summary>A scene as saved: <c>*.scene.json</c>, its nodes, its environment and where the camera starts.</summary>
public sealed class SceneFile
{
    public List<Node> Objects { get; set; } = [];
    public SceneEnvironment Environment { get; set; } = new();
    public SceneCamera? Camera { get; set; }

    public static SceneFile Load(string path) =>
        JsonSerializer.Deserialize(File.ReadAllText(path), SceneJson.Default.SceneFile) ?? new SceneFile();

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, SceneJson.Default.SceneFile));
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(Vector3Converter)]
)]
[JsonSerializable(typeof(SceneFile))]
sealed partial class SceneJson : JsonSerializerContext;

/// <summary>Vectors as [x, y, z] arrays, which read better in scene files.</summary>
public sealed class Vector3Converter : JsonConverter<Vector3>
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
