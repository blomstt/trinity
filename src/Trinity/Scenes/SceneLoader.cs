using System.Numerics;
using System.Text.Json;

namespace Trinity.Scenes;

/// <summary>What went wrong loading or instantiating a scene. Errors stop a file loading; warnings are skipped parts.</summary>
public sealed class SceneProblems
{
    public readonly List<string> Errors = [];
    public readonly List<string> Warnings = [];
    public bool Ok => Errors.Count == 0;

    public void Add(SceneProblems other, string? prefix = null)
    {
        Errors.AddRange(other.Errors.Select(e => prefix + e));
        Warnings.AddRange(other.Warnings.Select(w => prefix + w));
    }

    public override string ToString() => string.Join("\n", Errors.Select(e => "error: " + e).Concat(Warnings.Select(w => "warning: " + w)));
}

/// <summary>
/// Reads scene files into worlds and writes worlds back. Scene files are assets named by their
/// project-relative path (their stable id). Loading validates a file completely before building
/// anything, migrates older formats, and caches parsed files, so a scene instanced a hundred times is
/// parsed once. Instancing builds a scene's nodes under an instance node and then applies that
/// instance's overrides; only the overrides are saved with the instance, so changes to the source
/// reach every instance that didn't override them.
/// </summary>
public sealed class SceneLibrary(string root)
{
    readonly Dictionary<string, (SceneDocument? Doc, SceneProblems Problems)> _cache = [];

    /// <summary>The folder scene and asset paths are relative to.</summary>
    public string Root { get; } = root;

    public string Absolute(string path) => Path.GetFullPath(Path.Combine(Root, path));

    public static string Normalize(string path) => path.Replace('\\', '/');

    /// <summary>A scene file, parsed, migrated and validated (cached until <see cref="Invalidate"/>). Null if it couldn't be read.</summary>
    public (SceneDocument? Doc, SceneProblems Problems) Load(string path)
    {
        path = Normalize(path);
        if (_cache.TryGetValue(path, out var cached))
            return cached;
        var full = Absolute(path);
        var result = File.Exists(full) ? Parse(File.ReadAllText(full), path) : (null, Problem($"{path}: no such scene"));
        _cache[path] = result;
        return result;
    }

    /// <summary>Forgets a cached file (it changed on disk, or was saved).</summary>
    public void Invalidate(string path) => _cache.Remove(Normalize(path));

    /// <summary>Uses an in-memory document for a path, as if it had been saved there (the editor, and tests).</summary>
    public void Put(string path, SceneDocument doc) => _cache[Normalize(path)] = (doc, Validate(doc, path));

    static SceneProblems Problem(string error)
    {
        var p = new SceneProblems();
        p.Errors.Add(error);
        return p;
    }

    // --- Reading, migrating, validating -------------------------------------------------------------

    /// <summary>Parses scene JSON of any supported format into a validated current-format document.</summary>
    public static (SceneDocument? Doc, SceneProblems Problems) Parse(string json, string name)
    {
        SceneDocument? doc;
        try
        {
            using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var rootElement = parsed.RootElement;
            if (rootElement.ValueKind != JsonValueKind.Object)
                return (null, Problem($"{name}: not a scene (expected a JSON object)"));
            if (!rootElement.TryGetProperty("Format", out _) && rootElement.TryGetProperty("Objects", out _))
                doc = MigrateFormat1(rootElement);
            else
                doc = rootElement.Deserialize(SceneJson.Default.SceneDocument);
        }
        catch (JsonException e)
        {
            return (null, Problem($"{name}: malformed JSON at line {(e.LineNumber ?? 0) + 1}: {e.Message}"));
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException)
        {
            return (null, Problem($"{name}: {e.Message}"));
        }
        if (doc == null)
            return (null, Problem($"{name}: empty scene file"));
        var problems = Validate(doc, name);
        return (problems.Ok ? doc : null, problems);
    }

