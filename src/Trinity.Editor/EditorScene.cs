using System.Numerics;
using System.Text.Json;
using Trinity.Scenes;

namespace Trinity.Editor;

/// <summary>
/// The scene being edited: a world built from a scene file, its environment and starting camera, the
/// selection, unsaved-change tracking, and undo/redo. Every edit the editor makes goes through here,
/// so each one can be undone and none touches a source scene the edited scene instances.
/// <para>
/// Property edits (transform, components, script fields, overrides) are recorded as old and new
/// values; edits from one drag merge into one step. Structural edits (create, delete, duplicate,
/// reparent, instance, components) are recorded as whole-scene snapshots: simple and complete, costing
/// O(scene size) per step. Nodes are found by their persistent path, so steps stay valid after a
/// snapshot restore rebuilds the world.
/// </para>
/// </summary>
sealed class EditorScene
{
    public readonly SceneLibrary Library;
    public readonly World World = new();
    public SceneEnvironment Environment = new();
    public SceneCamera? Camera;

    /// <summary>Project-relative path the scene saves to.</summary>
    public string Path;
    public bool Dirty;

    public readonly HashSet<EntityId> Selection = [];
    public EntityId Primary { get; private set; }

    abstract record Edit(string Label);
    sealed record PropertyEdit(string Label, string Key, string Property, JsonElement? Old, JsonElement New, Override? OldOverride, Override? NewOverride) : Edit(Label)
    {
        public JsonElement New { get; set; } = New;
        public Override? NewOverride { get; set; } = NewOverride;
    }
    sealed record SnapshotEdit(string Label, string Before, string After, string[] SelectionBefore, string[] SelectionAfter) : Edit(Label);
    sealed record EnvironmentEdit(string Label, string Before, string After) : Edit(Label)
    {
        public string After { get; set; } = After;
    }

    /// <summary>An inherited node's override of one property: the instance root's path, the node's path in the source, and the value (null: not overridden).</summary>
    sealed record Override(string RootKey, string SourcePath, string Property, JsonElement? Value);

    readonly List<Edit> _undo = [], _redo = [];
    Edit? _open; // the edit a drag is still adding to

    EditorScene(SceneLibrary library, string path)
    {
        Library = library;
        Path = path;
    }

    public static EditorScene New(SceneLibrary library, string path) => new(library, path);

    public static EditorScene Load(SceneLibrary library, string path, out SceneProblems problems)
    {
        var scene = new EditorScene(library, path);
        library.Invalidate(path);
        var (doc, p) = library.Load(path);
        problems = p;
        if (doc != null)
        {
            problems.Add(library.Instantiate(scene.World, doc, name: path));
            scene.Environment = doc.Environment ?? new SceneEnvironment();
            scene.Camera = doc.Camera;
        }
        return scene;
    }

    public SceneDocument ToDocument() => SceneLibrary.Write(World, Environment, Camera);

    public string Snapshot() => SceneLibrary.Serialize(ToDocument());

    public void Save(string? path = null)
    {
        Path = path ?? Path;
        Library.Save(Path, ToDocument());
        Dirty = false;
        // Saved: an autosave of earlier unsaved changes is out of date
        DiscardAutosave();
    }

    /// <summary>Where unsaved changes are kept: beside the scene, never over it.</summary>
    public string AutosavePath => AutosaveOf(Path);

    public static string AutosaveOf(string path) =>
        path.EndsWith(".scene.json", StringComparison.Ordinal) ? path[..^".scene.json".Length] + ".autosave.scene.json" : path + ".autosave";

    /// <summary>Writes the unsaved changes to <see cref="AutosavePath"/>.</summary>
    public void Autosave() => Library.Save(AutosavePath, ToDocument());

    public void DiscardAutosave()
    {
        var file = Library.Absolute(AutosavePath);
        if (File.Exists(file))
            File.Delete(file);
    }

    /// <summary>Whether an autosave holds changes newer than the scene file (left by a quit or crash before saving).</summary>
    public static bool HasNewerAutosave(SceneLibrary library, string path)
    {
        var autosave = library.Absolute(AutosaveOf(path));
        var scene = library.Absolute(path);
        return File.Exists(autosave) && (!File.Exists(scene) || File.GetLastWriteTimeUtc(autosave) > File.GetLastWriteTimeUtc(scene));
    }

