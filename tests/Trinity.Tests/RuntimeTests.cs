using System.Numerics;
using Trinity.Scenes;
using Xunit;

namespace Trinity.Tests;

// Scripts the runtime tests attach; registered by hand here (games get this from the generator)
sealed class Recorder : Script
{
    public static readonly List<string> Events = [];
    public Node Target;
    public bool DestroySelfOnUpdate;
    public string SpawnChildNamed = "";

    public override void Ready() => Events.Add($"ready {Node.Name}");
    public override void Destroyed() => Events.Add($"destroyed {Node.Name}");

    public override void Update(in FrameTime time)
    {
        Events.Add($"update {Node.Name}");
        if (DestroySelfOnUpdate)
        {
            Node.Destroy();
            Events.Add($"still valid during update: {Node.IsValid}");
        }
        if (SpawnChildNamed.Length > 0)
        {
            var child = Node.AddChild(SpawnChildNamed);
            child.Add(new ScriptComponent("Recorder"));
            SpawnChildNamed = "";
        }
    }
}

[Collection("scripts")]
public class RuntimeTests
{
    static RuntimeTests() => Scripts.Register("Recorder", static () => new Recorder(),
        ("Target", static (s, v) => ((Recorder)s).Target = Scripts.ReadNode(v)),
        ("DestroySelfOnUpdate", static (s, v) => ((Recorder)s).DestroySelfOnUpdate = v.GetBoolean()),
        ("SpawnChildNamed", static (s, v) => ((Recorder)s).SpawnChildNamed = v.GetString() ?? ""));

    static readonly FrameTime Frame = new(1 / 60f, 0, 0, 1 / 60f);

    static Scene NewScene() => new(null!, new SceneLibrary(Path.GetTempPath()));

    static Node Scripted(Scene scene, string name, Node parent = default)
    {
        var node = scene.Create(name, parent);
        node.Add(new ScriptComponent("Recorder"));
        return node;
    }

    [Fact]
    public void Ready_runs_children_before_parents_then_updates_run_in_creation_order()
    {
        Recorder.Events.Clear();
        var scene = NewScene();
        var parent = Scripted(scene, "parent");
        Scripted(scene, "child", parent);
        scene.Start();
        scene.Update(Frame);
        Assert.Equal(["ready child", "ready parent", "update parent", "update child"], Recorder.Events);
    }

    [Fact]
    public void A_script_destroying_its_node_mid_update_finishes_the_update_then_gets_no_more_calls()
    {
        Recorder.Events.Clear();
        var scene = NewScene();
        var doomed = Scripted(scene, "doomed");
        doomed.Get<ScriptComponent>().Fields = new() { ["DestroySelfOnUpdate"] = SceneSchema.Json("true") };
        scene.Start();
        var script = (Recorder)doomed.Get<ScriptComponent>().Instance!;
        Assert.True(script.DestroySelfOnUpdate);
        Scripted(scene, "survivor");
        scene.Update(Frame);
        Assert.Contains("still valid during update: True", Recorder.Events); // destruction waits for the update to end
        Assert.Contains("destroyed doomed", Recorder.Events);
        Assert.False(doomed.IsValid);
        Assert.False(script.Node.IsValid);
        Recorder.Events.Clear();
        scene.Update(Frame);
        Assert.DoesNotContain(Recorder.Events, e => e.Contains("doomed"));
        Assert.Throws<InvalidOperationException>(() => doomed.Position);
    }

    [Fact]
    public void Nodes_added_by_scripts_start_at_the_next_update()
    {
        Recorder.Events.Clear();
        var scene = NewScene();
        var spawner = scene.Create("spawner");
        spawner.Add(new ScriptComponent("Recorder") { Fields = new() { ["SpawnChildNamed"] = SceneSchema.Json("\"spawned\"") } });
        scene.Start();
        scene.Update(Frame);
        Assert.DoesNotContain("ready spawned", Recorder.Events);
        scene.Update(Frame);
        Assert.Contains("ready spawned", Recorder.Events);
        Assert.Contains("update spawned", Recorder.Events);
    }