    /// <summary>Format 1 (a flat list of placed models, before E3) to format 2: each object becomes a root node with components.</summary>
    static SceneDocument MigrateFormat1(JsonElement root)
    {
        var doc = new SceneDocument();
        long id = 1;
        foreach (var o in root.GetProperty("Objects").EnumerateArray())
        {
            string? Str(string p) => o.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var rec = new NodeRecord
            {
                Id = id++,
                Name = Str("Name") ?? "Node",
                Position = o.TryGetProperty("Position", out var p) ? SceneSchema.ReadVector(p) : Vector3.Zero,
                Rotation = o.TryGetProperty("Rotation", out var r) ? SceneSchema.ReadVector(r) : Vector3.Zero,
                Scale = new Vector3(o.TryGetProperty("Scale", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetSingle() : 1),
                Components = [],
            };
            if (Str("Model") is { Length: > 0 } model)
                rec.Components["MeshRenderer"] = new() { ["Mesh"] = SceneSchema.Text(model) };
            if (Str("Clip") is { Length: > 0 } clip)
            {
                var playing = !o.TryGetProperty("Playing", out var pl) || pl.ValueKind != JsonValueKind.False;
                var speed = o.TryGetProperty("Speed", out var sp) && sp.ValueKind == JsonValueKind.Number ? sp.GetSingle() : 1;
                rec.Components["Animator"] = new() { ["Clip"] = SceneSchema.Text(clip), ["Speed"] = SceneSchema.Float(speed), ["Paused"] = SceneSchema.Bool(!playing) };
            }
            if (Str("Script") is { Length: > 0 } script)
            {
                var props = new Dictionary<string, JsonElement> { ["Class"] = SceneSchema.Text(script) };
                if (o.TryGetProperty("Fields", out var fields) && fields.ValueKind == JsonValueKind.Object)
                    foreach (var f in fields.EnumerateObject())
                        props[SceneSchema.FieldsPrefix + f.Name] = f.Value.Clone();
                rec.Components["Script"] = props;
            }
            doc.Nodes.Add(rec);
        }
        doc.NextId = id;
        if (root.TryGetProperty("Environment", out var env))
            doc.Environment = env.Deserialize(SceneJson.Default.SceneEnvironment);
        if (root.TryGetProperty("Camera", out var cam))
            doc.Camera = cam.Deserialize(SceneJson.Default.SceneCamera);
        return doc;
    }

    /// <summary>Checks a document's structure before anything is built from it.</summary>
    public static SceneProblems Validate(SceneDocument doc, string name)
    {
        var p = new SceneProblems();
        if (doc.Format > SceneDocument.CurrentFormat)
            p.Errors.Add($"{name}: format {doc.Format} is newer than this Trinity reads ({SceneDocument.CurrentFormat})");
        if (doc.Format < 2)
            p.Errors.Add($"{name}: format {doc.Format} isn't a known scene format");
        var seen = new HashSet<long>();
        foreach (var r in doc.Nodes)
        {
            var who = $"{name}: node '{r.Name}' (id {r.Id})";
            if (r.Id <= 0)
                p.Errors.Add($"{who}: ids must be positive");
            else if (!seen.Add(r.Id))
                p.Errors.Add($"{who}: id used twice");
            if (r.Parent != 0 && !seen.Contains(r.Parent))
                p.Errors.Add(doc.Nodes.Any(o => o.Id == r.Parent)
                    ? $"{who}: comes before its parent {r.Parent} (nodes must be in hierarchy order)"
                    : $"{who}: parent {r.Parent} doesn't exist");
            if (r.Instance is { Length: 0 })
                p.Errors.Add($"{who}: empty instance path");
            foreach (var (component, props) in r.Components ?? [])
            {
                if (SceneSchema.Component(component) is not { } def)
                {
                    p.Warnings.Add($"{who}: unknown component '{component}' skipped");
                    continue;
                }
                foreach (var (prop, value) in props)
                {
                    if (component == "Script" && prop.StartsWith(SceneSchema.FieldsPrefix, StringComparison.Ordinal))
                        continue;
                    if (Array.Find(def.Properties, d => d.Name == prop) is not { } pd)
                        p.Warnings.Add($"{who}: {component} has no property '{prop}'");
                    else if (!SceneSchema.Fits(pd.Kind, value))
                        p.Warnings.Add($"{who}: {component}.{prop} wants {pd.Kind}, got {value.GetRawText()}");
                }
            }
            if (r.Overrides != null && r.Instance == null)
                p.Warnings.Add($"{who}: has overrides but isn't an instance");
        }
        if (doc.NextId <= (doc.Nodes.Count == 0 ? 0 : doc.Nodes.Max(n => n.Id)))
            doc.NextId = doc.Nodes.Count == 0 ? 1 : doc.Nodes.Max(n => n.Id) + 1; // repair, harmlessly
        return p;
    }

    // --- Building worlds ----------------------------------------------------------------------------

    sealed class Build(World world)
    {
        public readonly World World = world;
        public readonly SceneProblems Problems = new();
        /// <summary>Every node built, by path relative to the outermost document: "5", "12/7", "12/3/7".</summary>
        public readonly Dictionary<string, EntityId> Paths = [];
        public readonly List<(EntityId Entity, string Property, JsonElement Value, string Prefix)> References = [];
        public readonly Stack<string> Scenes = new();
    }

    /// <summary>Builds a document's nodes into <paramref name="world"/> as its own (not inherited) nodes, under <paramref name="parent"/>.</summary>
    public SceneProblems Instantiate(World world, SceneDocument doc, EntityId parent = default, string name = "scene")
    {
        var b = new Build(world);
        world.NextPersistentId = Math.Max(world.NextPersistentId, doc.NextId);
        b.Scenes.Push(name);
        // Nodes already in the world, by path, so references can point at them (a pasted copy referring to the original)
        if (world.Count > 0)
            foreach (var e in world.All())
                b.Paths[KeyOf(world, e)] = e;
        AddDocument(b, doc, name, parent, "", EntityId.None, "", insertAt: -1);
        ResolveReferences(b);
        return b.Problems;
    }

    /// <summary>Adds an instance of the scene at <paramref name="source"/> under <paramref name="parent"/>: a new node whose children are the scene's nodes.</summary>
    public EntityId Instance(World world, string source, EntityId parent, out SceneProblems problems, string? name = null)
    {
        source = Normalize(source);
        var node = world.Create(name ?? Path.GetFileName(source).Replace(".scene.json", ""), parent);
        world.Add(node, new SceneInstance { Source = source, Overrides = [] });
        problems = Rebuild(world, node);
        return node;
    }

    /// <summary>
    /// Rebuilds an instance's contents from its source scene, then reapplies its overrides: after the
    /// source changed, or an override was reset. Nodes added under the instance locally are kept.
    /// </summary>
    public SceneProblems Rebuild(World world, EntityId instance)
    {
        var b = new Build(world);
        var toRemove = new List<EntityId>();
        foreach (var c in world.Children(instance))
            if (world.Has<Inherited>(c))
                toRemove.Add(c);
        foreach (var c in toRemove)
            world.Destroy(c);
        // Everything still in the world, by path, so overrides can refer to nodes outside the instance
        foreach (var e in world.All())
            b.Paths[KeyOf(world, e)] = e;
        var key = KeyOf(world, instance);
        var owner = world.Has<Inherited>(instance) ? world.Get<Inherited>(instance).InstanceRoot : instance;
        var ownerKey = owner == instance ? key + "/" : KeyOf(world, owner) + "/";
        var declaringPrefix = key.Contains('/') ? key[..(key.LastIndexOf('/') + 1)] : "";
        ref var si = ref world.Get<SceneInstance>(instance);
        AddInstanceContent(b, instance, si.Source, si.Overrides, key, owner, ownerKey, declaringPrefix);
        ResolveReferences(b);
        return b.Problems;
    }

    void AddDocument(Build b, SceneDocument doc, string docName, EntityId attach, string keyPrefix, EntityId owner, string ownerKey, int insertAt)
    {
        var w = b.World;
        var byId = new Dictionary<long, EntityId>();
        var rootIndex = insertAt;
        foreach (var r in doc.Nodes)
        {
            var parent = r.Parent == 0 ? attach : byId[r.Parent];
            var inherited = !owner.IsNone;
            var e = w.Create(r.Name, parent, inherited ? 0 : r.Id);
            if (r.Parent == 0 && rootIndex >= 0)
                w.SetParent(e, attach, rootIndex++);
            byId[r.Id] = e;
            var key = keyPrefix + r.Id;
            b.Paths[key] = e;
            w.SetPosition(e, r.Position);
            w.SetRotation(e, r.Rotation);
            w.SetScale(e, r.Scale);
            w.SetHidden(e, r.Hidden);
            w.SetLocked(e, r.Locked);
            if (inherited)
                w.Add(e, new Inherited { InstanceRoot = owner, SourcePath = key[ownerKey.Length..] });
            foreach (var (component, props) in r.Components ?? [])
            {
                if (SceneSchema.Component(component) is not { } def)
                    continue;
                def.Add(w, e);
                foreach (var (prop, value) in props)
                    SetOrDefer(b, e, $"{component}.{prop}", value, keyPrefix, $"{docName}: '{r.Name}'");
            }
            if (r.Instance != null)
            {
                w.Add(e, new SceneInstance { Source = Normalize(r.Instance), Overrides = Copy(r.Overrides) });
                AddInstanceContent(b, e, Normalize(r.Instance), r.Overrides, key, inherited ? owner : e, inherited ? ownerKey : key + "/", keyPrefix);
            }
        }
    }

    void AddInstanceContent(Build b, EntityId node, string source, Dictionary<string, Dictionary<string, JsonElement>>? overrides,
        string key, EntityId owner, string ownerKey, string declaringPrefix)
    {
        var who = $"'{b.World.Name(node)}' (instance of {source})";
        if (b.Scenes.Contains(source))
        {
            b.Problems.Errors.Add($"{who}: {source} contains itself ({string.Join(" > ", b.Scenes.Reverse())} > {source}); not expanded");
            return;
        }
        var (doc, problems) = Load(source);
        b.Problems.Add(problems);
        if (doc == null)
        {
            b.Problems.Warnings.Add($"{who}: source scene missing or broken; the instance is empty");
            return;
        }
        b.Scenes.Push(source);
        AddDocument(b, doc, source, node, key + "/", owner, ownerKey, insertAt: 0);
        b.Scenes.Pop();
        foreach (var (sourcePath, props) in overrides ?? [])
        {
            if (!b.Paths.TryGetValue(key + "/" + sourcePath, out var target))
            {
                b.Problems.Warnings.Add($"{who}: overrides node {sourcePath}, which {source} no longer has; kept, but unused");
                continue;
            }
            foreach (var (prop, value) in props)
                SetOrDefer(b, target, prop, value, declaringPrefix, who);
        }
    }

    static void SetOrDefer(Build b, EntityId e, string property, JsonElement value, string prefix, string who)
    {
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("$entity", out _))
        {
            b.References.Add((e, property, value, prefix));
            return;
        }
        if (SceneSchema.Set(b.World, e, property, value) is { } error)
            b.Problems.Warnings.Add($"{who}: {property}: {error}");
    }

