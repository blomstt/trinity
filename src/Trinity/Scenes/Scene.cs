using System.Reflection.Metadata;
using Trinity.Graphics;

[assembly: MetadataUpdateHandler(typeof(Trinity.Scenes.HotReload))]

namespace Trinity.Scenes;

/// <summary>
/// A running scene: a <see cref="Scenes.World"/> of nodes built from a scene file, the scripts on them,
/// its environment and a camera. Load one with <see cref="Load"/>, call <see cref="Start"/> once, then
/// <see cref="FixedUpdate"/> and <see cref="Update"/> each frame (<see cref="SceneGame"/> does all of
/// this). A script that throws is reported in the log and paused until its code changes (hot reload),
/// so one bug doesn't stop the game. Nodes destroyed during an update go at the end of it.
/// </summary>
public sealed class Scene
{
    public readonly World World = new();

    /// <summary>Where scene files and assets are read from (the project root, or the game's folder when shipped).</summary>
    public readonly SceneLibrary Library;

    public string Root => Library.Root;
    public Engine Engine { get; }
    public OrbitCamera Camera = new() { Yaw = 0.8f, Pitch = 0.35f, Distance = 8f, Target = new(0, 1, 0) };

    /// <summary>Sun, sky, fog and exposure; applied to the renderer each frame, so scripts can change them (a day cycle, say).</summary>
    public SceneEnvironment Environment = new();

    readonly HashSet<Script> _failed = [];
    int _reloads;

    public Scene(Engine engine, SceneLibrary library)
    {
        Engine = engine;
        Library = library;
        World.Destroying += OnDestroying;
    }

    /// <summary>
    /// Loads a scene file. Scenes name assets relative to the project root (the folder with
    /// <c>trinity.json</c>), as the editor does. A relative path resolves from <paramref name="root"/>,
    /// by default the executable's folder, which is the asset root when the game is built. For an
    /// absolute path the root is the nearest folder above it with a <c>trinity.json</c>.
    /// </summary>
    public static Scene Load(Engine engine, string path, string? root = null)
    {
        string full;
        if (Path.IsPathRooted(path))
        {
            full = Path.GetFullPath(path);
            root ??= ProjectRoot(full);
        }
        else
        {
            root ??= AppContext.BaseDirectory;
            full = Path.GetFullPath(Path.Combine(root, path));
        }
        var library = new SceneLibrary(root);
        var relative = SceneLibrary.Normalize(Path.GetRelativePath(root, full));
        var scene = new Scene(engine, library);
        var (doc, problems) = library.Load(relative);
        Report(problems);
        if (doc == null)
            return scene;
        Report(library.Instantiate(scene.World, doc, name: relative));
        scene.Environment = doc.Environment ?? new SceneEnvironment();
        if (doc.Camera is { } c)
            scene.Camera = new OrbitCamera
            {
                Target = c.Target,
                Yaw = float.DegreesToRadians(c.Yaw),
                Pitch = float.DegreesToRadians(c.Pitch),
                Distance = c.Distance,
            };
        return scene;
    }

