using System.Numerics;
using System.Text.Json;
using Trinity.Scenes;
using Xunit;

namespace Trinity.Tests;

public sealed class SceneFileTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("trinity-scenes-").FullName;
    readonly SceneLibrary _library;

    public SceneFileTests() => _library = new SceneLibrary(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    static JsonElement J(string raw) => SceneSchema.Json(raw);

    /// <summary>Saves a world as a scene file and loads it into a fresh world.</summary>
    (World World, SceneProblems Problems) RoundTrip(World source, string path = "Scenes/test.scene.json")
    {
        _library.Save(path, SceneLibrary.Write(source));
        var (doc, problems) = _library.Load(path);
        Assert.True(problems.Ok, problems.ToString());
        var world = new World();
        var built = _library.Instantiate(world, doc!, name: path);
        Assert.True(built.Ok, built.ToString());
        return (world, built);
    }

    static EntityId Find(World w, string name) => w.All().First(e => w.Name(e) == name);

    [Fact]
    public void A_hierarchy_with_components_survives_save_and_load()
    {
        var w = new World();
        var root = w.Create("Root");
        w.SetPosition(root, new Vector3(1, 2, 3));
        var arm = w.Create("Arm", root);
        w.SetRotation(arm, new Vector3(0, 90, 0));
        w.SetScale(arm, new Vector3(2, 1, 1));
        w.SetHidden(arm, true);
        var hand = w.Create("Hand", arm);
        w.Create("Second", root);
        w.Add(hand, new MeshRenderer("Assets/hand.glb"));
        w.Add(hand, new Animator("wave") { Speed = 1.5f, Paused = true });
        w.Add(hand, new ScriptComponent("Wave") { Fields = new() { ["Height"] = J("2.5") } });
        var ids = (w.PersistentId(root), w.PersistentId(arm), w.PersistentId(hand));

        var (loaded, _) = RoundTrip(w);
        var r = Find(loaded, "Root");
        var a = Find(loaded, "Arm");
        var h = Find(loaded, "Hand");
        Assert.Equal(ids, (loaded.PersistentId(r), loaded.PersistentId(a), loaded.PersistentId(h)));
        Assert.Equal(new Vector3(1, 2, 3), loaded.Position(r));
        Assert.Equal(new Vector3(0, 90, 0), loaded.Rotation(a));
        Assert.Equal(new Vector3(2, 1, 1), loaded.Scale(a));
        Assert.True(loaded.Hidden(a));
        Assert.Equal(a, loaded.Parent(h));
        Assert.Equal(["Arm", "Second"], Children(loaded, r));
        Assert.Equal("Assets/hand.glb", loaded.Get<MeshRenderer>(h).Mesh);
        Assert.Equal(1.5f, loaded.Get<Animator>(h).Speed);
        Assert.True(loaded.Get<Animator>(h).Paused);
        Assert.Equal("Wave", loaded.Get<ScriptComponent>(h).Class);
        Assert.Equal(2.5f, loaded.Get<ScriptComponent>(h).Fields!["Height"].GetSingle());
        Assert.True(loaded.NextPersistentId > ids.Item3); // new nodes won't reuse saved ids
    }

    [Fact]
    public void Entity_references_are_saved_as_paths_and_reconnected_on_load()
    {
        var w = new World();
        var target = w.Create("Target");
        var follower = w.Create("Follower");
        w.Add(follower, new ScriptComponent("Follow") { Fields = new() { ["Target"] = SceneSchema.EntityRef(target) } });

        var doc = SceneLibrary.Write(w);
        var saved = doc.Nodes.Single(n => n.Name == "Follower").Components!["Script"]["Fields.Target"];
        Assert.Equal(w.PersistentId(target).ToString(), saved.GetProperty("$entity").GetString());

        var (loaded, _) = RoundTrip(w);
        var reference = SceneSchema.ReadEntity(loaded.Get<ScriptComponent>(Find(loaded, "Follower")).Fields!["Target"]);
        Assert.Equal(Find(loaded, "Target"), reference);
    }

    [Fact]
    public void A_reference_to_a_node_that_is_gone_loads_as_empty_with_a_warning()
    {
        var doc = new SceneDocument
        {
            Nodes = [new NodeRecord { Id = 1, Name = "Follower", Components = new() { ["Script"] = new() { ["Class"] = J("\"Follow\""), ["Fields.Target"] = J("{\"$entity\": \"99\"}") } } }],
        };
        var world = new World();
        var problems = _library.Instantiate(world, doc);
        Assert.Contains(problems.Warnings, w => w.Contains("doesn't exist"));
        Assert.True(SceneSchema.ReadEntity(world.Get<ScriptComponent>(Find(world, "Follower")).Fields!["Target"]).IsNone);
    }

    [Fact]
    public void Missing_assets_are_reported_but_the_scene_still_loads()
    {
        var w = new World();
        w.Add(w.Create("Thing"), new MeshRenderer("Assets/not-here.glb"));
        var doc = SceneLibrary.Write(w);
        Assert.Equal(["Assets/not-here.glb"], _library.MissingDependencies(doc));
        var (loaded, _) = RoundTrip(w);
        Assert.Equal("Assets/not-here.glb", loaded.Get<MeshRenderer>(Find(loaded, "Thing")).Mesh);
    }

    [Theory]
    [InlineData("{ \"Format\": 2, \"Nodes\": [ { \"Id\": 1, ", "malformed JSON at line")]
    [InlineData("[1, 2]", "not a scene")]
    [InlineData("{ \"Format\": 9, \"Nodes\": [] }", "newer")]
    [InlineData("{ \"Format\": 2, \"Nodes\": [ { \"Id\": 1 }, { \"Id\": 1 } ] }", "id used twice")]
    [InlineData("{ \"Format\": 2, \"Nodes\": [ { \"Id\": 2, \"Parent\": 1 }, { \"Id\": 1 } ] }", "before its parent")]
    [InlineData("{ \"Format\": 2, \"Nodes\": [ { \"Id\": 2, \"Parent\": 7 } ] }", "doesn't exist")]
    [InlineData("{ \"Format\": 2, \"Nodes\": [ { \"Id\": 0 } ] }", "positive")]
    public void Broken_files_are_refused_with_a_useful_error(string json, string expected)
    {
        var (doc, problems) = SceneLibrary.Parse(json, "bad.scene.json");
        Assert.Null(doc);
        Assert.Contains(problems.Errors, e => e.Contains(expected) && e.Contains("bad.scene.json"));
    }

    [Fact]
    public void Unknown_components_and_wrong_value_types_are_warnings_not_failures()
    {
        var json = """{ "Format": 2, "Nodes": [ { "Id": 1, "Components": { "Teleporter": {}, "MeshRenderer": { "Mesh": 12, "Colour": "red" } } } ] }""";
        var (doc, problems) = SceneLibrary.Parse(json, "odd.scene.json");
        Assert.NotNull(doc);
        Assert.Contains(problems.Warnings, w => w.Contains("unknown component 'Teleporter'"));
        Assert.Contains(problems.Warnings, w => w.Contains("Mesh wants Asset"));
        Assert.Contains(problems.Warnings, w => w.Contains("no property 'Colour'"));
    }

    [Fact]
    public void Format_1_scenes_are_migrated()
    {
        var json = """
            {
              "Objects": [
                { "Name": "Spinner", "Model": "Assets/a.glb", "Position": [1, 0, 0], "Rotation": [0, 45, 0], "Scale": 2,
                  "Clip": "idle", "Speed": 0.5, "Playing": false, "Script": "Spin", "Fields": { "DegreesPerSecond": 60 } },
                { "Name": "Empty", "Model": "", "Position": [0, 0, 0], "Rotation": [0, 0, 0], "Scale": 1, "Clip": "", "Speed": 1, "Playing": true }
              ],
              "Environment": { "SunElevation": 12 },
              "Camera": { "Target": [0, 1, 0], "Yaw": 10, "Pitch": 20, "Distance": 5 }
            }
            """;
        var (doc, problems) = SceneLibrary.Parse(json, "old.scene.json");
        Assert.True(problems.Ok, problems.ToString());
        Assert.Equal(SceneDocument.CurrentFormat, doc!.Format);
        Assert.Equal(12, doc.Environment!.SunElevation);
        Assert.Equal(5, doc.Camera!.Distance);
        var w = new World();
        _library.Instantiate(w, doc);
        var s = Find(w, "Spinner");
        Assert.Equal(new Vector3(2), w.Scale(s));
        Assert.Equal("Assets/a.glb", w.Get<MeshRenderer>(s).Mesh);
        Assert.Equal(0.5f, w.Get<Animator>(s).Speed);
        Assert.True(w.Get<Animator>(s).Paused);
        Assert.Equal(60, w.Get<ScriptComponent>(s).Fields!["DegreesPerSecond"].GetSingle());
        Assert.False(w.Has<MeshRenderer>(Find(w, "Empty")));
    }

    static List<string> Children(World w, EntityId e)
    {
        var list = new List<string>();
        foreach (var c in w.Children(e))
            list.Add(w.Name(c));
        return list;
    }
}