    static void ResolveReferences(Build b)
    {
        foreach (var (e, property, value, prefix) in b.References)
        {
            var path = value.GetProperty("$entity").GetString() ?? "";
            var target = b.Paths.GetValueOrDefault(prefix + path);
            if (target.IsNone)
                b.Problems.Warnings.Add($"'{b.World.Name(e)}': {property} refers to node {path}, which doesn't exist; cleared");
            SceneSchema.Set(b.World, e, property, SceneSchema.EntityRef(target));
        }
    }

    static Dictionary<string, Dictionary<string, JsonElement>> Copy(Dictionary<string, Dictionary<string, JsonElement>>? o) =>
        o == null ? [] : o.ToDictionary(kv => kv.Key, kv => new Dictionary<string, JsonElement>(kv.Value));

    /// <summary>A node's path in its world's scene: its persistent id, or for a node from an instance, the instance's path and its path in the source.</summary>
    public static string KeyOf(World w, EntityId e) =>
        w.Has<Inherited>(e) ? KeyOf(w, w.Get<Inherited>(e).InstanceRoot) + "/" + w.Get<Inherited>(e).SourcePath : w.PersistentId(e).ToString();

    // --- Writing ------------------------------------------------------------------------------------

    /// <summary>
    /// The world's own nodes as a document: nodes from instances are left out (their instance node
    /// records the source and overrides instead), entity references become persistent paths, and
    /// runtime state (animation time, running scripts) isn't saved.
    /// </summary>
    public static SceneDocument Write(World w, SceneEnvironment? environment = null, SceneCamera? camera = null)
    {
        var doc = new SceneDocument { NextId = w.NextPersistentId, Environment = environment, Camera = camera };
        foreach (var root in w.Roots)
            WriteNode(w, root, doc);
        return doc;
    }

