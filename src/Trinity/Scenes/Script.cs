using System.Numerics;
using System.Text.Json;

namespace Trinity.Scenes;

/// <summary>
/// Behaviour attached to a <see cref="Scenes.Node"/>:
/// <code>
/// public sealed class Spin : Script
/// {
///     public float DegreesPerSecond = 90; // public fields show in the editor's Inspector
///
///     public override void Update(in FrameTime time) =>
///         Node.Rotation += new Vector3(0, DegreesPerSecond * time.Delta, 0);
/// }
/// </code>
/// Scripts are found at build time (Trinity.Generators), not by reflection, so they work under NativeAOT.
/// Inspector fields can be <c>float</c>, <c>double</c>, <c>int</c>, <c>bool</c>, <c>string</c>,
/// <c>Vector3</c> or <c>Node</c> (a reference to another node, kept across saves).
/// <para>
/// Order: when a scene starts, every script is created first, then <see cref="Ready"/> runs children
/// before parents, so a parent's Ready sees its children ready. Updates run in the order
/// scripts were created. <see cref="Destroyed"/> runs before the node goes; after that the script gets
/// no more calls, and <see cref="Node"/> is no longer valid.
/// </para>
/// </summary>
public abstract class Script
{
    /// <summary>The node this script is on. (A field, so <c>Node.Position = ...</c> works; don't assign it.)</summary>
    public Node Node;

    public Scene Scene { get; internal set; } = null!;
    public World World => Scene.World;
    public Engine Engine => Scene.Engine;
    protected Input Input => Engine.Input;

    /// <summary>Called once, after every script in the scene exists; children before their parents.</summary>
    public virtual void Ready() { }

    /// <summary>Called when the node is destroyed (or the scene unloads), while it still exists.</summary>
    public virtual void Destroyed() { }

    /// <summary>Once per frame.</summary>
    public virtual void Update(in FrameTime time) { }

    /// <summary>At the engine's fixed rate (60 a second), for movement and simulation.</summary>
    public virtual void FixedUpdate(in FrameTime time) { }
}

/// <summary>
/// Every script class in the game, registered at start-up by code that Trinity.Generators writes at build
/// time. Games don't call this themselves.
/// </summary>
public static class Scripts
{
    public delegate void FieldSetter(Script script, JsonElement value);

    sealed record Entry(Func<Script> Create, Dictionary<string, FieldSetter> Fields);

    static readonly Dictionary<string, Entry> Registered = [];

    public static IEnumerable<string> Names => Registered.Keys;

    public static void Register(string name, Func<Script> create, params (string Name, FieldSetter Set)[] fields) =>
        Registered[name] = new Entry(create, fields.ToDictionary(f => f.Name, f => f.Set));

    [ThreadStatic]
    static World? _world;

    /// <summary>A new instance of the named script with the given field values, or null if there's no such script.</summary>
    public static Script? Create(string name, IReadOnlyDictionary<string, JsonElement>? fields, World? world = null)
    {
        if (!Registered.TryGetValue(name, out var entry))
            return null;
        _world = world;
        var script = entry.Create();
        foreach (var (field, value) in fields ?? new Dictionary<string, JsonElement>())
        {
            if (!entry.Fields.TryGetValue(field, out var set))
            {
                Log.Warn("scene", $"{name} has no field '{field}' (renamed or removed?)");
                continue;
            }
            try
            {
                set(script, value);
            }
            catch (Exception e) when (e is InvalidOperationException or FormatException)
            {
                Log.Warn("scene", $"{name}.{field}: can't use {value.GetRawText()} ({e.Message})");
            }
        }
        _world = null;
        return script;
    }

    /// <summary>Used by the generated setters: a node reference (resolved when the scene loaded).</summary>
    public static Node ReadNode(JsonElement e)
    {
        var id = SceneSchema.ReadEntity(e);
        return _world != null && _world.IsAlive(id) ? new Node(_world, id) : Node.None;
    }

    /// <summary>Used by the generated setters: a Vector3 from a JSON [x, y, z].</summary>
    public static Vector3 ReadVector3(JsonElement e) =>
        e.ValueKind == JsonValueKind.Array && e.GetArrayLength() == 3
            ? new Vector3(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle())
            : throw new FormatException("expected [x, y, z]");
}
