using System.Collections;
using System.Diagnostics;
using Brutal.ImGuiApi;
using Trinity.Editor;
using Xunit;

namespace Trinity.Tests;

/// <summary>Tests that run the editor's ImGui code headless or change PATH: one at a time, after the others.</summary>
[CollectionDefinition(nameof(HeadlessUi), DisableParallelization = true)]
public sealed class HeadlessUi;

/// <summary>
/// The FileSystem dock's logic: natural sort, sizes, paste names, create, image previews. Then the bugs
/// review-20261018-134036 confirmed in PR 17, each driven through the dock's own code (see HeadlessDock);
/// each test fails while its bug is there.
/// </summary>
[Collection(nameof(HeadlessUi))]
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
    public void Sizes_read_short(long bytes, string expected) => Assert.Equal(expected, EditorApp.FormatSize(bytes));

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
    public void Create_makes_folders_scenes_and_files()
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

    // --- Bugs from review-20261018-134036 --------------------------------------------------------------

    /// <summary>A precondition the test needs; failing it means the setup is wrong, not the dock.</summary>
    static void Setup(bool ok, string what)
    {
        if (!ok)
            throw new InvalidOperationException("SETUP, not the bug under test: " + what);
    }

    [Fact]
    public void Tiles_draw_files_that_arent_images_with_their_icon()
    {
        // Control for the next one: the headless tiles draw (no thumbnail involved)
        using var dock = new HeadlessDock(_dir);
        File.WriteAllText(dock.File("notes.md"), "");
        var entries = dock.Entries();
        dock.Frames(2, () => dock.Call("Tiles", entries, 0, false));
    }

    [Fact]
    public void Tiles_draw_an_image_that_cant_be_decoded()
    {
        // Bug 1: Add > New "x.png" makes an empty file; once its decode fails, Tiles reads thumb.Texture!.Width
        using var dock = new HeadlessDock(_dir);
        File.WriteAllText(dock.File("x.png"), "");
        var entries = dock.Entries();
        var thrown = Record.Exception(() =>
        {
            dock.Frame(() => dock.Call("Tiles", entries, 0, false)); // starts the decode
            dock.WaitForDecodes();
            dock.Frames(2, () => dock.Call("Tiles", entries, 0, false));
        });
        Assert.True(thrown == null, $"drawing the tiles threw {thrown}");
    }

    [Theory]
    [InlineData(95)] // control: the cache has room
    [InlineData(96)] // bug 2: the cache is full
    public void A_new_thumbnail_stays_in_the_cache(int alreadyCached)
    {
        using var dock = new HeadlessDock(_dir);
        File.WriteAllText(dock.File("new.png"), "");
        var entry = dock.Entries()[0]!;
        dock.Frames(120); // two seconds in
        // Images the tiles showed a second ago (their decodes failed, so nothing waits on the GPU)
        var images = dock.Get<IDictionary>("_images");
        var type = typeof(EditorApp).GetNestedType("ImagePreviewEntry", System.Reflection.BindingFlags.NonPublic)!;
        for (var i = 0; i < alreadyCached; i++)
        {
            var img = Activator.CreateInstance(type)!;
            type.GetField("Error")!.SetValue(img, "not an image this can read");
            type.GetField("Used")!.SetValue(img, 1.0);
            images[$"old{i}.png@160"] = img;
        }
        Setup(ImGui.GetTime() > 1.5, $"ImGui's clock should be past the old images' 1.0 s, is {ImGui.GetTime()}");

        dock.Frame(() => dock.Call("Thumbnail", entry, 160));
        Assert.True(images.Contains("new.png@160"), $"new.png was evicted as soon as it was added ({images.Count} cached)");
        Assert.Equal(Math.Min(alreadyCached + 1, 96), images.Count);
    }

    [Theory]
    [InlineData("")] // control: Shift-click over the whole folder
    [InlineData("item0")] // bug 3: after a search leaves fewer items than the anchor's index
    public void Shift_click_selects_a_range_of_what_shows(string search)
    {
        using var dock = new HeadlessDock(_dir);
        for (var i = 0; i < 40; i++)
            File.WriteAllText(dock.File($"item{i:00}.txt"), "");
        var all = dock.Entries();
        Setup(all.Count == 41 && HeadlessDock.PathOf(all[30]!) == "item30.txt", "the 40 items in order, then trinity.json");
        dock.Call("Clicked", all, 30, 1); // click item 30
        dock.Hold(ImGuiKey.Mod_Ctrl, true);
        dock.Call("Clicked", all, 31, 1); // Ctrl+click item 31: the anchor is 31
        dock.Hold(ImGuiKey.Mod_Ctrl, false);

        dock.Search(search);
        var shown = dock.Entries();
        Setup(search == "" ? shown.Count == 41 : shown.Count == 10, $"the search shows {shown.Count}");
        dock.Hold(ImGuiKey.Mod_Shift, true);
        var thrown = Record.Exception(() => dock.Call("Clicked", shown, 1, 1)); // Shift+click the second item
        Assert.True(thrown == null, $"Shift+click threw {thrown?.GetType().Name}: {thrown?.Message}");
        var paths = shown.Cast<object>().Select(HeadlessDock.PathOf).ToHashSet();
        Assert.All(dock.Selected, p => Assert.Contains(p, paths));
        Assert.Contains("item01.txt", dock.Selected);
    }

    [Fact]
    public void F2_renames_the_current_item()
    {
        // Control for the rename and new-item tests: the headless name box takes typing and Enter
        using var dock = new HeadlessDock(_dir);
        foreach (var n in new[] { "a.txt", "b.txt", "c.txt" })
            File.WriteAllText(dock.File(n), n);
        dock.Call("Clicked", dock.Entries(), 1, 1);
        dock.Press(ImGuiKey.F2, () => dock.Call("FsKeys", dock.Entries()));
        dock.TypeInNameBox("d");
        Assert.False(File.Exists(dock.File("b.txt")));
        Assert.Equal("b.txt", File.ReadAllText(dock.File("d.txt")));
    }

    [Fact]
    public void Rename_renames_the_item_it_was_opened_on_or_nothing()
    {
        // Bug 5: the box doesn't record its target; on Enter it renames entries[CursorIn], and when the
        // current item has gone (deleted outside, or the relist dropped it) CursorIn falls back to the first
        using var dock = new HeadlessDock(_dir);
        foreach (var n in new[] { "a.txt", "b.txt", "c.txt" })
            File.WriteAllText(dock.File(n), n);
        dock.Call("Clicked", dock.Entries(), 2, 1); // c.txt
        dock.Press(ImGuiKey.F2, () => dock.Call("FsKeys", dock.Entries()));
        File.Delete(dock.File("c.txt"));
        dock.Call("FsRefresh");
        dock.TypeInNameBox("renamed");
        Assert.True(File.Exists(dock.File("a.txt")), "a.txt, which nobody renamed, was renamed to " +
            string.Join(", ", Directory.EnumerateFiles(dock.Root, "renamed*").Select(Path.GetFileName)));
    }

    [Theory]
    [InlineData("New folder/", "Textures/", "Textures")] // control: the trailing / kept
    [InlineData("New folder/", "Textures", "Textures")] // bug 7: typing over the selected text drops the /
    [InlineData("new.scene.json", "level.scene.json", "level.scene.json")] // control
    [InlineData("new.scene.json", "level", "level.scene.json")] // bug 7: and the .scene.json
    public void New_folder_and_new_scene_make_what_they_say(string initial, string typed, string made)
    {
        using var dock = new HeadlessDock(_dir);
        dock.Call("StartInput", HeadlessDock.Enum("FsInput", "Create"), initial); // what Add > New folder / New scene do
        dock.TypeInNameBox(typed);
        if (initial.EndsWith('/'))
            Assert.True(Directory.Exists(dock.File(made)), $"no folder {made}; made a file: {File.Exists(dock.File(typed))}");
        else
        {
            Assert.True(File.Exists(dock.File(made)), $"no scene {made}; made a file {typed}: {File.Exists(dock.File(typed))}");
            var (doc, problems) = new Trinity.Scenes.SceneLibrary(dock.Root).Load(made);
            Assert.True(problems.Ok && doc != null, problems.ToString());
        }
    }

    [Theory]
    [InlineData("sub/x")] // bug 11: moves it into a new subfolder
    [InlineData("../outside")] // bug 11: moves it out of the project
    public void Rename_refuses_names_with_folders_in_them(string typed)
    {
        using var dock = new HeadlessDock(_dir);
        File.WriteAllText(dock.File("a.txt"), "a");
        dock.Press(ImGuiKey.F2, () => dock.Call("FsKeys", dock.Entries()));
        dock.TypeInNameBox(typed);
        var parent = Path.GetDirectoryName(dock.Root)!;
        Assert.True(File.Exists(dock.File("a.txt")), "a.txt was moved to " + string.Join(", ",
            Directory.EnumerateFiles(parent, "*.txt", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(dock.Root, f))));
    }

    [Theory]
    [InlineData(false)] // control: a scene file that isn't valid JSON shows its problem
    [InlineData(true)] // bug 14: one without read permission throws UnauthorizedAccessException
    public void The_scene_preview_shows_a_scene_it_cant_read(bool locked)
    {
        using var dock = new HeadlessDock(_dir);
        var file = dock.File("level.scene.json");
        File.WriteAllText(file, locked ? "{}" : "not json");
        if (locked)
        {
            File.SetUnixFileMode(file, UnixFileMode.None);
            Setup(Record.Exception(() => File.ReadAllText(file)) is UnauthorizedAccessException, "a file with mode 000 can't be read (not root)");
        }
        try
        {
            var entry = dock.Entries()[0];
            var thrown = Record.Exception(() => dock.Frame(() => dock.Call("ScenePreview", entry)));
            Assert.True(thrown == null, $"the preview threw {thrown?.GetType().Name}: {thrown?.Message}");
        }
        finally
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void Sorting_by_a_column_title_is_remembered()
    {
        // Bug 19: the list's column titles change the sort without SaveView
        using var dock = new HeadlessDock(_dir);
        File.WriteAllText(dock.File("a.txt"), "");
        File.WriteAllText(dock.File("b.txt"), "bb");
        var entries = dock.Entries();
        void Rows() => dock.Call("ListRows", entries, 0, false);
        dock.Frame(Rows);
        // The "Size" title: at the list's width - 230 (the window is 1000 wide with 8 of padding), first line
        var width = 1000 - 2 * ImGui.GetStyle().WindowPadding.X;
        dock.Click(width - 230 + 12, ImGui.GetStyle().WindowPadding.Y + 6, Rows);
        Setup(dock.Get<object>("_fsSort").ToString() == "Size", $"the click lands on Size (sort is {dock.Get<object>("_fsSort")})");
        Assert.True(File.Exists(dock.SettingsFile) && File.ReadAllText(dock.SettingsFile).Contains("\"Size\""),
            "the sort isn't saved: " + (File.Exists(dock.SettingsFile) ? File.ReadAllText(dock.SettingsFile) : "no settings file"));
    }

    [Theory]
    [InlineData(".gitignore", false, ".gitignore")] // bug 21: gives _1.gitignore
    [InlineData(".env", false, ".env")]
    [InlineData("v1.2", true, "v1.2")] // bug 21: a folder gives v1_1.2
    [InlineData("rock.glb", false, "rock_")] // control
    public void Pasting_over_a_taken_dotfile_or_dotted_folder_keeps_its_name_in_front(string name, bool folder, string start)
    {
        if (folder)
            Directory.CreateDirectory(Path.Combine(_dir, name));
        else
            File.WriteAllText(Path.Combine(_dir, name), "");
        var free = ProjectFiles.FreeName(_dir, "", name);
        Assert.NotEqual(name, free);
        Assert.StartsWith(start, free);
    }

    [Fact]
    public void Opening_a_file_without_xdg_open_installed_doesnt_throw()
    {
        // Bug 10: Enter, double-click or "Open with the desktop's app" on an "other" file runs xdg-open
        // inside the frame; without xdg-utils, Process.Start throws Win32Exception out of DrawUi.
        // PATH points at an empty folder for this test, so nothing is ever opened on the desktop.
        using var dock = new HeadlessDock(_dir);
        File.WriteAllText(dock.File("notes.xyz"), "");
        var empty = Directory.CreateDirectory(Path.Combine(_dir, "no-programs")).FullName;
        var path = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", empty);
        try
        {
            Setup(Record.Exception(() => Process.Start(new ProcessStartInfo("xdg-open") { UseShellExecute = false })) is System.ComponentModel.Win32Exception,
                "with PATH empty, xdg-open isn't found");
            var thrown = Record.Exception(() => dock.Call("ActivateFile", "notes.xyz"));
            Assert.True(thrown == null, $"opening it threw {thrown?.GetType().Name}: {thrown?.Message}");
            dock.RunAfterTree(); // if the fix queues it there, Safely logs the failure instead
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", path);
        }
    }
}