    static void WriteNode(World w, EntityId e, SceneDocument doc)
    {
        if (w.Has<Inherited>(e))
            return;
        var parent = w.Parent(e);
        var r = new NodeRecord
        {
            Id = w.PersistentId(e),
            Parent = parent.IsNone ? 0 : w.PersistentId(parent),
            Name = w.Name(e),
            Position = w.Position(e),
            Rotation = w.Rotation(e),
            Scale = w.Scale(e),
            Hidden = w.Hidden(e),
            Locked = w.Locked(e),
        };
        foreach (var def in SceneSchema.Components)
        {
            if (!def.Has(w, e))
                continue;
            var props = new Dictionary<string, JsonElement>();
            foreach (var p in def.Properties)
                if (p.Get(w, e) is { } v)
                    props[p.Name] = ToFile(w, v);
            if (def.Name == "Script" && w.Get<ScriptComponent>(e).Fields is { } fields)
                foreach (var (k, v) in fields)
                    props[SceneSchema.FieldsPrefix + k] = ToFile(w, v);
            (r.Components ??= [])[def.Name] = props;
        }
        if (w.Has<SceneInstance>(e))
        {
            var si = w.Get<SceneInstance>(e);
            r.Instance = si.Source;
            if (si.Overrides is { Count: > 0 })
                r.Overrides = si.Overrides.ToDictionary(kv => kv.Key, kv => kv.Value.ToDictionary(p => p.Key, p => ToFile(w, p.Value)));
        }
        doc.Nodes.Add(r);
        foreach (var c in w.Children(e))
            WriteNode(w, c, doc);
    }