    /// <summary>The scene with the unsaved changes from its autosave, ready to review and save (it's marked unsaved).</summary>
    public static EditorScene LoadAutosave(SceneLibrary library, string path, out SceneProblems problems)
    {
        var scene = Load(library, AutosaveOf(path), out problems);
        scene.Path = path;
        scene.Dirty = true;
        return scene;
    }

    // --- Selection ----------------------------------------------------------------------------------

    public void Select(EntityId e, bool add = false)
    {
        if (!add)
            Selection.Clear();
        if (e.IsNone)
        {
            Primary = EntityId.None;
            return;
        }
        if (add && !Selection.Add(e))
        {
            Selection.Remove(e);
            Primary = Selection.FirstOrDefault();
            return;
        }
        Selection.Add(e);
        Primary = e;
    }

    void PruneSelection()
    {
        Selection.RemoveWhere(e => !World.IsAlive(e));
        if (!World.IsAlive(Primary))
            Primary = Selection.FirstOrDefault();
    }

    string Key(EntityId e) => SceneLibrary.KeyOf(World, e);

    EntityId FindKey(string key)
    {
        foreach (var e in World.All())
            if (Key(e) == key)
                return e;
        return EntityId.None;
    }

    // --- Undo and redo ------------------------------------------------------------------------------

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoLabel => _undo.LastOrDefault()?.Label;
    public string? RedoLabel => _redo.LastOrDefault()?.Label;

    /// <summary>Undo history limits: steps, and memory held by snapshots (big scenes keep fewer structural steps).</summary>
    const int MaxSteps = 200;
    const long MaxSnapshotBytes = 64L << 20;

    void Record(Edit edit)
    {
        _undo.Add(edit);
        _redo.Clear();
        while (_undo.Count > MaxSteps || _undo.Count > 1 && SnapshotBytes() > MaxSnapshotBytes)
            _undo.RemoveAt(0);
        Dirty = true;
    }

    long SnapshotBytes()
    {
        // Consecutive snapshot steps share a string (one's After is the next one's Before), so count each once
        var seen = new HashSet<string>(ReferenceEqualityComparer.Instance);
        long bytes = 0;
        foreach (var e in _undo)
            if (e is SnapshotEdit s)
                foreach (var text in (ReadOnlySpan<string>)[s.Before, s.After])
                    if (seen.Add(text))
                        bytes += text.Length * 2L;
        return bytes;
    }

    string? _lastSnapshot;

    /// <summary>The steps that can be undone, oldest first (for the History panel).</summary>
    public IEnumerable<string> UndoSteps => _undo.Select(e => e.Label);

    /// <summary>The steps that can be redone, next first.</summary>
    public IEnumerable<string> RedoSteps => Enumerable.Reverse(_redo).Select(e => e.Label);

    /// <summary>Undoes or redoes until <paramref name="done"/> steps are applied (0: as opened).</summary>
    public void StepTo(int done)
    {
        while (_undo.Count > done && Undo()) { }
        while (_undo.Count < done && Redo()) { }
    }

    /// <summary>Forgets every undo and redo step (after a change undo can't follow, such as moving files).</summary>
    public void ClearHistory()
    {
        _undo.Clear();
        _redo.Clear();
        _open = null;
        _lastSnapshot = null;
    }

    /// <summary>Ends a drag: the next edit starts a new undo step even if it's to the same property.</summary>
    public void CloseEdit() => _open = null;

    public bool Undo() => Step(_undo, _redo, undo: true);
    public bool Redo() => Step(_redo, _undo, undo: false);

    bool Step(List<Edit> from, List<Edit> to, bool undo)
    {
        if (from.Count == 0)
            return false;
        _open = null;
        var edit = from[^1];
        from.RemoveAt(from.Count - 1);
        switch (edit)
        {
            case PropertyEdit p:
                var e = FindKey(p.Key);
                if (!e.IsNone)
                {
                    var value = undo ? p.Old : p.New;
                    if (value is { } v)
                        SceneSchema.Set(World, e, p.Property, v);
                    else if (p.Property.StartsWith("Script." + SceneSchema.FieldsPrefix, StringComparison.Ordinal) && World.Has<ScriptComponent>(e))
                        World.Get<ScriptComponent>(e).Fields?.Remove(p.Property[("Script." + SceneSchema.FieldsPrefix).Length..]); // back to the class's default
                    ApplyOverride(undo ? p.OldOverride : p.NewOverride);
                }
                break;
            case SnapshotEdit s:
                Restore(undo ? s.Before : s.After, undo ? s.SelectionBefore : s.SelectionAfter);
                break;
            case EnvironmentEdit env:
                Environment = JsonSerializer.Deserialize(undo ? env.Before : env.After, SceneJsonEditor.Default.SceneEnvironment) ?? new();
                break;
        }
        to.Add(edit);
        Dirty = true;
        return true;
    }

