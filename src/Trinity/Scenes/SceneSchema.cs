using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace Trinity.Scenes;

public enum PropertyKind
{
    Text,
    Float,
    Int,
    Bool,
    Vector3,
    /// <summary>A project-relative path to an asset (a model, a texture, a scene).</summary>
    Asset,
    /// <summary>Another entity in the scene: saved as its persistent id path, live as an <see cref="EntityId"/>.</summary>
    Entity,
}

/// <summary>A saved, editable property: how to read and write it as JSON on an entity.</summary>
public sealed record PropertyDef(string Name, PropertyKind Kind, Func<World, EntityId, JsonElement?> Get, Action<World, EntityId, JsonElement> Set);

/// <summary>A component type as scenes, the inspector and overrides see it.</summary>
public sealed record ComponentDef(
    string Name,
    Func<World, EntityId, bool> Has,
    Action<World, EntityId> Add,
    Action<World, EntityId> Remove,
    PropertyDef[] Properties
);

/// <summary>
/// Every property a scene saves, described once: node properties (name, transform, hidden) and each
/// built-in component's. Serialization, instance overrides, the editor's inspector and undo all go
/// through these, so a property behaves the same everywhere. Written by hand, without reflection,
/// so it works under NativeAOT. A script's fields are properties too: "Script.Fields.Speed".
/// </summary>
public static class SceneSchema
{
    public static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    public static JsonElement Float(float v) => Json(v.ToString("R", CultureInfo.InvariantCulture));
    public static JsonElement Int(int v) => Json(v.ToString(CultureInfo.InvariantCulture));
    public static JsonElement Bool(bool v) => Json(v ? "true" : "false");
    public static JsonElement Text(string v) => Json(JsonSerializer.Serialize(v, SceneJson.Default.String));
    public static JsonElement Vector(Vector3 v) =>
        Json($"[{v.X.ToString("R", CultureInfo.InvariantCulture)}, {v.Y.ToString("R", CultureInfo.InvariantCulture)}, {v.Z.ToString("R", CultureInfo.InvariantCulture)}]");

    /// <summary>A live entity reference, as kept in memory after loading (files hold <c>{"$entity": "path"}</c> instead).</summary>
    public static JsonElement EntityRef(EntityId e) => Json($"{{\"$id\": [{e.Index}, {e.Generation}]}}");

    public static float ReadFloat(JsonElement v) => v.GetSingle();
    public static Vector3 ReadVector(JsonElement v) =>
        v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == 3
            ? new Vector3(v[0].GetSingle(), v[1].GetSingle(), v[2].GetSingle())
            : throw new FormatException("expected [x, y, z]");

    public static EntityId ReadEntity(JsonElement v) =>
        v.ValueKind == JsonValueKind.Object && v.TryGetProperty("$id", out var id) && id.GetArrayLength() == 2
            ? new EntityId(id[0].GetInt32(), id[1].GetInt32())
            : EntityId.None;

