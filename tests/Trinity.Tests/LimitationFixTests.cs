using Trinity.Editor;
using Trinity.Scenes;
using Xunit;

namespace Trinity.Tests;

public sealed class LimitationFixTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("trinity-limits-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Find_reaches_nodes_whose_names_contain_a_slash()
    {
        var scene = new Scene(null!, new SceneLibrary(_dir));
        var house = scene.Create("House");
        var door = house.AddChild("Door 1/2");
        var handle = door.AddChild("Handle");
        var plain = house.AddChild("Door 1");
        plain.AddChild("2");
        Assert.Equal(door, scene.Find("House/Door 1/2")); // the whole name wins over "Door 1" > "2"
        Assert.Equal(handle, scene.Find("House/Door 1/2/Handle"));
        Assert.Equal(plain, scene.Find("House/Door 1"));
        Assert.Equal(handle, house.Find("Door 1/2/Handle"));
        Assert.Equal(house, scene.Find("/House/"));
        Assert.False(scene.Find("House/Door 3").IsValid);
        Assert.False(scene.Find("").IsValid);

        var slashRoot = scene.Create("a/b");
        Assert.Equal(slashRoot, scene.Find("a/b"));
    }

    static string EngineRoot()
    {
        for (var d = AppContext.BaseDirectory; d != null; d = Path.GetDirectoryName(d.TrimEnd('/')))
            if (ProjectTemplate.IsEngineRoot(d))
                return d.TrimEnd('/');
        throw new InvalidOperationException("tests run inside the Trinity repository");
    }

    [Fact]
    public void A_project_whose_engine_moved_is_pointed_at_the_engine_again()
    {
        var project = Path.Combine(_dir, "Game");
        ProjectTemplate.Create(project, "Game", EngineRoot());
        var props = Path.Combine(project, "Directory.Build.props");
        Assert.Contains("TRINITY_ROOT", File.ReadAllText(props));
        Assert.Null(ProjectTemplate.RepairEngine(project, EngineRoot())); // still right: left alone

        // Trinity "moved": the written path leads nowhere
        File.WriteAllText(props, File.ReadAllText(props).Replace(EngineRoot() + "/", "/nowhere/trinity/"));
        Assert.Equal("/nowhere/trinity", ProjectTemplate.RepairEngine(project, EngineRoot()));
        Assert.Contains(EngineRoot() + "/</TrinityRoot>", File.ReadAllText(props));

        // The first version of the template had no TRINITY_ROOT override: repaired into the current one
        File.WriteAllText(props, "<Project>\n  <PropertyGroup>\n    <TrinityRoot>/old/place/</TrinityRoot>\n  </PropertyGroup>\n  <Import Project=\"$(TrinityRoot)Directory.Build.props\" />\n</Project>\n");
        Assert.Equal("/old/place", ProjectTemplate.RepairEngine(project, EngineRoot()));
        Assert.Contains("TRINITY_ROOT", File.ReadAllText(props));
    }
}