    [Fact]
    public void Node_fields_on_scripts_resolve_to_live_nodes_after_loading()
    {
        var source = new World();
        var target = source.Create("Target");
        source.Add(source.Create("Watcher"), new ScriptComponent("Recorder") { Fields = new() { ["Target"] = SceneSchema.EntityRef(target) } });
        var doc = SceneLibrary.Write(source);

        var scene = NewScene();
        Assert.True(scene.Library.Instantiate(scene.World, doc).Ok);
        scene.Start();
        var watcher = (Recorder)scene.Find("Watcher").Get<ScriptComponent>().Instance!;
        Assert.True(watcher.Target.IsValid);
        Assert.Equal("Target", watcher.Target.Name);
        Assert.Equal(scene.Find("Target"), watcher.Target);
    }

    [Fact]
    public void World_transforms_and_animation_reach_the_renderer_and_hidden_nodes_do_not()
    {
        var w = new World();
        var parent = w.Create("parent");
        w.SetPosition(parent, new Vector3(10, 0, 0));
        var child = w.Create("child", parent);
        w.SetPosition(child, new Vector3(0, 2, 0));
        w.Add(child, new MeshRenderer("Assets/a.glb"));
        w.Add(child, new Animator("walk") { Time = 0.5f });
        var hidden = w.Create("hidden", parent);
        w.SetHidden(hidden, true);
        w.Add(w.Create("under hidden", hidden), new MeshRenderer("Assets/a.glb"));
        w.Add(w.Create("other"), new MeshRenderer("Assets/a.glb") { NoShadows = true });

        var draws = new List<SceneRenderer.SceneDraw>();
        SceneRenderer.Collect(w, draws);
        Assert.Equal(2, draws.Count);
        var c = draws.Single(d => d.Entity == child);
        HierarchyTests.AssertNear(new Vector3(10, 2, 0), c.World.Translation);
        Assert.Equal(("walk", 0.5f), (c.Clip, c.Time));
        Assert.True(draws.Single(d => d.Entity != child).NoShadows);
        Assert.All(draws, d => Assert.Equal("Assets/a.glb", d.Mesh)); // one model path: one shared GPU mesh

        // Moving the parent reaches the renderer through the cached world matrix
        w.SetPosition(parent, Vector3.Zero);
        SceneRenderer.Collect(w, draws);
        HierarchyTests.AssertNear(new Vector3(0, 2, 0), draws.Single(d => d.Entity == child).World.Translation);
    }

    [Fact]
    public void Unloading_a_scene_destroys_every_script_once()
    {
        Recorder.Events.Clear();
        var scene = NewScene();
        Scripted(scene, "a", Scripted(scene, "b"));
        scene.Start();
        scene.Unload();
        Assert.Equal(2, Recorder.Events.Count(e => e.StartsWith("destroyed")));
        Assert.Equal(0, scene.World.Count);
    }

    [Fact]
    public void A_scene_loads_assets_from_its_project_root_wherever_the_scenes_folder_is()
    {
        var dir = Directory.CreateTempSubdirectory("trinity-root-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "trinity.json"), """{ "scenes": "Content/Scenes" }""");
            var library = new SceneLibrary(dir);
            var w = new World();
            w.Add(w.Create("Thing"), new MeshRenderer("Assets/a.glb"));
            library.Save("Content/Scenes/x.scene.json", SceneLibrary.Write(w));

            var scene = Scene.Load(null!, Path.Combine(dir, "Content", "Scenes", "x.scene.json"));
            Assert.Equal(Path.GetFullPath(dir), Path.GetFullPath(scene.Root));
            Assert.Equal(1, scene.World.Count);
            var relative = Scene.Load(null!, "Content/Scenes/x.scene.json", root: dir);
            Assert.Equal(1, relative.World.Count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}


[CollectionDefinition("scripts", DisableParallelization = true)]
public class ScriptsCollection;