    /// <summary>Whether a value has the right JSON shape for a kind (in a file, entity references are <c>{"$entity": "path"}</c>).</summary>
    public static bool Fits(PropertyKind kind, JsonElement v) => kind switch
    {
        PropertyKind.Text or PropertyKind.Asset => v.ValueKind == JsonValueKind.String,
        PropertyKind.Float => v.ValueKind == JsonValueKind.Number,
        PropertyKind.Int => v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out _),
        PropertyKind.Bool => v.ValueKind is JsonValueKind.True or JsonValueKind.False,
        PropertyKind.Vector3 => v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == 3 && v.EnumerateArray().All(e => e.ValueKind == JsonValueKind.Number),
        PropertyKind.Entity => v.ValueKind == JsonValueKind.Object && (v.TryGetProperty("$entity", out _) || v.TryGetProperty("$id", out _)) || v.ValueKind == JsonValueKind.Null,
        _ => false,
    };

    public static readonly PropertyDef[] NodeProperties =
    [
        new("Name", PropertyKind.Text, (w, e) => Text(w.Name(e)), (w, e, v) => w.SetName(e, v.GetString() ?? "")),
        new("Position", PropertyKind.Vector3, (w, e) => Vector(w.Position(e)), (w, e, v) => w.SetPosition(e, ReadVector(v))),
        new("Rotation", PropertyKind.Vector3, (w, e) => Vector(w.Rotation(e)), (w, e, v) => w.SetRotation(e, ReadVector(v))),
        new("Scale", PropertyKind.Vector3, (w, e) => Vector(w.Scale(e)), (w, e, v) => w.SetScale(e, ReadVector(v))),
        new("Hidden", PropertyKind.Bool, (w, e) => Bool(w.Hidden(e)), (w, e, v) => w.SetHidden(e, v.GetBoolean())),
        new("Locked", PropertyKind.Bool, (w, e) => Bool(w.Locked(e)), (w, e, v) => w.SetLocked(e, v.GetBoolean())),
    ];

    static ComponentDef Def<T>(string name, params PropertyDef[] properties) where T : struct =>
        new(name, (w, e) => w.Has<T>(e), (w, e) => { if (!w.Has<T>(e)) w.Add(e, new T()); }, (w, e) => w.Remove<T>(e), properties);

    public static readonly ComponentDef[] Components =
    [
        Def<MeshRenderer>("MeshRenderer",
            new("Mesh", PropertyKind.Asset, (w, e) => Text(w.Get<MeshRenderer>(e).Mesh ?? ""), (w, e, v) => w.Get<MeshRenderer>(e).Mesh = v.GetString() ?? ""),
            new("NoShadows", PropertyKind.Bool, (w, e) => Bool(w.Get<MeshRenderer>(e).NoShadows), (w, e, v) => w.Get<MeshRenderer>(e).NoShadows = v.GetBoolean())),
        Def<Animator>("Animator",
            new("Clip", PropertyKind.Text, (w, e) => Text(w.Get<Animator>(e).Clip ?? ""), (w, e, v) => w.Get<Animator>(e).Clip = v.GetString() ?? ""),
            new("Speed", PropertyKind.Float, (w, e) => Float(w.Get<Animator>(e).Speed), (w, e, v) => w.Get<Animator>(e).Speed = v.GetSingle()),
            new("Paused", PropertyKind.Bool, (w, e) => Bool(w.Get<Animator>(e).Paused), (w, e, v) => w.Get<Animator>(e).Paused = v.GetBoolean()),
            new("Once", PropertyKind.Bool, (w, e) => Bool(w.Get<Animator>(e).Once), (w, e, v) => w.Get<Animator>(e).Once = v.GetBoolean())),
        Def<Camera3D>("Camera",
            new("FieldOfView", PropertyKind.Float, (w, e) => Float(w.Get<Camera3D>(e).FieldOfViewOrDefault), (w, e, v) => w.Get<Camera3D>(e).FieldOfView = v.GetSingle()),
            new("Near", PropertyKind.Float, (w, e) => Float(w.Get<Camera3D>(e).NearOrDefault), (w, e, v) => w.Get<Camera3D>(e).Near = v.GetSingle()),
            new("Far", PropertyKind.Float, (w, e) => Float(w.Get<Camera3D>(e).FarOrDefault), (w, e, v) => w.Get<Camera3D>(e).Far = v.GetSingle()),
            new("Current", PropertyKind.Bool, (w, e) => Bool(w.Get<Camera3D>(e).Current), (w, e, v) => w.Get<Camera3D>(e).Current = v.GetBoolean())),
        Def<DirectionalLight>("DirectionalLight",
            new PropertyDef("Illuminance", PropertyKind.Float, (w, e) => Float(w.Get<DirectionalLight>(e).Illuminance), (w, e, v) => w.Get<DirectionalLight>(e).Illuminance = v.GetSingle())),
        Def<ScriptComponent>("Script",
            new PropertyDef("Class", PropertyKind.Text, (w, e) => Text(w.Get<ScriptComponent>(e).Class ?? ""), (w, e, v) => w.Get<ScriptComponent>(e).Class = v.GetString() ?? "")),
    ];

    public static ComponentDef? Component(string name) => Array.Find(Components, c => c.Name == name);

    public const string FieldsPrefix = "Fields.";

    /// <summary>
    /// Reads a property by its path: a node property ("Position") or a component's ("MeshRenderer.Mesh",
    /// "Script.Fields.Speed"). Null if the entity doesn't have it.
    /// </summary>
    public static JsonElement? Get(World w, EntityId e, string path)
    {
        var (component, property) = Split(path);
        if (component == null)
            return Array.Find(NodeProperties, p => p.Name == property)?.Get(w, e);
        if (Component(component) is not { } def || !def.Has(w, e))
            return null;
        if (component == "Script" && property.StartsWith(FieldsPrefix, StringComparison.Ordinal))
            return w.Get<ScriptComponent>(e).Fields is { } fields && fields.TryGetValue(property[FieldsPrefix.Length..], out var f) ? f : null;
        return Array.Find(def.Properties, p => p.Name == property)?.Get(w, e);
    }

    /// <summary>Writes a property by its path, adding the component if needed. Returns an error, or null on success.</summary>
    public static string? Set(World w, EntityId e, string path, JsonElement value)
    {
        var (component, property) = Split(path);
        if (component == null)
        {
            if (Array.Find(NodeProperties, p => p.Name == property) is not { } p)
                return $"no node property '{property}'";
            return Apply(p, w, e, value);
        }
        if (Component(component) is not { } def)
            return $"no component '{component}'";
        def.Add(w, e);
        if (component == "Script" && property.StartsWith(FieldsPrefix, StringComparison.Ordinal))
        {
            ref var script = ref w.Get<ScriptComponent>(e);
            script.Fields ??= [];
            script.Fields[property[FieldsPrefix.Length..]] = value;
            return null;
        }
        if (Array.Find(def.Properties, p => p.Name == property) is not { } prop)
            return $"no property '{property}' on {component}";
        return Apply(prop, w, e, value);
    }

    static string? Apply(PropertyDef p, World w, EntityId e, JsonElement value)
    {
        if (!Fits(p.Kind, value))
            return $"{p.Name} wants {p.Kind}, got {value.GetRawText()}";
        p.Set(w, e, value);
        return null;
    }

    public static (string? Component, string Property) Split(string path)
    {
        var dot = path.IndexOf('.');
        return dot < 0 ? (null, path) : (path[..dot], path[(dot + 1)..]);
    }

    /// <summary>Every property an entity has, by path, with its current value (node properties first).</summary>
    public static IEnumerable<(string Path, JsonElement Value)> Snapshot(World w, EntityId e)
    {
        foreach (var p in NodeProperties)
            if (p.Get(w, e) is { } v)
                yield return (p.Name, v);
        foreach (var c in Components)
        {
            if (!c.Has(w, e))
                continue;
            foreach (var p in c.Properties)
                if (p.Get(w, e) is { } v)
                    yield return ($"{c.Name}.{p.Name}", v);
            if (c.Name == "Script" && w.Get<ScriptComponent>(e).Fields is { } fields)
                foreach (var (k, v) in fields)
                    yield return ($"Script.{FieldsPrefix}{k}", v);
        }
    }
}