    /// <summary>The nearest folder above a file with a trinity.json, or else the folder above the file's folder (Scenes/x.scene.json).</summary>
    static string ProjectRoot(string file)
    {
        for (var dir = Path.GetDirectoryName(file); dir != null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "trinity.json")))
                return dir;
        return Path.GetDirectoryName(Path.GetDirectoryName(file))!;
    }

    static void Report(SceneProblems problems)
    {
        foreach (var e in problems.Errors)
            Log.Error("scene", e);
        foreach (var w in problems.Warnings)
            Log.Warn("scene", w);
    }

    /// <summary>Every node, parents before children.</summary>
    public IEnumerable<Node> Nodes => World.All().Select(e => new Node(World, e));

    /// <summary>A node by a path of names from the top ("House/Front door"), or <see cref="Node.None"/>.</summary>
    public Node Find(string path)
    {
        var parts = path.Split('/', 2);
        foreach (var root in World.Roots)
            if (World.Name(root) == parts[0])
                return parts.Length == 1 ? new Node(World, root) : new Node(World, root).Find(parts[1]);
        return Node.None;
    }

    /// <summary>A new, empty node; scripts added to it start at the next update.</summary>
    public Node Create(string name = "Node", Node parent = default) => new(World, World.Create(name, parent.Id));

    /// <summary>An instance of another scene (a prefab) under <paramref name="parent"/>; its scripts start at the next update.</summary>
    public Node Spawn(string scenePath, Node parent = default)
    {
        var node = Library.Instance(World, scenePath, parent.Id, out var problems);
        Report(problems);
        return new Node(World, node);
    }

    /// <summary>
    /// Creates the scripts of nodes that don't have them running yet, then calls their <see cref="Script.Ready"/>,
    /// children before parents. Called at the start, and at each update for nodes added since.
    /// </summary>
    public void Start()
    {
        if (World.Store<ScriptComponent>().Count == 0)
            return;
        var started = new List<Script>();
        // Children before parents: each root's subtree in post-order
        foreach (var root in World.Roots)
            StartSubtree(root, started);
        foreach (var s in started)
            Run(s, "Ready", static (s, _) => s.Ready(), default);
    }

    void StartSubtree(EntityId e, List<Script> started)
    {
        foreach (var c in World.Children(e))
            StartSubtree(c, started);
        if (!World.Has<ScriptComponent>(e))
            return;
        ref var sc = ref World.Get<ScriptComponent>(e);
        if (sc.Instance != null || string.IsNullOrEmpty(sc.Class))
            return;
        var script = Scripts.Create(sc.Class, sc.Fields, World);
        if (script == null)
        {
            Log.Error("scene", $"{World.Name(e)}: no script class '{sc.Class}' in the game (scripts: {string.Join(", ", Scripts.Names)})");
            sc.Class = ""; // don't try again every frame
            return;
        }
        script.Node = new Node(World, e);
        script.Scene = this;
        sc.Instance = script;
        started.Add(script);
    }

    public void FixedUpdate(in FrameTime time)
    {
        Prepare();
        World.BeginIteration();
        // By index over the store: scripts may add nodes as they run (those start next update)
        var scripts = World.Store<ScriptComponent>();
        var count = scripts.Count;
        for (var i = 0; i < count && i < scripts.Count; i++)
            if (scripts.Components[i].Instance is { } s)
                Run(s, "FixedUpdate", static (s, t) => s.FixedUpdate(t), time);
        World.EndIteration();
    }

    public void Update(in FrameTime time)
    {
        Prepare();
        World.BeginIteration();
        foreach (ref var animator in World.Store<Animator>().Components)
            if (!animator.Paused)
                animator.Time += time.Delta * animator.Speed;
        var scripts = World.Store<ScriptComponent>();
        var count = scripts.Count;
        for (var i = 0; i < count && i < scripts.Count; i++)
            if (scripts.Components[i].Instance is { } s)
                Run(s, "Update", static (s, t) => s.Update(t), time);
        World.EndIteration();
    }

    void Prepare()
    {
        ResumeAfterReload();
        // Start scripts on nodes added since the last update (a quick scan when there are none)
        var scripts = World.Store<ScriptComponent>();
        for (var i = 0; i < scripts.Count; i++)
            if (scripts.Components[i].Instance == null && !string.IsNullOrEmpty(scripts.Components[i].Class))
            {
                Start();
                break;
            }
    }

    void OnDestroying(EntityId e)
    {
        if (!World.Has<ScriptComponent>(e))
            return;
        ref var sc = ref World.Get<ScriptComponent>(e);
        if (sc.Instance is not { } s)
            return;
        Run(s, "Destroyed", static (s, _) => s.Destroyed(), default);
        _failed.Remove(s);
        sc.Instance = null;
    }

    /// <summary>Destroys every node, telling their scripts.</summary>
    public void Unload() => World.Clear();

    void Run(Script script, string method, Action<Script, FrameTime> call, FrameTime time)
    {
        if (_failed.Contains(script))
            return;
        try
        {
            call(script, time);
        }
        catch (Exception e)
        {
            _failed.Add(script);
            // Where in the script it happened: its own deepest frame (with file and line in Debug builds;
            // code applied by hot reload has no line numbers, so then just the method)
            var type = script.GetType().FullName + ".";
            var at = e.StackTrace?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("at " + type, StringComparison.Ordinal)) ?? "";
            var name = script.Node.IsValid ? script.Node.Name : "(destroyed node)";
            Log.Error("script", $"{name} ({script.GetType().Name}.{method}): {e.GetType().Name}: {e.Message} {at}");
            Log.Warn("script", $"{name}'s script is paused until its code changes");
        }
    }

    void ResumeAfterReload()
    {
        if (_reloads == HotReload.Count)
            return;
        _reloads = HotReload.Count;
        if (_failed.Count > 0)
            Log.Info("script", $"code changed: resuming {_failed.Count} paused script(s)");
        _failed.Clear();
    }
}

/// <summary>Told by .NET when hot reload applies a code change to the running game (dotnet watch).</summary>
static class HotReload
{
    public static volatile int Count;

    public static void ClearCache(Type[]? types) { }

    public static void UpdateApplication(Type[]? types)
    {
        Count++;
        Log.Info("script", "hot reload applied");
    }
}
