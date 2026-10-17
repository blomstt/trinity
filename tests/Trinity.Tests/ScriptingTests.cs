using System.Numerics;
using System.Text.Json;
using Trinity.Editor;
using Trinity.Editor.Scripting;
using Trinity.Scenes;
using Xunit;

namespace Trinity.Tests;

public sealed class ScriptingTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("trinity-scripts-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Index_finds_scripts_and_their_inspector_fields()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "Scripts"));
        File.WriteAllText(Path.Combine(_dir, "Scripts", "Mover.cs"), """
            using System.Numerics;
            using Trinity.Scenes;

            // class Fake : Script  (a comment, not a script)
            public sealed class Mover : Script
            {
                public float Speed = 2.5f;
                public int Count = 3;
                public bool Loop = true;
                public string Label = "hi; there";
                public Vector3 Offset = new Vector3(1, 2, 3);
                public Node Target;
                public static float Shared = 9;
                public readonly float Fixed = 1;
                float _private = 4;

                public override void Update(in Trinity.FrameTime time)
                {
                    var local = 1;
                }
            }

            class Helper { public float NotAScript = 1; }
            """);
        var index = new ScriptIndex(_dir);
        index.Refresh(0, force: true);

        var mover = Assert.Single(index.All);
        Assert.Equal("Mover", mover.Name);
        Assert.Equal(4, mover.Line);
        Assert.Equal(["Speed", "Count", "Loop", "Label", "Offset", "Target"], mover.Fields.Select(f => f.Name));
        Assert.Equal("Node", mover.Fields[5].Type);
        Assert.Equal(2.5f, mover.Fields[0].Default);
        Assert.Equal(3, mover.Fields[1].Default);
        Assert.Equal(true, mover.Fields[2].Default);
        Assert.Equal("hi; there", mover.Fields[3].Default);
        Assert.Equal(new Vector3(1, 2, 3), mover.Fields[4].Default);
    }

    [Fact]
    public void Lexer_carries_block_comments_and_keeps_strings_whole()
    {
        var tokens = new List<Token>();
        var state = CSharpLexer.Line("var a = 1; /* start", LexState.Normal, tokens);
        Assert.Equal(LexState.BlockComment, state);
        tokens.Clear();
        state = CSharpLexer.Line("end */ var url = \"http://x\"; // done", state, tokens);
        Assert.Equal(LexState.Normal, state);
        Assert.Equal(TokenKind.Comment, tokens[0].Kind);
        Assert.Contains(tokens, t => t.Kind == TokenKind.String && t.Length == "\"http://x\"".Length);
        Assert.Equal(TokenKind.Comment, tokens[^1].Kind);
    }

    [Fact]
    public void Checker_reads_compiler_and_runtime_errors_in_the_project_only()
    {
        File.WriteAllText(Path.Combine(_dir, "trinity.json"), """{ "name": "Test" }""");
        var checker = new ScriptChecker(EditorProject.Load(_dir));
        var file = Path.Combine(_dir, "Scripts", "Spin.cs");

        checker.Feed($"{file}(13,46): error CS1061: 'Node' does not contain a definition for 'Positon' [{_dir}/Game.csproj]");
        checker.Feed("/elsewhere/Engine.cs(1,1): warning CS0168: not ours");
        checker.Feed($"   0.080 error script: Spinner (Spin.Update): InvalidOperationException: test at Spin.Update(FrameTime& time) in {file}:line 11");

        var all = checker.All;
        Assert.Equal(2, all.Count);
        Assert.Equal(new Diagnostic(file, 12, 45, true, "CS1061", "'Node' does not contain a definition for 'Positon'"), all[0]);
        Assert.Equal((10, "runtime"), (all[1].Line, all[1].Code));
        Assert.Equal("Spinner (Spin.Update): InvalidOperationException: test", all[1].Message);

        checker.Feed("dotnet watch Hot reload succeeded.");
        Assert.Empty(checker.All);

        // After hot reload the stack has no line numbers: the error goes on the script's class
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "using Trinity.Scenes;\n\npublic sealed class Spin : Script { }\n");
        var index = new ScriptIndex(_dir);
        index.Refresh(0, force: true);
        var placed = new ScriptChecker(EditorProject.Load(_dir), index);
        placed.Feed("   3.193 error script: Spinner (Spin.Update): InvalidOperationException: test at Spin.Update(FrameTime& time)");
        Assert.Equal(new Diagnostic(file, 2, 0, true, "runtime", "Spinner (Spin.Update): InvalidOperationException: test"), Assert.Single(placed.All));
    }

    sealed class Counter : Script
    {
        public float Step = 1;
        public Vector3 Axis;
    }

    [Fact]
    public void Scripts_are_created_by_name_with_saved_field_values()
    {
        Scripts.Register("Counter", static () => new Counter(),
            ("Step", static (s, v) => ((Counter)s).Step = v.GetSingle()),
            ("Axis", static (s, v) => ((Counter)s).Axis = Scripts.ReadVector3(v)));
        var fields = new Dictionary<string, JsonElement>
        {
            ["Step"] = JsonDocument.Parse("2.5").RootElement.Clone(),
            ["Axis"] = JsonDocument.Parse("[0, 1, 0]").RootElement.Clone(),
            ["Gone"] = JsonDocument.Parse("1").RootElement.Clone(), // renamed field: warned about, skipped
        };
        var counter = Assert.IsType<Counter>(Scripts.Create("Counter", fields));
        Assert.Equal(2.5f, counter.Step);
        Assert.Equal(Vector3.UnitY, counter.Axis);
        Assert.Null(Scripts.Create("NoSuchScript", null));
    }

    [Fact]
    public void Scene_files_keep_scripts_and_field_values()
    {
        var library = new SceneLibrary(_dir);
        var world = new World();
        var spinner = world.Create("Spinner");
        world.SetPosition(spinner, new Vector3(1, 2, 3));
        world.Add(spinner, new MeshRenderer("Assets/a.glb"));
        world.Add(spinner, new ScriptComponent { Class = "Spin", Fields = new() { ["DegreesPerSecond"] = JsonDocument.Parse("45").RootElement.Clone() } });
        world.Create("Plain");
        library.Save("Scenes/test.scene.json", SceneLibrary.Write(world));

        Assert.DoesNotContain("null", File.ReadAllText(Path.Combine(_dir, "Scenes", "test.scene.json")));
        var (doc, problems) = library.Load("Scenes/test.scene.json");
        Assert.True(problems.Ok, problems.ToString());
        var again = new World();
        library.Instantiate(again, doc!);
        var loaded = again.All();
        Assert.Equal("Spin", again.Get<ScriptComponent>(loaded[0]).Class);
        Assert.Equal(45, again.Get<ScriptComponent>(loaded[0]).Fields!["DegreesPerSecond"].GetSingle());
        Assert.Equal(new Vector3(1, 2, 3), again.Position(loaded[0]));
        Assert.False(again.Has<ScriptComponent>(loaded[1]));
    }
}