    void ApplyOverride(Override? state)
    {
        if (state == null)
            return;
        var root = FindKey(state.RootKey);
        if (root.IsNone || !World.Has<SceneInstance>(root))
            return;
        ref var si = ref World.Get<SceneInstance>(root);
        if (state.Value is { } v)
        {
            if (!si.Overrides.TryGetValue(state.SourcePath, out var props))
                si.Overrides[state.SourcePath] = props = [];
            props[state.Property] = v;
        }
        else if (si.Overrides.TryGetValue(state.SourcePath, out var props))
        {
            props.Remove(state.Property);
            if (props.Count == 0)
                si.Overrides.Remove(state.SourcePath);
        }
    }

    /// <summary>Rebuilds the world from a snapshot, keeping the selection by persistent path.</summary>
    void Restore(string json, string[] selection)
    {
        var (doc, problems) = SceneLibrary.Parse(json, Path);
        if (doc == null)
        {
            Log.Error("editor", $"couldn't restore the scene: {problems}");
            return;
        }
        World.Clear();
        Library.Instantiate(World, doc, name: Path);
        Environment = doc.Environment ?? new();
        Camera = doc.Camera;
        Selection.Clear();
        Primary = EntityId.None;
        foreach (var key in selection)
            if (FindKey(key) is { IsNone: false } e)
                Select(e, add: true);
    }

    /// <summary>Runs a structural change and records it as a before/after snapshot.</summary>
    internal T Structural<T>(string label, Func<T> change)
    {
        _open = null;
        var before = Snapshot();
        if (before == _lastSnapshot)
            before = _lastSnapshot; // share the previous step's copy
        var selectionBefore = Selection.Select(Key).ToArray();
        T result;
        try
        {
            result = change();
        }
        catch
        {
            // Put the scene back as it was, so a failed edit leaves nothing half done
            Restore(before, selectionBefore);
            throw;
        }
        PruneSelection();
        _lastSnapshot = Snapshot();
        Record(new SnapshotEdit(label, before, _lastSnapshot, selectionBefore, Selection.Select(Key).ToArray()));
        return result;
    }

    // --- Edits --------------------------------------------------------------------------------------

    /// <summary>Whether an entity came from an instanced scene (its structure belongs to the source; properties can be overridden).</summary>
    public bool IsInherited(EntityId e) => World.Has<Inherited>(e);

    /// <summary>
    /// Sets a property (a node property or "Component.Property"). On a node from an instanced scene this
    /// also records an override on the instance. With <paramref name="merge"/>, successive calls for the
    /// same property (a drag) make one undo step until <see cref="CloseEdit"/>. Returns an error or null.
    /// </summary>
    public string? SetProperty(EntityId e, string property, JsonElement value, bool merge = false)
    {
        var key = Key(e);
        var old = SceneSchema.Get(World, e, property);
        Override? oldOverride = null, newOverride = null;
        if (World.Has<Inherited>(e))
        {
            var inherited = World.Get<Inherited>(e);
            var rootKey = Key(inherited.InstanceRoot);
            var overrides = World.Get<SceneInstance>(inherited.InstanceRoot).Overrides;
            JsonElement? current = overrides.TryGetValue(inherited.SourcePath, out var props) && props.TryGetValue(property, out var cv) ? cv : null;
            oldOverride = new Override(rootKey, inherited.SourcePath, property, current);
            newOverride = new Override(rootKey, inherited.SourcePath, property, value);
        }
        if (SceneSchema.Set(World, e, property, value) is { } error)
            return error;
        ApplyOverride(newOverride);
        if (merge && _open is PropertyEdit open && open.Key == key && open.Property == property)
        {
            open.New = value;
            open.NewOverride = newOverride;
            Dirty = true;
            return null;
        }
        var edit = new PropertyEdit($"change {property}", key, property, old, value, oldOverride, newOverride);
        Record(edit);
        _open = merge ? edit : null;
        return null;
    }

