using System.Diagnostics;
using System.Numerics;
using Trinity.Scenes;
using Xunit;
using Xunit.Abstractions;

namespace Trinity.Tests;

/// <summary>The per-frame scene work allocates nothing once warmed up, and stays cheap at crowd scale.</summary>
[Collection("scripts")]
public class PerformanceTests(ITestOutputHelper output)
{
    static World Crowd(int count, out List<EntityId> movers)
    {
        var w = new World();
        movers = [];
        var groups = new EntityId[50];
        for (var g = 0; g < groups.Length; g++)
            groups[g] = w.Create($"Group {g}");
        for (var i = 0; i < count; i++)
        {
            var e = w.Create("Unit", groups[i % groups.Length]);
            w.SetPosition(e, new Vector3(i % 100, 0, i / 100));
            w.Add(e, new MeshRenderer("Assets/character.glb"));
            w.Add(e, new Animator("walk"));
            movers.Add(e);
        }
        return w;
    }

    [Fact]
    public void Steady_state_frames_allocate_nothing()
    {
        var w = Crowd(5000, out var movers);
        var draws = new List<SceneRenderer.SceneDraw>(8192);
        SceneRenderer.Collect(w, draws); // warm up
        var group = w.Parent(movers[0]);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = 0; frame < 10; frame++)
        {
            foreach (var e in movers)
                w.SetPosition(e, w.Position(e) + new Vector3(0.01f, 0, 0));
            w.SetPosition(group, new Vector3(frame, 0, 0)); // a moving parent dirties its subtree
            SceneRenderer.Collect(w, draws);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(5000, draws.Count);
    }

    [Fact]
    public void Crowd_frame_costs()
    {
        var w = Crowd(5000, out var movers);
        var draws = new List<SceneRenderer.SceneDraw>(8192);
        SceneRenderer.Collect(w, draws);
        var clock = Stopwatch.StartNew();
        const int frames = 200;
        for (var frame = 0; frame < frames; frame++)
        {
            foreach (var e in movers)
                w.SetPosition(e, w.Position(e) + new Vector3(0.01f, 0, 0));
            SceneRenderer.Collect(w, draws);
        }
        var moveAndCollect = clock.Elapsed.TotalMilliseconds / frames;
        clock.Restart();
        for (var frame = 0; frame < frames; frame++)
            SceneRenderer.Collect(w, draws);
        var collectOnly = clock.Elapsed.TotalMilliseconds / frames;
        output.WriteLine($"5,000 nodes: move all + collect {moveAndCollect:F3} ms, collect (nothing moved) {collectOnly:F3} ms");
        Assert.True(moveAndCollect < 5, $"{moveAndCollect} ms"); // generous: a regression guard, not a benchmark
    }

    [Fact]
    public void Structural_undo_snapshots_stay_quick_at_crowd_scale()
    {
        var dir = Directory.CreateTempSubdirectory("trinity-perf-").FullName;
        try
        {
            var scene = Trinity.Editor.EditorScene.New(new SceneLibrary(dir), "Scenes/big.scene.json");
            for (var i = 0; i < 5000; i++)
                scene.World.Add(scene.World.Create($"Unit {i}"), new MeshRenderer("Assets/character.glb"));
            scene.CreateNode("warm-up");
            var clock = Stopwatch.StartNew();
            scene.CreateNode("Timed"); // snapshot before and after
            var create = clock.Elapsed.TotalMilliseconds;
            clock.Restart();
            scene.Undo(); // rebuild the world from the snapshot
            var undo = clock.Elapsed.TotalMilliseconds;
            output.WriteLine($"5,000 nodes: structural edit {create:F1} ms, undo {undo:F1} ms, snapshot {scene.Snapshot().Length / 1024} KB");
            Assert.Equal(5001, scene.World.Count);
            Assert.True(create + undo < 1000, $"{create} + {undo} ms");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
