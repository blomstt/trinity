using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trinity.Scenes;

/// <summary>
/// A node in a scene, as scripts and game code use it: a light handle (a world and an entity id) with
/// the node's name, transform, hierarchy and components. Copying it copies the handle, not the node.
/// Using a node after it's destroyed throws, and <see cref="IsValid"/> says whether it still exists.
/// <code>
/// Node.Position += new Vector3(0, 1, 0);
/// var door = Node.Find("House/Front door");
/// ref var animator = ref door.Get&lt;Animator&gt;();
/// </code>
/// </summary>
public readonly struct Node(World world, EntityId id) : IEquatable<Node>
{
    public readonly World World = world;
    public readonly EntityId Id = id;

    public static readonly Node None = default;

    public bool IsValid => World != null && World.IsAlive(Id);

    public string Name
    {
        get => World.Name(Id);
        set => World.SetName(Id, value);
    }

    /// <summary>Position relative to the parent.</summary>
    public Vector3 Position
    {
        get => World.Position(Id);
        set => World.SetPosition(Id, value);
    }

    /// <summary>Rotation relative to the parent, in degrees, applied X, then Y, then Z.</summary>
    public Vector3 Rotation
    {
        get => World.Rotation(Id);
        set => World.SetRotation(Id, value);
    }

    public Vector3 Scale
    {
        get => World.Scale(Id);
        set => World.SetScale(Id, value);
    }

    public bool Hidden
    {
        get => World.Hidden(Id);
        set => World.SetHidden(Id, value);
    }

    public Matrix4x4 WorldMatrix => World.WorldMatrix(Id);
    public Vector3 WorldPosition => World.WorldPosition(Id);

    /// <summary>The way the node faces in the world: its -Z axis.</summary>
    public Vector3 Forward
    {
        get
        {
            var m = WorldMatrix;
            return Vector3.Normalize(-new Vector3(m.M31, m.M32, m.M33));
        }
    }

    public Node Parent => new(World, World.Parent(Id));

    public List<Node> Children
    {
        get
        {
            var list = new List<Node>();
            foreach (var c in World.Children(Id))
                list.Add(new Node(World, c));
            return list;
        }
    }

    /// <summary>The first child with this name, or <see cref="None"/>.</summary>
    public Node Child(string name)
    {
        foreach (var c in World.Children(Id))
            if (World.Name(c) == name)
                return new Node(World, c);
        return None;
    }

    /// <summary>A descendant by a path of child names, "House/Front door"; <see cref="None"/> if there isn't one.</summary>
    public Node Find(string path)
    {
        var node = this;
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            node = node.Child(part);
            if (!node.IsValid)
                return None;
        }
        return node;
    }

    public Node AddChild(string name = "Node") => new(World, World.Create(name, Id));

    public void SetParent(Node parent, bool keepWorld = false) => World.SetParent(Id, parent.Id, keepWorld: keepWorld);

    public bool Has<T>() where T : struct => World.Has<T>(Id);
    public ref T Get<T>() where T : struct => ref World.Get<T>(Id);
    public ref T Add<T>(in T component) where T : struct => ref World.Add(Id, component);
    public void Remove<T>() where T : struct => World.Remove<T>(Id);

    /// <summary>Destroys the node and everything under it (at the end of the current update, if one is running).</summary>
    public void Destroy() => World.Destroy(Id);

    /// <summary>The animation clip playing (via the <see cref="Animator"/>, added if needed).</summary>
    public string Clip
    {
        get => Has<Animator>() ? Get<Animator>().Clip ?? "" : "";
        set
        {
            if (Has<Animator>())
                Get<Animator>().Clip = value;
            else
                Add(new Animator(value));
        }
    }

    public bool Equals(Node other) => ReferenceEquals(World, other.World) && Id == other.Id;
    public override bool Equals(object? obj) => obj is Node n && Equals(n);
    public override int GetHashCode() => Id.GetHashCode();
    public static bool operator ==(Node a, Node b) => a.Equals(b);
    public static bool operator !=(Node a, Node b) => !a.Equals(b);
    public override string ToString() => IsValid ? $"{Name} {Id}" : $"(no node) {Id}";
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

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(Vector3Converter)]
)]
[JsonSerializable(typeof(SceneDocument))]
[JsonSerializable(typeof(SceneEnvironment))]
[JsonSerializable(typeof(SceneCamera))]
[JsonSerializable(typeof(string))]
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
