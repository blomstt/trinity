using System.Numerics;
using System.Text.Json;

namespace Trinity.Scenes;

/// <summary>
/// Behaviour attached to a <see cref="Scenes.Node"/>, like a Godot script:
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
/// Inspector fields can be <c>float</c>, <c>double</c>, <c>int</c>, <c>bool</c>, <c>string</c> or <c>Vector3</c>.
/// </summary>
public abstract class Script
{
    public Node Node { get; internal set; } = null!;
    public Scene Scene { get; internal set; } = null!;
    public Engine Engine => Scene.Engine;
    protected Input Input => Engine.Input;

    /// <summary>Called once, when the scene starts, after every node's script exists.</summary>
    public virtual void Ready() { }

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

    /// <summary>A new instance of the named script with the given field values, or null if there's no such script.</summary>
    public static Script? Create(string name, IReadOnlyDictionary<string, JsonElement>? fields)
    {
        if (!Registered.TryGetValue(name, out var entry))
            return null;
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
        return script;
    }

    /// <summary>Used by the generated setters: a Vector3 from a JSON [x, y, z].</summary>
    public static Vector3 ReadVector3(JsonElement e) =>
        e.ValueKind == JsonValueKind.Array && e.GetArrayLength() == 3
            ? new Vector3(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle())
            : throw new FormatException("expected [x, y, z]");
}