    public bool IsOverridden(EntityId e, string property)
    {
        if (!World.Has<Inherited>(e))
            return false;
        var inherited = World.Get<Inherited>(e);
        return World.Get<SceneInstance>(inherited.InstanceRoot).Overrides.TryGetValue(inherited.SourcePath, out var props) && props.ContainsKey(property);
    }

    /// <summary>Drops an inherited node's override of a property, so it shows the source's value again.</summary>
    public void ResetOverride(EntityId e, string property) =>
        Structural($"reset {property}", () =>
        {
            var inherited = World.Get<Inherited>(e);
            var root = inherited.InstanceRoot;
            ref var si = ref World.Get<SceneInstance>(root);
            if (si.Overrides.TryGetValue(inherited.SourcePath, out var props))
            {
                props.Remove(property);
                if (props.Count == 0)
                    si.Overrides.Remove(inherited.SourcePath);
            }
            var selected = Selection.Select(Key).ToArray();
            Library.Rebuild(World, root);
            Selection.Clear();
            foreach (var k in selected)
                if (FindKey(k) is { IsNone: false } n)
                    Selection.Add(n);
            Primary = Selection.FirstOrDefault();
            return true;
        });

    public string EnvironmentJson() => JsonSerializer.Serialize(Environment, SceneJsonEditor.Default.SceneEnvironment);

    /// <summary>Records a change made to <see cref="Environment"/> since it was <paramref name="before"/> (from <see cref="EnvironmentJson"/>).</summary>
    public void EnvironmentChanged(string before, bool merge = false)
    {
        var after = EnvironmentJson();
        if (before == after)
            return;
        if (merge && _open is EnvironmentEdit open)
        {
            open.After = after;
            Dirty = true;
            return;
        }
        var edit = new EnvironmentEdit("change environment", before, after);
        Record(edit);
        _open = merge ? edit : null;
    }

    /// <summary>A new node under <paramref name="parent"/>, optionally with a component ("Camera", say), as one undo step.</summary>
    public EntityId CreateNode(string name, EntityId parent = default, string? component = null) =>
        Structural($"create {name}", () =>
        {
            var e = World.Create(UniqueName(name), Attachable(parent));
            if (component != null)
                SceneSchema.Component(component)?.Add(World, e);
            Select(e);
            return e;
        });

    /// <summary>Where new nodes can go: not inside an instance's contents, so under the instance itself.</summary>
    EntityId Attachable(EntityId parent) =>
        !parent.IsNone && World.IsAlive(parent) && IsInherited(parent) ? World.Get<Inherited>(parent).InstanceRoot : parent;

    /// <summary>A world position as a local one under <paramref name="parent"/>.</summary>
    Vector3 LocalPosition(EntityId parent, Vector3 world) =>
        !parent.IsNone && Matrix4x4.Invert(World.WorldMatrix(parent), out var inverse) ? Vector3.Transform(world, inverse) : world;

    /// <summary>A node drawing a model at a world position, playing its idle clip if it has one.</summary>
    public EntityId Place(string model, Vector3 position, string? clip, EntityId parent = default) =>
        Structural($"place {System.IO.Path.GetFileNameWithoutExtension(model)}", () =>
        {
            parent = Attachable(parent);
            var e = World.Create(UniqueName(System.IO.Path.GetFileNameWithoutExtension(model)), parent);
            World.SetPosition(e, LocalPosition(parent, position));
            World.Add(e, new MeshRenderer(model));
            if (!string.IsNullOrEmpty(clip))
                World.Add(e, new Animator(clip));
            Select(e);
            return e;
        });

    /// <summary>An instance of another scene (a prefab) at a world position, as a new node.</summary>
    public EntityId InstantiateScene(string scenePath, Vector3 position, out SceneProblems problems, EntityId parent = default)
    {
        SceneProblems found = new();
        var e = Structural($"instance {System.IO.Path.GetFileName(scenePath)}", () =>
        {
            parent = Attachable(parent);
            var name = UniqueName(System.IO.Path.GetFileName(scenePath).Replace(".scene.json", ""));
            var node = Library.Instance(World, scenePath, parent, out found, name);
            World.SetPosition(node, LocalPosition(parent, position));
            Select(node);
            return node;
        });
        problems = found;
        return e;
    }

