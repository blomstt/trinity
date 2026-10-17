using System.Numerics;
using System.Text.Json;
using Trinity.Editor;
using Trinity.Scenes;
using Xunit;

namespace Trinity.Tests;

/// <summary>The editor's scene model: edits, undo and redo, overrides on instances, copying.</summary>
public sealed class EditorTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("trinity-editor-").FullName;
    readonly SceneLibrary _library;

    public EditorTests()
    {
        _library = new SceneLibrary(_dir);
        var door = new World();
        var panel = door.Create("Panel");
        door.SetPosition(panel, new Vector3(0, 1, 0));
        door.Add(panel, new MeshRenderer("Assets/door.glb"));
        _library.Save("Scenes/door.scene.json", SceneLibrary.Write(door));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    EditorScene NewScene() => EditorScene.New(_library, "Scenes/test.scene.json");

    static EntityId Find(World w, string name) => w.All().First(e => w.Name(e) == name);

    [Fact]
    public void Property_edits_undo_and_redo_and_a_drag_is_one_step()
    {
        var s = NewScene();
        var e = s.CreateNode("A");
        for (var x = 1; x <= 5; x++)
            s.SetProperty(e, "Position", SceneSchema.Vector(new Vector3(x, 0, 0)), merge: true);
        s.CloseEdit();
        Assert.Equal(new Vector3(5, 0, 0), s.World.Position(e));
        Assert.True(s.Undo());
        Assert.Equal(Vector3.Zero, s.World.Position(Find(s.World, "A")));
        Assert.True(s.Redo());
        Assert.Equal(new Vector3(5, 0, 0), s.World.Position(Find(s.World, "A")));
        // Undo past the creation removes the node; redo brings it back with the same persistent id
        var id = s.World.PersistentId(Find(s.World, "A"));
        s.Undo();
        s.Undo();
        Assert.Equal(0, s.World.Count);
        s.Redo();
        Assert.Equal(id, s.World.PersistentId(Find(s.World, "A")));
        Assert.Equal(Vector3.Zero, s.World.Position(Find(s.World, "A")));
    }

    [Fact]
    public void Separate_edits_are_separate_steps_and_a_new_edit_clears_redo()
    {
        var s = NewScene();
        var e = s.CreateNode("A");
        s.SetProperty(e, "Position", SceneSchema.Vector(new Vector3(1, 0, 0)), merge: true);
        s.CloseEdit();
        s.SetProperty(e, "Position", SceneSchema.Vector(new Vector3(2, 0, 0)), merge: true);
        s.CloseEdit();
        s.Undo();
        Assert.Equal(new Vector3(1, 0, 0), s.World.Position(e));
        s.SetProperty(e, "Name", SceneSchema.Text("B"));
        Assert.False(s.CanRedo);
        Assert.Equal("change Name", s.UndoLabel);
    }

    [Fact]
    public void Delete_and_undo_restore_the_subtree_components_and_selection()
    {
        var s = NewScene();
        var parent = s.CreateNode("Parent");
        var child = s.CreateNode("Child", parent);
        s.AddComponent(child, "MeshRenderer");
        s.SetProperty(child, "MeshRenderer.Mesh", SceneSchema.Text("Assets/a.glb"));
        s.Select(parent);
        Assert.Null(s.Delete(s.Selection));
        Assert.Equal(0, s.World.Count);
        Assert.Empty(s.Selection);
        s.Undo();
        var restored = Find(s.World, "Child");
        Assert.Equal("Parent", s.World.Name(s.World.Parent(restored)));
        Assert.Equal("Assets/a.glb", s.World.Get<MeshRenderer>(restored).Mesh);
        Assert.Equal("Parent", s.World.Name(s.Primary));
    }

    [Fact]
    public void Reparenting_keeps_world_position_refuses_cycles_and_undoes()
    {
        var s = NewScene();
        var a = s.CreateNode("A");
        s.SetProperty(a, "Position", SceneSchema.Vector(new Vector3(10, 0, 0)));
        var b = s.CreateNode("B");
        s.SetProperty(b, "Position", SceneSchema.Vector(new Vector3(0, 0, 5)));
        Assert.Null(s.Reparent(b, a));
        b = Find(s.World, "B");
        Assert.Equal(new Vector3(0, 0, 5), s.World.WorldPosition(b));
        Assert.Equal(new Vector3(-10, 0, 5), s.World.Position(b));
        Assert.NotNull(s.Reparent(Find(s.World, "A"), b)); // into its own child
        s.Undo();
        Assert.True(s.World.Parent(Find(s.World, "B")).IsNone);
        Assert.Equal(new Vector3(0, 0, 5), s.World.Position(Find(s.World, "B")));
    }

    [Fact]
    public void Reordering_siblings_is_undoable()
    {
        var s = NewScene();
        s.CreateNode("A");
        s.CreateNode("B");
        var c = s.CreateNode("C");
        Assert.Null(s.Reparent(c, EntityId.None, 0));
        Assert.Equal(["C", "A", "B"], s.World.Roots.ToList().Select(s.World.Name));
        s.Undo();
        Assert.Equal(["A", "B", "C"], s.World.Roots.ToList().Select(s.World.Name));
    }

    [Fact]
    public void Editing_an_inherited_node_records_an_override_that_saves_resets_and_undoes()
    {
        var s = NewScene();
        var door = s.InstantiateScene("Scenes/door.scene.json", Vector3.Zero, out var problems);
        Assert.True(problems.Ok, problems.ToString());
        var panel = Find(s.World, "Panel");
        Assert.True(s.IsInherited(panel));
        Assert.False(s.IsOverridden(panel, "Position"));

        s.SetProperty(panel, "Position", SceneSchema.Vector(new Vector3(0, 5, 0)));
        Assert.True(s.IsOverridden(panel, "Position"));
        s.Save();
        var saved = File.ReadAllText(Path.Combine(_dir, "Scenes/test.scene.json"));
        Assert.Contains("Overrides", saved);
        Assert.DoesNotContain("Panel", saved); // the source's nodes aren't copied into the scene

        // Loading applies the override
        var again = EditorScene.Load(_library, "Scenes/test.scene.json", out var loadProblems);
        Assert.True(loadProblems.Ok, loadProblems.ToString());
        Assert.Equal(new Vector3(0, 5, 0), again.World.Position(Find(again.World, "Panel")));

        s.ResetOverride(Find(s.World, "Panel"), "Position");
        Assert.Equal(new Vector3(0, 1, 0), s.World.Position(Find(s.World, "Panel")));
        Assert.False(s.IsOverridden(Find(s.World, "Panel"), "Position"));
        s.Undo(); // the reset
        Assert.True(s.IsOverridden(Find(s.World, "Panel"), "Position"));
        s.Undo(); // the edit: no override any more
        Assert.False(s.IsOverridden(Find(s.World, "Panel"), "Position"));
        Assert.Equal(new Vector3(0, 1, 0), s.World.Position(Find(s.World, "Panel")));
        Assert.Equal(0, s.OverrideCount(Find(s.World, "door")));
    }

    [Fact]
    public void Inherited_nodes_cant_be_deleted_moved_or_given_components()
    {
        var s = NewScene();
        s.InstantiateScene("Scenes/door.scene.json", Vector3.Zero, out _);
        var panel = Find(s.World, "Panel");
        var other = s.CreateNode("Other");
        Assert.NotNull(s.Delete([panel]));
        Assert.NotNull(s.Reparent(panel, other));
        Assert.NotNull(s.Reparent(other, panel));
        Assert.NotNull(s.AddComponent(panel, "Camera"));
        Assert.NotNull(s.RemoveComponent(panel, "MeshRenderer"));
        Assert.True(s.World.IsAlive(panel));
    }

    [Fact]
    public void Source_changes_reach_instances_and_keep_their_overrides()
    {
        var s = NewScene();
        s.InstantiateScene("Scenes/door.scene.json", Vector3.Zero, out _);
        s.SetProperty(Find(s.World, "Panel"), "Position", SceneSchema.Vector(new Vector3(0, 5, 0)));

        // The door scene gains a handle and a new mesh
        var door = EditorScene.Load(_library, "Scenes/door.scene.json", out _);
        door.SetProperty(Find(door.World, "Panel"), "MeshRenderer.Mesh", SceneSchema.Text("Assets/door2.glb"));
        door.CreateNode("Handle", Find(door.World, "Panel"));
        door.Save();

        Assert.Equal(1, s.ReloadInstancesOf("Scenes/door.scene.json"));
        var panel = Find(s.World, "Panel");
        Assert.Equal("Assets/door2.glb", s.World.Get<MeshRenderer>(panel).Mesh);
        Assert.Equal(new Vector3(0, 5, 0), s.World.Position(panel));
        Assert.True(s.IsInherited(Find(s.World, "Handle")));
    }

    [Fact]
    public void Duplicating_copies_the_subtree_with_new_ids_and_points_internal_references_at_the_copies()
    {
        var s = NewScene();
        var outside = s.CreateNode("Outside");
        var root = s.CreateNode("Root");
        var child = s.CreateNode("Child", root);
        s.SetScript(root, "Recorder");
        s.SetProperty(root, "Script.Fields.Target", SceneSchema.EntityRef(child));
        s.SetScript(child, "Recorder");
        s.SetProperty(child, "Script.Fields.Target", SceneSchema.EntityRef(outside));

        var copies = s.Duplicate([Find(s.World, "Root")]);
        var copy = Assert.Single(copies);
        var w = s.World;
        Assert.Equal("Root 2", w.Name(copy));
        Assert.Equal(["Outside", "Root", "Root 2"], w.Roots.ToList().Select(w.Name));
        var copiedChild = w.Children(copy).ToList().Single();
        Assert.NotEqual(w.PersistentId(Find(w, "Root")), w.PersistentId(copy));
        Assert.Equal(w.All().Count, w.All().Select(w.PersistentId).Distinct().Count());
        Assert.Equal(copiedChild, SceneSchema.ReadEntity(w.Get<ScriptComponent>(copy).Fields!["Target"]));
        Assert.Equal(Find(w, "Outside"), SceneSchema.ReadEntity(w.Get<ScriptComponent>(copiedChild).Fields!["Target"]));
        Assert.Equal(copy, s.Primary);
        s.Undo();
        Assert.Equal(3, s.World.Count);
    }

    [Fact]
    public void Components_add_and_remove_with_undo()
    {
        var s = NewScene();
        var e = s.CreateNode("Cam");
        s.AddComponent(e, "Camera");
        Assert.True(s.World.Has<Camera3D>(e));
        s.SetProperty(e, "Camera.FieldOfView", SceneSchema.Float(75));
        s.RemoveComponent(e, "Camera");
        Assert.False(s.World.Has<Camera3D>(Find(s.World, "Cam")));
        s.Undo();
        Assert.Equal(75, s.World.Get<Camera3D>(Find(s.World, "Cam")).FieldOfView);
    }

    [Fact]
    public void Script_field_undo_returns_to_the_scripts_default()
    {
        var s = NewScene();
        var e = s.CreateNode("A");
        s.SetScript(e, "Recorder");
        s.SetProperty(e, "Script.Fields.SpawnChildNamed", SceneSchema.Text("x"));
        s.Undo();
        Assert.False(s.World.Get<ScriptComponent>(e).Fields?.ContainsKey("SpawnChildNamed") ?? false);
    }

    [Fact]
    public void Environment_changes_merge_per_drag_and_undo()
    {
        var s = NewScene();
        var before = s.EnvironmentJson();
        s.Environment.Exposure = 1;
        s.EnvironmentChanged(before, merge: true);
        var mid = s.EnvironmentJson();
        s.Environment.Exposure = 2;
        s.EnvironmentChanged(mid, merge: true);
        s.CloseEdit();
        s.Undo();
        Assert.Equal(0, s.Environment.Exposure);
        Assert.False(s.CanUndo);
    }

    [Fact]
    public void Dirty_tracks_unsaved_changes()
    {
        var s = NewScene();
        Assert.False(s.Dirty);
        s.CreateNode("A");
        Assert.True(s.Dirty);
        s.Save();
        Assert.False(s.Dirty);
        s.Undo();
        Assert.True(s.Dirty);
    }

    [Fact]
    public void Multi_select_toggles_with_add()
    {
        var s = NewScene();
        var a = s.CreateNode("A");
        var b = s.CreateNode("B");
        s.Select(a);
        s.Select(b, add: true);
        Assert.Equal(2, s.Selection.Count);
        Assert.Equal(b, s.Primary);
        s.Select(b, add: true);
        Assert.Equal([a], s.Selection);
        Assert.Equal(a, s.Primary);
    }

    [Fact]
    public void Runtime_assemblies_dont_reference_the_editor_or_its_ui()
    {
        foreach (var assembly in new[] { typeof(World).Assembly, typeof(Trinity.Rendering.Renderer).Assembly, typeof(Trinity.Graphics.GraphicsDevice).Assembly })
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                Assert.DoesNotContain("ImGui", reference.Name);
                Assert.DoesNotContain("Editor", reference.Name);
            }
    }

    [Fact]
    public void Undo_history_is_capped()
    {
        var s = NewScene();
        var e = s.CreateNode("A");
        for (var i = 0; i < 300; i++)
            s.SetProperty(e, "Position", SceneSchema.Vector(new Vector3(i, 0, 0)));
        var steps = 0;
        while (s.Undo())
            steps++;
        Assert.Equal(200, steps);
    }
}