public sealed class InstanceTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("trinity-instances-").FullName;
    readonly SceneLibrary _library;

    public InstanceTests()
    {
        _library = new SceneLibrary(_dir);
        // Door.scene: a panel; House.scene: a roof and two doors (nested instances)
        var door = new World();
        door.Add(door.Create("Panel"), new MeshRenderer("Assets/door.glb"));
        _library.Save("Door.scene.json", SceneLibrary.Write(door));
        _library.Save("House.scene.json", HouseDocument(roofHeight: 3));
    }

    SceneDocument HouseDocument(float roofHeight, bool withChimney = true)
    {
        var house = new World();
        var roof = house.Create("Roof");
        house.SetPosition(roof, new Vector3(0, roofHeight, 0));
        house.Add(roof, new MeshRenderer("Assets/roof.glb"));
        _library.Instance(house, "Door.scene.json", EntityId.None, out _, "Front door");
        _library.Instance(house, "Door.scene.json", EntityId.None, out _, "Back door");
        if (withChimney)
            house.Create("Chimney", roof);
        return SceneLibrary.Write(house);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    static JsonElement J(string raw) => SceneSchema.Json(raw);

    static EntityId Find(World w, string name, EntityId under = default) =>
        (under.IsNone ? w.All() : w.Subtree(under)).First(e => w.Name(e) == name);

    [Fact]
    public void Nested_scenes_are_instanced_with_their_nodes_marked_as_inherited()
    {
        var w = new World();
        var house = _library.Instance(w, "House.scene.json", EntityId.None, out var problems);
        Assert.True(problems.Ok, problems.ToString());
        // House node > Roof (> Chimney), Front door (> Panel), Back door (> Panel)
        Assert.Equal(7, w.Count);
        var panel = Find(w, "Panel", Find(w, "Front door"));
        Assert.Equal(house, w.Get<Inherited>(panel).InstanceRoot);
        var frontId = w.Get<Inherited>(Find(w, "Front door")).SourcePath;
        Assert.Equal($"{frontId}/{w.Get<Inherited>(panel).SourcePath.Split('/')[1]}", w.Get<Inherited>(panel).SourcePath);
        Assert.Equal("Assets/door.glb", w.Get<MeshRenderer>(panel).Mesh);
        Assert.False(w.Has<Inherited>(house));
    }

    [Fact]
    public void Instances_of_one_scene_are_independent_and_share_their_asset_references()
    {
        var w = new World();
        var a = _library.Instance(w, "House.scene.json", EntityId.None, out _, "A");
        var b = _library.Instance(w, "House.scene.json", EntityId.None, out _, "B");
        w.SetPosition(Find(w, "Roof", a), new Vector3(9, 9, 9));
        Assert.Equal(new Vector3(0, 3, 0), w.Position(Find(w, "Roof", b)));
        // Same path, so the renderer loads the mesh once for both
        Assert.Equal(w.Get<MeshRenderer>(Find(w, "Roof", a)).Mesh, w.Get<MeshRenderer>(Find(w, "Roof", b)).Mesh);
        Assert.Single(SceneLibrary.Dependencies(SceneLibrary.Write(w)).Scenes);
    }

    /// <summary>Sets a property on an inherited node and records it as the instance's override, as the editor does.</summary>
    static void Override(World w, EntityId node, string property, JsonElement value)
    {
        var inherited = w.Get<Inherited>(node);
        ref var instance = ref w.Get<SceneInstance>(inherited.InstanceRoot);
        if (!instance.Overrides.TryGetValue(inherited.SourcePath, out var props))
            instance.Overrides[inherited.SourcePath] = props = [];
        props[property] = value;
        Assert.Null(SceneSchema.Set(w, node, property, value));
    }

    World Level(out EntityId a, out EntityId b)
    {
        var w = new World();
        a = _library.Instance(w, "House.scene.json", EntityId.None, out _, "A");
        b = _library.Instance(w, "House.scene.json", EntityId.None, out _, "B");
        return w;
    }

    [Fact]
    public void Overrides_survive_saving_and_reloading_the_level()
    {
        var w = Level(out var a, out _);
        Override(w, Find(w, "Roof", a), "Position", SceneSchema.Vector(new Vector3(0, 10, 0)));
        Override(w, Find(w, "Panel", Find(w, "Back door", a)), "MeshRenderer.Mesh", J("\"Assets/fancy-door.glb\""));

        var doc = SceneLibrary.Write(w);
        // Only the two instance nodes are saved, not their contents
        Assert.Equal(2, doc.Nodes.Count);
        Assert.Equal(2, doc.Nodes.Single(n => n.Name == "A").Overrides!.Count);
        _library.Save("Level.scene.json", doc);

        var (loaded, problems) = _library.Load("Level.scene.json");
        Assert.True(problems.Ok);
        var w2 = new World();
        Assert.True(_library.Instantiate(w2, loaded!).Ok);
        var a2 = Find(w2, "A");
        var b2 = Find(w2, "B");
        Assert.Equal(new Vector3(0, 10, 0), w2.Position(Find(w2, "Roof", a2)));
        Assert.Equal(new Vector3(0, 3, 0), w2.Position(Find(w2, "Roof", b2)));
        Assert.Equal("Assets/fancy-door.glb", w2.Get<MeshRenderer>(Find(w2, "Panel", Find(w2, "Back door", a2))).Mesh);
        Assert.Equal("Assets/door.glb", w2.Get<MeshRenderer>(Find(w2, "Panel", Find(w2, "Front door", a2))).Mesh);
    }

    [Fact]
    public void Resetting_an_override_brings_back_the_source_value()
    {
        var w = Level(out var a, out _);
        var roof = Find(w, "Roof", a);
        Override(w, roof, "Position", SceneSchema.Vector(new Vector3(0, 10, 0)));
        var path = w.Get<Inherited>(roof).SourcePath;
        w.Get<SceneInstance>(a).Overrides[path].Remove("Position");
        Assert.True(_library.Rebuild(w, a).Ok);
        Assert.Equal(new Vector3(0, 3, 0), w.Position(Find(w, "Roof", a)));
    }

    [Fact]
    public void Source_changes_reach_instances_except_where_overridden()
    {
        var w = Level(out var a, out var b);
        Override(w, Find(w, "Roof", a), "Position", SceneSchema.Vector(new Vector3(0, 10, 0)));
        Override(w, Find(w, "Roof", a), "MeshRenderer.NoShadows", J("true"));

        // The house's roof moves up in its own scene
        _library.Save("House.scene.json", HouseDocument(roofHeight: 4));
        Assert.True(_library.Rebuild(w, a).Ok);
        Assert.True(_library.Rebuild(w, b).Ok);
        Assert.Equal(new Vector3(0, 10, 0), w.Position(Find(w, "Roof", a))); // overridden: kept
        Assert.True(w.Get<MeshRenderer>(Find(w, "Roof", a)).NoShadows);
        Assert.Equal(new Vector3(0, 4, 0), w.Position(Find(w, "Roof", b))); // not overridden: follows the source
    }

    [Fact]
    public void Overrides_of_deleted_source_nodes_are_reported_and_kept_while_others_still_apply()
    {
        var w = Level(out var a, out _);
        Override(w, Find(w, "Chimney", a), "Name", J("\"Smokestack\""));
        Override(w, Find(w, "Roof", a), "Position", SceneSchema.Vector(new Vector3(0, 10, 0)));
        var chimneyPath = w.Get<Inherited>(Find(w, "Smokestack", a)).SourcePath;

        _library.Save("House.scene.json", HouseDocument(roofHeight: 3, withChimney: false));
        var problems = _library.Rebuild(w, a);
        Assert.Contains(problems.Warnings, x => x.Contains($"node {chimneyPath}") && x.Contains("no longer has"));
        Assert.Equal(new Vector3(0, 10, 0), w.Position(Find(w, "Roof", a)));
        // Still saved, in case the node comes back
        Assert.True(SceneLibrary.Write(w).Nodes.Single(n => n.Name == "A").Overrides!.ContainsKey(chimneyPath));
    }

    [Fact]
    public void Local_children_of_an_instance_survive_a_rebuild()
    {
        var w = Level(out var a, out _);
        var lamp = w.Create("Lamp", a);
        Assert.True(_library.Rebuild(w, a).Ok);
        Assert.True(w.IsAlive(lamp));
        Assert.Equal(a, w.Parent(lamp));
        Assert.Contains(SceneLibrary.Write(w).Nodes, n => n.Name == "Lamp" && n.Parent == w.PersistentId(a));
    }

    [Fact]
    public void A_scene_that_contains_itself_is_refused_without_looping()
    {
        var x = new World();
        _library.Instance(x, "Loop2.scene.json", EntityId.None, out _);
        _library.Save("Loop1.scene.json", SceneLibrary.Write(x));
        var y = new World();
        _library.Instance(y, "Loop1.scene.json", EntityId.None, out _);
        _library.Save("Loop2.scene.json", SceneLibrary.Write(y));
        _library.Invalidate("Loop1.scene.json");

        var w = new World();
        _library.Instance(w, "Loop1.scene.json", EntityId.None, out var problems);
        Assert.Contains(problems.Errors, e => e.Contains("contains itself"));
        Assert.True(w.Count < 10);
    }
}