    string UniqueName(string name)
    {
        var names = World.All().Select(World.Name).ToHashSet();
        if (!names.Contains(name))
            return name;
        for (var i = 2; ; i++)
            if (!names.Contains($"{name} {i}"))
                return $"{name} {i}";
    }

    /// <summary>Deletes nodes (and what's under them). Nodes from an instance can't be deleted on their own; returns why, or null.</summary>
    public string? Delete(IEnumerable<EntityId> nodes)
    {
        var list = nodes.Where(World.IsAlive).ToList();
        if (list.Any(IsInherited))
            return "nodes from an instanced scene can't be deleted here; delete the instance, or change its source scene";
        if (list.Count == 0)
            return null;
        Structural(list.Count == 1 ? $"delete {World.Name(list[0])}" : $"delete {list.Count} nodes", () =>
        {
            foreach (var e in list)
                World.Destroy(e);
            return true;
        });
        return null;
    }

    /// <summary>Copies nodes (with what's under them) just after the originals; returns the copies.</summary>
    public List<EntityId> Duplicate(IEnumerable<EntityId> nodes)
    {
        var list = nodes.Where(e => World.IsAlive(e) && !IsInherited(e)).ToList();
        // Copy only the topmost of nested selections
        list = list.Where(e => !list.Any(o => o != e && World.IsSelfOrAncestor(o, e))).ToList();
        if (list.Count == 0)
            return [];
        return Structural(list.Count == 1 ? $"duplicate {World.Name(list[0])}" : $"duplicate {list.Count} nodes", () =>
        {
            var copies = new List<EntityId>();
            foreach (var e in list)
            {
                var (doc, rootId) = SubtreeDocument(e);
                var parent = World.Parent(e);
                Library.Instantiate(World, doc, parent, Path);
                var copy = World.All().First(n => !IsInherited(n) && World.PersistentId(n) == rootId);
                World.SetParent(copy, parent, World.SiblingIndex(e) + 1);
                World.SetName(copy, UniqueName(World.Name(e)));
                copies.Add(copy);
            }
            Selection.Clear();
            foreach (var c in copies)
                Selection.Add(c);
            Primary = copies[^1];
            return copies;
        });
    }

    /// <summary>
    /// A node's subtree as its own document with fresh persistent ids, for copying. References between
    /// nodes inside it are pointed at the copies; references outside it keep pointing at the originals.
    /// </summary>
    (SceneDocument Doc, long RootId) SubtreeDocument(EntityId e)
    {
        var ids = new HashSet<long>();
        foreach (var n in World.Subtree(e))
            if (!IsInherited(n))
                ids.Add(World.PersistentId(n));
        var remap = ids.ToDictionary(id => id, _ => World.NextPersistentId++);
        var doc = new SceneDocument { NextId = World.NextPersistentId };
        foreach (var r in ToDocument().Nodes.Where(r => ids.Contains(r.Id)))
        {
            var copy = JsonSerializer.Deserialize(JsonSerializer.Serialize(r, SceneJsonEditor.Default.NodeRecord), SceneJsonEditor.Default.NodeRecord)!;
            copy.Id = remap[r.Id];
            copy.Parent = r.Id == World.PersistentId(e) ? 0 : remap[r.Parent];
            if (copy.Components != null)
                foreach (var props in copy.Components.Values)
                    foreach (var key in props.Keys.ToList())
                        props[key] = Remap(props[key], remap);
            if (copy.Overrides != null)
                foreach (var props in copy.Overrides.Values)
                    foreach (var key in props.Keys.ToList())
                        props[key] = Remap(props[key], remap);
            doc.Nodes.Add(copy);
        }
        return (doc, remap[World.PersistentId(e)]);
    }

    static JsonElement Remap(JsonElement v, Dictionary<long, long> remap)
    {
        if (v.ValueKind != JsonValueKind.Object || !v.TryGetProperty("$entity", out var p) || p.GetString() is not { } path)
            return v;
        var slash = path.IndexOf('/');
        var head = slash < 0 ? path : path[..slash];
        if (!long.TryParse(head, out var id) || !remap.TryGetValue(id, out var to))
            return v;
        var moved = to + (slash < 0 ? "" : path[slash..]);
        return SceneSchema.Json($"{{\"$entity\": {SceneSchema.Text(moved).GetRawText()}}}");
    }

