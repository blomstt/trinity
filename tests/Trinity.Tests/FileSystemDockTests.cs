using Trinity.Editor;
using Xunit;

namespace Trinity.Tests;

/// <summary>The FileSystem dock's logic: yazi's natural sort, sizes, paste names, create, image previews.</summary>
public sealed class FileSystemDockTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("trinity-fs-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Natural_sort_orders_numbers_by_value_and_is_case_sensitive()
    {
        string[] names = ["rock 10.glb", "rock 2.glb", "Rock.glb", "rock 1.glb", "rock 02b.glb", "a", "B"];
        var sorted = names.OrderBy(n => n, Comparer<string>.Create(EditorApp.Natural)).ToArray();
        Assert.Equal(["B", "Rock.glb", "a", "rock 1.glb", "rock 2.glb", "rock 02b.glb", "rock 10.glb"], sorted);
    }

    [Theory]
    [InlineData(0, "0B")]
    [InlineData(1023, "1023B")]
    [InlineData(2770, "2.7K")]
    [InlineData(1_300_000, "1.2M")]
    [InlineData(5L * 1024 * 1024 * 1024, "5G")]
    public void Sizes_read_as_in_yazi(long bytes, string expected) => Assert.Equal(expected, EditorApp.FormatSize(bytes));

    [Fact]
    public void Pasting_over_a_taken_name_adds_a_number_before_the_extension()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "Assets"));
        Assert.Equal("Assets/rock.glb", ProjectFiles.FreeName(_dir, "Assets", "rock.glb"));
        File.WriteAllText(Path.Combine(_dir, "Assets/rock.glb"), "");
        Assert.Equal("Assets/rock_1.glb", ProjectFiles.FreeName(_dir, "Assets", "rock.glb"));
        File.WriteAllText(Path.Combine(_dir, "Assets/rock_1.glb"), "");
        Assert.Equal("Assets/rock_2.glb", ProjectFiles.FreeName(_dir, "Assets", "rock.glb"));
        File.WriteAllText(Path.Combine(_dir, "level.scene.json"), "");
        Assert.Equal("level_1.scene.json", ProjectFiles.FreeName(_dir, "", "level.scene.json"));
    }

    [Fact]
    public void Create_makes_folders_scenes_and_files_as_yazi_does()
    {
        Assert.Equal("models/rocks", ProjectFiles.Create(_dir, "", "models/rocks/"));
        Assert.True(Directory.Exists(Path.Combine(_dir, "models/rocks")));
        Assert.Equal("models/level.scene.json", ProjectFiles.Create(_dir, "models", "level.scene.json"));
        var (doc, problems) = new Trinity.Scenes.SceneLibrary(_dir).Load("models/level.scene.json");
        Assert.True(problems.Ok && doc != null, problems.ToString());
        Assert.Equal("notes/todo.md", ProjectFiles.Create(_dir, "notes", "todo.md"));
        Assert.Equal("", File.ReadAllText(Path.Combine(_dir, "notes/todo.md")));
        Assert.Throws<IOException>(() => ProjectFiles.Create(_dir, "notes", "todo.md"));
        Assert.Throws<IOException>(() => ProjectFiles.Create(_dir, "", "../outside"));
    }

    [Fact]
    public void Image_previews_are_shrunk_to_fit()
    {
        var root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "samples")))
            root = Path.GetDirectoryName(root.TrimEnd('/'))!;
        var image = Directory.EnumerateFiles(Path.Combine(root, "samples/Sandbox/Assets"), "*.jpg", SearchOption.AllDirectories).First();
        var (w, h, rgba, sw, sh) = EditorApp.DecodeSmall(image, 256);
        Assert.True(sw > 256 && Math.Max(w, h) <= 256, $"{sw}x{sh} -> {w}x{h}");
        Assert.Equal(w * h * 4, rgba.Length);
    }
}
