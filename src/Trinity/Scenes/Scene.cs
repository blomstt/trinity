using System.Reflection.Metadata;
using Trinity.Graphics;

[assembly: MetadataUpdateHandler(typeof(Trinity.Scenes.HotReload))]

namespace Trinity.Scenes;

/// <summary>
/// A running scene: its nodes, their scripts and a camera. Load one with <see cref="Load"/>, call
/// <see cref="Start"/> once, then <see cref="FixedUpdate"/> and <see cref="Update"/> from the game
/// (<see cref="SceneGame"/> does all of this). A script that throws is reported in the log and paused
/// until the code changes (hot reload) or the scene restarts, so one bug doesn't stop the game.
/// </summary>
public sealed class Scene
{
    public readonly List<Node> Nodes;

    /// <summary>The folder model paths are relative to: the project root.</summary>
    public readonly string Root;

    public Engine Engine { get; }
    public OrbitCamera Camera = new() { Yaw = 0.8f, Pitch = 0.35f, Distance = 8f, Target = new(0, 1, 0) };

    readonly HashSet<Script> _failed = [];
    int _reloads;

    public Scene(Engine engine, List<Node> nodes, string root)
    {
        Engine = engine;
        Nodes = nodes;
        Root = root;
    }

    /// <summary>Loads a <c>*.scene.json</c>. Relative paths resolve from the executable's folder.</summary>
    public static Scene Load(Engine engine, string path)
    {
        var full = Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
        var file = SceneFile.Load(full);
        // Scenes live in <root>/Scenes/; models are named relative to <root>
        var root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(full)))!;
        return new Scene(engine, file.Objects, root);
    }

    public Node? Find(string name) => Nodes.FirstOrDefault(n => n.Name == name);

    /// <summary>Creates every node's script, then calls each one's <see cref="Script.Ready"/>.</summary>
    public void Start()
    {
        foreach (var node in Nodes)
        {
            if (string.IsNullOrEmpty(node.Script))
                continue;
            var script = Scripts.Create(node.Script, node.Fields);
            if (script == null)
            {
                Log.Error("scene", $"{node.Name}: no script class '{node.Script}' in the game (scripts: {string.Join(", ", Scripts.Names)})");
                continue;
            }
            script.Node = node;
            script.Scene = this;
            node.Behaviour = script;
        }
        foreach (var node in Nodes)
            if (node.Behaviour is { } s)
                Run(s, "Ready", static (s, _) => s.Ready(), default);
    }

    public void FixedUpdate(in FrameTime time)
    {
        ResumeAfterReload();
        foreach (var node in Nodes)
            if (node.Behaviour is { } s)
                Run(s, "FixedUpdate", static (s, t) => s.FixedUpdate(t), time);
    }

    public void Update(in FrameTime time)
    {
        ResumeAfterReload();
        foreach (var node in Nodes)
        {
            if (node.Playing)
                node.Time += time.Delta * node.Speed;
            if (node.Behaviour is { } s)
                Run(s, "Update", static (s, t) => s.Update(t), time);
        }
    }

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
            Log.Error("script", $"{script.Node.Name} ({script.GetType().Name}.{method}): {e.GetType().Name}: {e.Message} {at}");
            Log.Warn("script", $"{script.Node.Name}'s script is paused until its code changes");
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
