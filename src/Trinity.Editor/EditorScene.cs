using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trinity.Editor;

/// <summary>One placed model. Saved in scene files; <see cref="Time"/> is only the live animation clock.</summary>
sealed class SceneObject
{
    public string Name { get; set; } = "Object";
    public string Model { get; set; } = ""; // relative to the project root
    public Vector3 Position { get; set; }
    public Vector3 Rotation { get; set; } // degrees, X then Y then Z
    public float Scale { get; set; } = 1;
    public string Clip { get; set; } = "";
    public float Speed { get; set; } = 1;
    public bool Playing { get; set; } = true;

    [JsonIgnore]
    public float Time;

    public Matrix4x4 Transform =>
        Matrix4x4.CreateScale(Scale)
        * Matrix4x4.CreateRotationX(float.DegreesToRadians(Rotation.X))
        * Matrix4x4.CreateRotationY(float.DegreesToRadians(Rotation.Y))
        * Matrix4x4.CreateRotationZ(float.DegreesToRadians(Rotation.Z))
        * Matrix4x4.CreateTranslation(Position);
}

/// <summary>
/// The editor's scene until Trinity's scene system (roadmap E3) replaces it: a list of placed
/// models, saved as <c>*.scene.json</c>.
/// </summary>
sealed class EditorScene
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new Vector3Converter() },
    };

    public List<SceneObject> Objects { get; set; } = [];

    [JsonIgnore]
    public string? Path;

    [JsonIgnore]
    public bool Dirty;

    public static EditorScene Load(string path)
    {
        var scene = JsonSerializer.Deserialize<EditorScene>(File.ReadAllText(path), Json) ?? new EditorScene();
        scene.Path = path;
        return scene;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        Path = path;
        Dirty = false;
    }

    /// <summary>Vectors as [x, y, z] arrays, which read better in scene files.</summary>
    sealed class Vector3Converter : JsonConverter<Vector3>
    {
        public override Vector3 Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            var v = JsonSerializer.Deserialize<float[]>(ref reader, options) ?? [];
            return v.Length == 3 ? new Vector3(v[0], v[1], v[2]) : Vector3.Zero;
        }

        public override void Write(Utf8JsonWriter writer, Vector3 value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, new[] { value.X, value.Y, value.Z }, options);
    }
}