    /// <summary>Live entity references ({"$id": [index, generation]}) to file ones ({"$entity": "path"}).</summary>
    static JsonElement ToFile(World w, JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.Object || !v.TryGetProperty("$id", out _))
            return v;
        var e = SceneSchema.ReadEntity(v);
        return w.IsAlive(e) ? SceneSchema.Json($"{{\"$entity\": {JsonSerializer.Serialize(KeyOf(w, e), SceneJson.Default.String)}}}") : SceneSchema.Json("null");
    }

    public static string Serialize(SceneDocument doc) => JsonSerializer.Serialize(doc, SceneJson.Default.SceneDocument);

    /// <summary>Writes the world's scene to a file atomically, and drops the file's cached copy.</summary>
    public void Save(string path, SceneDocument doc)
    {
        var full = Absolute(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + ".tmp";
        File.WriteAllText(temp, Serialize(doc));
        File.Move(temp, full, overwrite: true);
        Invalidate(path);
    }

    /// <summary>Assets and scenes the document refers to that aren't on disk.</summary>
    public List<string> MissingDependencies(SceneDocument doc)
    {
        var (assets, scenes) = Dependencies(doc);
        return assets.Concat(scenes).Where(p => !File.Exists(Absolute(p))).Order().ToList();
    }

    /// <summary>The assets and scenes a document refers to, for dependency tracking.</summary>
    public static (HashSet<string> Assets, HashSet<string> Scenes) Dependencies(SceneDocument doc)
    {
        var assets = new HashSet<string>();
        var scenes = new HashSet<string>();
        foreach (var r in doc.Nodes)
        {
            if (r.Instance != null)
                scenes.Add(Normalize(r.Instance));
            foreach (var (component, props) in r.Components ?? [])
                if (SceneSchema.Component(component) is { } def)
                    foreach (var p in def.Properties.Where(p => p.Kind == PropertyKind.Asset))
                        if (props.TryGetValue(p.Name, out var v) && v.GetString() is { Length: > 0 } a)
                            assets.Add(Normalize(a));
        }
        return (assets, scenes);
    }
}