    /// <summary>
    /// Moves a node under another (none: to the top) at a sibling index, keeping where it is in the world.
    /// Refused for nodes from an instance, under nodes from an instance, and into its own subtree.
    /// </summary>
    public string? Reparent(EntityId node, EntityId parent, int index = -1)
    {
        if (IsInherited(node))
            return "nodes from an instanced scene stay where their scene puts them";
        if (!parent.IsNone && IsInherited(parent))
            return "nodes can't go inside an instance's contents (put them under the instance itself)";
        if (!World.CanParent(node, parent))
            return "a node can't go inside itself";
        Structural($"move {World.Name(node)}", () =>
        {
            World.SetParent(node, parent, index, keepWorld: true);
            return true;
        });
        return null;
    }

    public string? AddComponent(EntityId e, string component)
    {
        if (IsInherited(e))
            return "components of nodes from an instanced scene come from the source scene";
        if (SceneSchema.Component(component) is not { } def || def.Has(World, e))
            return null;
        Structural($"add {component}", () =>
        {
            def.Add(World, e);
            return true;
        });
        return null;
    }

    public string? RemoveComponent(EntityId e, string component)
    {
        if (IsInherited(e))
            return "components of nodes from an instanced scene come from the source scene";
        if (SceneSchema.Component(component) is not { } def || !def.Has(World, e))
            return null;
        Structural($"remove {component}", () =>
        {
            def.Remove(World, e);
            return true;
        });
        return null;
    }

    /// <summary>Attaches a script class (null: removes the script). Field values saved for the old class are dropped.</summary>
    public string? SetScript(EntityId e, string? className)
    {
        if (IsInherited(e))
        {
            if (className == null)
                return "a script from an instanced scene can't be removed here";
            return SetProperty(e, "Script.Class", SceneSchema.Text(className));
        }
        Structural(className == null ? "remove script" : $"attach {className}", () =>
        {
            if (className == null)
                World.Remove<ScriptComponent>(e);
            else if (World.Has<ScriptComponent>(e))
                World.Get<ScriptComponent>(e) = new ScriptComponent { Class = className };
            else
                World.Add(e, new ScriptComponent { Class = className });
            return true;
        });
        return null;
    }

    /// <summary>Drops a script field's saved value, so the script's own default applies (on an inherited node: the source's value).</summary>
    public void ClearScriptField(EntityId e, string field)
    {
        var path = "Script." + SceneSchema.FieldsPrefix + field;
        if (IsInherited(e))
        {
            if (IsOverridden(e, path))
                ResetOverride(e, path);
            return;
        }
        Structural($"reset {field}", () =>
        {
            World.Get<ScriptComponent>(e).Fields?.Remove(field);
            return true;
        });
    }

    /// <summary>Drops every override on an instance (including those on nodes inside it), back to the source scene.</summary>
    public void RevertInstance(EntityId instance) =>
        Structural($"revert {World.Name(instance)}", () =>
        {
            World.Get<SceneInstance>(instance).Overrides.Clear();
            Library.Rebuild(World, instance);
            return true;
        });

    /// <summary>How many properties an instance overrides.</summary>
    public int OverrideCount(EntityId instance) =>
        World.Has<SceneInstance>(instance) ? World.Get<SceneInstance>(instance).Overrides.Sum(kv => kv.Value.Count) : 0;

    /// <summary>After a scene file changed (saved here or elsewhere), rebuilds the instances of it in this scene.</summary>
    public int ReloadInstancesOf(string scenePath)
    {
        scenePath = SceneLibrary.Normalize(scenePath);
        Library.Invalidate(scenePath);
        var instances = World.All().Where(e => World.Has<SceneInstance>(e) && World.Get<SceneInstance>(e).Source == scenePath && !IsInherited(e)).ToList();
        foreach (var e in instances)
            Library.Rebuild(World, e);
        PruneSelection();
        return instances.Count;
    }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(Converters = [typeof(Vector3Converter)])]
[System.Text.Json.Serialization.JsonSerializable(typeof(SceneEnvironment))]
[System.Text.Json.Serialization.JsonSerializable(typeof(NodeRecord))]
sealed partial class SceneJsonEditor : System.Text.Json.Serialization.JsonSerializerContext;
