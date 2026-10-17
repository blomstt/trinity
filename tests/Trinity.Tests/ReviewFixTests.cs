using System.Numerics;
using System.Text.Json;
using Brutal.GlfwApi;
using Trinity.Editor;
using Trinity.Scenes;
using Xunit;

namespace Trinity.Tests;

/// <summary>Regression tests for the problems found reviewing the project (crashes, lost work, input).</summary>
public sealed class ReviewFixTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("trinity-review-").FullName;
    readonly SceneLibrary _library;

    public ReviewFixTests() => _library = new SceneLibrary(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    EditorScene NewScene() => EditorScene.New(_library, "Scenes/a.scene.json");

    (SceneDocument? Doc, SceneProblems Problems) Parse(string json) => SceneLibrary.Parse(json, "test");

    // --- Crashes ------------------------------------------------------------------------------------

    [Fact]
    public void Reparenting_under_a_node_scaled_to_nothing_keeps_the_local_transform_and_saves()
    {
        var s = NewScene();
        var parent = s.CreateNode("Flat");
        s.SetProperty(parent, "Scale", SceneSchema.Vector(Vector3.Zero));
        var child = s.CreateNode("Child");
        s.SetProperty(child, "Position", SceneSchema.Vector(new Vector3(1, 2, 3)));
        Assert.Null(s.Reparent(child, parent));
        child = s.World.All().First(e => s.World.Name(e) == "Child");
        Assert.Equal(new Vector3(1, 2, 3), s.World.Position(child));
        s.Save(); // used to throw: NaN can't be written as JSON
    }

    [Fact]
    public void A_structural_edit_that_fails_leaves_the_scene_and_history_as_they_were()
    {
        var s = NewScene();
        s.CreateNode("Kept");
        var steps = s.UndoSteps.Count();
        Assert.Throws<IOException>(() => s.Structural<bool>("broken", () =>
        {
            s.World.Create("Half made");
            throw new IOException("disk full");
        }));
        Assert.Equal(["Kept"], s.World.All().Select(s.World.Name));
        Assert.Equal(steps, s.UndoSteps.Count());
    }

    [Fact]
    public void Numbers_json_cant_hold_are_written_as_the_nearest_it_can()
    {
        Assert.Equal(float.MaxValue, SceneSchema.Float(float.PositiveInfinity).GetSingle());
        Assert.Equal(0, SceneSchema.Float(float.NaN).GetSingle());
        Assert.Equal(new Vector3(0, float.MinValue, 1), SceneSchema.ReadVector(SceneSchema.Vector(new Vector3(float.NaN, float.NegativeInfinity, 1))));
        var w = new World();
        var e = w.Create("Broken");
        w.SetPosition(e, new Vector3(float.NaN, 0, 0));
        w.Add(e, new Camera3D { FieldOfView = float.PositiveInfinity });
        var json = SceneLibrary.Serialize(SceneLibrary.Write(w)); // used to throw
        Assert.True(Parse(json).Doc != null);
    }

    [Fact]
    public void Non_finite_numbers_in_a_file_are_reported_and_left_out()
    {
        var (doc, problems) = Parse("""{"Format":2,"Nodes":[{"Id":1,"Position":[1e40,0,0],"Components":{"Camera":{"FieldOfView":1e40}}}]}""");
        Assert.NotNull(doc);
        Assert.Equal(Vector3.Zero, doc!.Nodes[0].Position);
        Assert.Contains(problems.Warnings, w => w.Contains("too large"));
        Assert.Contains(problems.Warnings, w => w.Contains("FieldOfView"));
        var world = new World();
        _library.Instantiate(world, doc);
        Assert.True(float.IsFinite(world.Get<Camera3D>(world.All()[0]).FieldOfViewOrDefault));
    }

    [Fact]
    public void A_null_node_list_is_read_as_empty()
    {
        var (doc, problems) = Parse("""{"Format":2,"Nodes":null}""");
        Assert.NotNull(doc);
        Assert.Empty(doc!.Nodes);
        Assert.NotEmpty(problems.Warnings);
    }

    [Fact]
    public void A_node_that_is_its_own_parent_is_an_error_not_a_crash()
    {
        var (doc, problems) = Parse("""{"Format":2,"Nodes":[{"Id":1},{"Id":2,"Parent":2}]}""");
        Assert.Null(doc);
        Assert.Contains(problems.Errors, e => e.Contains("its own parent"));
    }

    [Fact]
    public void Building_a_document_with_a_missing_parent_puts_the_node_at_the_top()
    {
        var doc = new SceneDocument { Nodes = [new NodeRecord { Id = 5, Parent = 99, Name = "Orphan" }] };
        var world = new World();
        _library.Instantiate(world, doc); // unvalidated (made in code): used to throw
        Assert.True(world.Parent(world.All()[0]).IsNone);
    }

    // --- Lost work ----------------------------------------------------------------------------------

    [Fact]
    public void A_new_scene_never_takes_the_path_of_an_existing_one()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "Scenes"));
        Assert.Equal("Scenes/untitled.scene.json", EditorApp.UntitledPath(_dir, "Scenes"));
        File.WriteAllText(Path.Combine(_dir, "Scenes/untitled.scene.json"), "{}");
        Assert.Equal("Scenes/untitled 2.scene.json", EditorApp.UntitledPath(_dir, "Scenes"));
        File.WriteAllText(Path.Combine(_dir, "Scenes/untitled 2.autosave.scene.json"), "{}");
        Assert.Equal("Scenes/untitled 3.scene.json", EditorApp.UntitledPath(_dir, "Scenes"));
    }

    [Fact]
    public void Unsaved_changes_kept_in_an_autosave_can_be_restored_and_saving_clears_it()
    {
        var s = NewScene();
        s.CreateNode("Saved");
        s.Save();
        s.CreateNode("Not saved yet");
        Thread.Sleep(20); // the autosave must be newer than the file
        s.Autosave();
        Assert.True(EditorScene.HasNewerAutosave(_library, "Scenes/a.scene.json"));

        var restored = EditorScene.LoadAutosave(_library, "Scenes/a.scene.json", out var problems);
        Assert.True(problems.Ok, problems.ToString());
        Assert.Equal("Scenes/a.scene.json", restored.Path);
        Assert.True(restored.Dirty);
        Assert.Equal(["Saved", "Not saved yet"], restored.World.All().Select(restored.World.Name));

        restored.Save();
        Assert.False(File.Exists(Path.Combine(_dir, "Scenes/a.autosave.scene.json")));
        Assert.False(EditorScene.HasNewerAutosave(_library, "Scenes/a.scene.json"));
    }

    [Theory]
    [InlineData("rock.glb", "boulder", "boulder.glb")]
    [InlineData("rock.glb", "boulder.gltf", "boulder.gltf")]
    [InlineData("level.scene.json", "level2", "level2.scene.json")]
    [InlineData("level.scene.json", "level2.json", "level2.json.scene.json")]
    [InlineData("level.scene.json", "level2.scene.json", "level2.scene.json")]
    [InlineData("README", "NOTES", "NOTES")]
    public void Renaming_keeps_the_kind_of_file(string original, string typed, string expected) =>
        Assert.Equal(expected, ProjectFiles.KeepExtension(original, typed));

    // --- Other ----------------------------------------------------------------------------------------

    [Fact]
    public void Losing_focus_releases_what_was_held()
    {
        var input = new Input();
        input.Bind("run", GlfwKey.W);
        input.Key(GlfwKey.W, true);
        input.BeginFrame();
        Assert.True(input.Down("run"));
        input.ReleaseAll(); // alt-tab: the release goes to another window
        input.BeginFrame();
        Assert.False(input.Down("run"));
        Assert.True(input.Released("run"));
    }

    [Fact]
    public void Changed_files_are_noticed_until_dealt_with()
    {
        var stamps = new AssetStamps();
        var model = Path.Combine(_dir, "a.glb");
        string File(string key) => Path.Combine(_dir, key);
        stamps.Track("a.glb", model); // missing at first (a failed load)
        Assert.Empty(stamps.Changed(File));
        System.IO.File.WriteAllText(model, "now it exists");
        Assert.Equal(["a.glb"], stamps.Changed(File));
        Assert.Equal(["a.glb"], stamps.Changed(File)); // still changed: nothing dealt with it
        stamps.Track("a.glb", model);
        Assert.Empty(stamps.Changed(File));
        System.IO.File.SetLastWriteTimeUtc(model, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(["a.glb"], stamps.Changed(File));
        stamps.Forget("a.glb");
        Assert.Empty(stamps.Changed(File));
    }

    [Fact]
    public void Only_real_build_output_is_left_out()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "bin"));
        Directory.CreateDirectory(Path.Combine(_dir, "Assets", "bin"));
        File.WriteAllText(Path.Combine(_dir, "Assets", "bin", "crate.glb"), "");
        Assert.False(ProjectFiles.Ignored(Path.Combine(_dir, "Assets"), "bin")); // an asset folder
        Assert.False(ProjectFiles.Ignored(_dir, "bin")); // no project here yet
        File.WriteAllText(Path.Combine(_dir, "Game.csproj"), "<Project />");
        Assert.True(ProjectFiles.Ignored(_dir, "bin")); // beside a .csproj: build output
        Assert.True(ProjectFiles.Ignored(_dir, ".git"));

        var copy = Path.Combine(Path.GetTempPath(), "trinity-copy-" + Guid.NewGuid().ToString("N"));
        try
        {
            ProjectFiles.Copy(_dir, copy);
            Assert.True(File.Exists(Path.Combine(copy, "Assets", "bin", "crate.glb")));
            Assert.False(Directory.Exists(Path.Combine(copy, "bin")));
        }
        finally
        {
            if (Directory.Exists(copy))
                Directory.Delete(copy, recursive: true);
        }
    }
}
