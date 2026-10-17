using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Trinity.Graphics;
using Trinity.Scenes;

namespace Trinity.Editor;

/// <summary>A project in the Project Manager's list (saved in the editor's settings).</summary>
sealed class ProjectEntry
{
    public string Path { get; set; } = "";
    public bool Favorite { get; set; }
    public DateTime LastOpened { get; set; }
}

/// <summary>
/// The Project Manager: the window Trinity Editor opens with when it's started without a
/// project. Lists the projects it knows, with favourites first; creates new ones from a template,
/// imports or scans for existing ones, renames, duplicates, tags and removes them, and runs a game
/// without opening the editor. Edit (or a double-click) opens the project in the editor.
/// </summary>
sealed class ProjectManager : IDisposable
{
    readonly Window _window;
    readonly GraphicsDevice _gpu;
    readonly ImGuiHost _ui;
    readonly Settings _settings;
    List<ProjectEntry> _entries;
    readonly Dictionary<string, (EditorProject? Project, DateTime Read)> _info = [];

    string? _selected;
    EditorProject? _result;
    readonly ImInputString _filter = new(128), _nameEdit = new(128), _locationEdit = new(1024), _tagEdit = new(64);
    int _sort; // 0 last edited, 1 name, 2 path
    static readonly string[] SortNames = ["Last Edited", "Name", "Path"];
    readonly FolderPicker _picker = new();
    string? _modal, _openModal; // which dialog is showing
    bool _createFolder = true;
    string _error = "";

    /// <summary>For checking without a person: save the window to a PNG after a while, then quit.</summary>
    public (string Path, double After)? Screenshot;

    public ProjectManager(Settings settings, bool splash)
    {
        _settings = settings;
        _entries = settings.Get("projects", EditorJson.Default.ListProjectEntry);
        // Editors before the Project Manager only remembered the last project: start the list with it
        if (_entries.Count == 0 && settings.Get("recent", EditorJson.Default.DictionaryStringString).GetValueOrDefault("Last") is { } last
            && File.Exists(EditorProject.FileIn(last)))
        {
            _entries.Add(new ProjectEntry { Path = Path.GetFullPath(last).TrimEnd('/'), LastOpened = DateTime.UtcNow });
            Save();
        }
        _window = new Window("Trinity - Project Manager", 1180, 720, appId: "trinity-editor");
        _gpu = new GraphicsDevice(_window, vsync: true);
        var config = Path.Combine(
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
            "trinity-editor"
        );
        _ui = new ImGuiHost(_window, _gpu, config, "project-manager.ini");
        if (splash && Environment.GetEnvironmentVariable("TRINITY_SKIP_SPLASH") != "1")
        {
            var shaders = new ShaderLibrary(_gpu);
            Splash.Show(_window, _gpu, shaders);
        }
        _selected = _entries.OrderByDescending(e => e.LastOpened).FirstOrDefault()?.Path;
    }

    /// <summary>Shows the list until a project is chosen for editing (returned) or the window is closed (null).</summary>
    public EditorProject? Run()
    {
        var clock = Stopwatch.StartNew();
        double lastActivity = 0, lastFrame = 0;
        _window.Activity += () => lastActivity = clock.Elapsed.TotalSeconds;
        while (!_window.ShouldClose && _result == null)
        {
            // Redraw only after input (or for a screenshot); otherwise sleep until something happens
            Window.PollEvents();
            var t = clock.Elapsed.TotalSeconds;
            if (t - lastActivity > 0.5 && Screenshot == null && t - lastFrame < 2)
            {
                Window.WaitEvents(2 - (t - lastFrame));
                continue;
            }
            lastFrame = t;
            if (!_gpu.BeginFrame(out var cmd))
                continue;
            _ui.NewFrame();
            Draw();
            _ui.Render(cmd);
            var shoot = Screenshot is { } shot && t >= shot.After;
            // Sized now, not at start: the window manager may have resized the window since
            using var capture = shoot ? Screenshots.Buffer(_gpu) : null;
            _gpu.EndFrame(cmd, capture, endRendering: false);
            if (shoot)
            {
                Screenshots.Save(_gpu, capture!, Screenshot!.Value.Path);
                break;
            }
        }
        _gpu.WaitIdle();
        return _result;
    }

    // --- Projects ---------------------------------------------------------------------------------------

    EditorProject? Info(string path)
    {
        var file = EditorProject.FileIn(path);
        var stamp = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : default;
        if (_info.TryGetValue(path, out var cached) && cached.Read == stamp)
            return cached.Project;
        EditorProject? project = null;
        try
        {
            if (stamp != default)
                project = EditorProject.Load(path);
        }
        catch (Exception e)
        {
            Log.Warn("projects", $"{file}: {e.Message}");
        }
        _info[path] = (project, stamp);
        return project;
    }

    void Save()
    {
        _settings.Set("projects", _entries, EditorJson.Default.ListProjectEntry);
        _settings.Save();
    }

    /// <summary>Adds a project folder to the list (once), selecting it.</summary>
    public void Add(string folder)
    {
        folder = Path.GetFullPath(folder).TrimEnd('/');
        if (_entries.All(e => e.Path != folder))
            _entries.Add(new ProjectEntry { Path = folder });
        _selected = folder;
        Save();
    }

    /// <summary>Marks a project as just opened (for sorting), adding it if it's new.</summary>
    public static void Opened(Settings settings, string folder)
    {
        var entries = settings.Get("projects", EditorJson.Default.ListProjectEntry);
        folder = Path.GetFullPath(folder).TrimEnd('/');
        var entry = entries.FirstOrDefault(e => e.Path == folder);
        if (entry == null)
            entries.Add(entry = new ProjectEntry { Path = folder });
        entry.LastOpened = DateTime.UtcNow;
        settings.Set("projects", entries, EditorJson.Default.ListProjectEntry);
        settings.Save();
    }

    DateTime Edited(ProjectEntry e)
    {
        var file = EditorProject.FileIn(e.Path);
        var modified = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : default;
        return e.LastOpened > modified ? e.LastOpened : modified;
    }

    IEnumerable<ProjectEntry> Visible()
    {
        var filter = _filter.ToString().Trim();
        var tag = filter.StartsWith("tag:", StringComparison.OrdinalIgnoreCase) ? filter[4..] : null;
        var list = _entries.Where(e =>
        {
            if (filter.Length == 0)
                return true;
            var p = Info(e.Path);
            return tag != null
                ? p?.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase) == true
                : (p?.Name ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase) || e.Path.Contains(filter, StringComparison.OrdinalIgnoreCase);
        });
        IOrderedEnumerable<ProjectEntry> favouritesFirst = list.OrderByDescending(e => e.Favorite);
        return _sort switch
        {
            1 => favouritesFirst.ThenBy(e => Info(e.Path)?.Name ?? Path.GetFileName(e.Path), StringComparer.OrdinalIgnoreCase),
            2 => favouritesFirst.ThenBy(e => e.Path, StringComparer.Ordinal),
            _ => favouritesFirst.ThenByDescending(Edited),
        };
    }

    // --- Drawing ----------------------------------------------------------------------------------------

    void Draw()
    {
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.WorkPos);
        ImGui.SetNextWindowSize(viewport.WorkSize);
        ImGui.Begin("##projects", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBringToFrontOnFocus);

        // Title, and the one tab
        ImGui.TextColored(ImGuiHost.Color(0.88f, 0.66f, 0.36f), "TRINITY");
        ImGui.SameLine(ImGui.GetWindowWidth() / 2 - 40);
        Icons.Inline(Icon.Folder, Icons.FolderColor);
        ImGui.SameLine();
        ImGui.Text("Projects");
        ImGui.SameLine(ImGui.GetWindowWidth() - 110);
        if (Icons.TextButton("Settings", Icon.Edit, 100))
            OpenModal("settings");
        ImGui.Separator();

        if (Icons.TextButton("Create", Icon.Plus))
        {
            _nameEdit.SetValue("New Game".AsSpan());
            _locationEdit.SetValue(DefaultLocation().AsSpan());
            _createFolder = true;
            OpenModal("create");
        }
        ImGui.SameLine();
        if (Icons.TextButton("Import", Icon.Import))
        {
            _picker.Start("Import a project: pick its folder (with trinity.json)", DefaultLocation(), wantProject: true);
            OpenModal("import");
        }
        ImGui.SameLine();
        if (Icons.TextButton("Scan", Icon.Scan))
        {
            _picker.Start("Scan a folder for projects", DefaultLocation(), wantProject: false);
            OpenModal("scan");
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 260);
        ImGui.InputTextWithHint("##filter", "Filter projects (or tag:name)", _filter);
        ImGui.SameLine();
        ImGui.Text("Sort:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##sort", SortNames[_sort]))
        {
            for (var i = 0; i < SortNames.Length; i++)
                if (ImGui.Selectable(SortNames[i], i == _sort))
                    _sort = i;
            ImGui.EndCombo();
        }

        const float sideWidth = 160;
        var avail = ImGui.GetContentRegionAvail();
        ImGui.BeginChild("list", new float2(avail.X - sideWidth - 8, avail.Y - ImGui.GetTextLineHeightWithSpacing()), ImGuiChildFlags.Borders);
        var visible = Visible().ToList();
        if (_entries.Count == 0)
            ImGui.TextDisabled("No projects yet. Create one, or Import or Scan for existing ones (folders with a trinity.json).");
        foreach (var e in visible)
            Row(e);
        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("actions", new float2(sideWidth, avail.Y - ImGui.GetTextLineHeightWithSpacing()));
        SideButtons();
        ImGui.EndChild();
        var version = $"Trinity {typeof(ProjectManager).Assembly.GetName().Version?.ToString(3)}";
        ImGui.SetCursorPosX(ImGui.GetWindowWidth() - ImGui.CalcTextSize(version).X - 12);
        ImGui.TextDisabled(version);

        Shortcuts();
        Modals();
        ImGui.End();
    }

    void Row(ProjectEntry e)
    {
        var project = Info(e.Path);
        var missing = project == null;
        var height = ImGui.GetTextLineHeight() * 2 + ImGui.GetStyle().ItemSpacing.Y * 3;
        ImGui.PushID(e.Path);
        var top = ImGui.GetCursorScreenPos();
        if (ImGui.Selectable("##row", _selected == e.Path, ImGuiSelectableFlags.AllowOverlap | ImGuiSelectableFlags.AllowDoubleClick, new float2(0, height)))
        {
            _selected = e.Path;
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                Edit(e);
        }
        var bottom = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        var line = ImGui.GetTextLineHeight();

        // Favourite star
        ImGui.SetCursorScreenPos(new float2(top.X + 4, top.Y + (height - ImGui.GetFrameHeight()) / 2));
        if (Icons.Button("fav", e.Favorite ? Icon.Star : Icon.StarOutline, e.Favorite ? Icons.Gold : Icons.Dim, e.Favorite ? "Favourite: click to unpin" : "Pin to the top"))
        {
            e.Favorite = !e.Favorite;
            Save();
        }

        // A tile with the project's initials, coloured from its name
        var name = project?.Name ?? Path.GetFileName(e.Path);
        var tile = new float2(top.X + 40, top.Y + 4);
        var size = height - 8;
        dl.AddRectFilled(tile, new float2(tile.X + size, tile.Y + size), ImGui.GetColorU32(TileColor(name)), 6);
        var initials = Initials(name);
        var textSize = ImGui.CalcTextSize(initials);
        dl.AddText(new float2(tile.X + (size - textSize.X) / 2, tile.Y + (size - textSize.Y) / 2), ImGui.GetColorU32(ImGuiHost.Color(1, 1, 1)), initials);

        var x = tile.X + size + 14;
        dl.AddText(new float2(x, top.Y + 4), ImGui.GetColorU32(missing ? ImGuiHost.Color(0.93f, 0.40f, 0.36f) : ImGuiHost.Color(0.92f, 0.93f, 0.95f)), missing ? $"{name}  (missing)" : name);
        // Right side: tags, engine version, when it was last edited
        var right = ImGui.GetWindowPos().X + ImGui.GetWindowWidth() - ImGui.GetStyle().ScrollbarSize - 12;
        var when = Edited(e);
        var date = when == default ? "" : when.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        var dateWidth = ImGui.CalcTextSize(date).X;
        dl.AddText(new float2(right - dateWidth, top.Y + line + 8), ImGui.GetColorU32(Icons.Dim), date);
        var infoLeft = right - dateWidth;
        if (project?.Engine is { } engine)
        {
            infoLeft -= ImGui.CalcTextSize(engine).X + 24;
            dl.AddText(new float2(infoLeft, top.Y + line + 8), ImGui.GetColorU32(Icons.FileColor), engine);
        }

        // The path, shortened from the left if it would run into the date
        Icons.Draw(dl, Icon.Folder, new float2(x, top.Y + line + 10), line * 0.85f, Icons.Dim);
        dl.AddText(new float2(x + line + 4, top.Y + line + 8), ImGui.GetColorU32(Icons.Dim), FitLeft(e.Path, infoLeft - 24 - (x + line + 4)));
        if (project is { Tags.Length: > 0 })
        {
            var tx = right;
            foreach (var tag in project.Tags.Reverse())
            {
                var w = ImGui.CalcTextSize(tag).X + 12;
                tx -= w + 6;
                ImGui.SetCursorScreenPos(new float2(tx, top.Y + 3));
                ImGui.PushStyleColor(ImGuiCol.Button, TileColor(tag) with { W = 0.55f });
                if (ImGui.SmallButton(tag))
                    _filter.SetValue(("tag:" + tag).AsSpan());
                ImGui.PopStyleColor();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"Show projects tagged {tag}");
            }
        }
        ImGui.SetCursorScreenPos(bottom);
        ImGui.Dummy(new float2(0, 0)); // so the list knows its rows' extent
        ImGui.PopID();
    }

    /// <summary>Text cut from the start to fit a width ("...projects/My game"), so the end stays readable.</summary>
    static string FitLeft(string text, float width)
    {
        if (ImGui.CalcTextSize(text).X <= width)
            return text;
        for (var n = 1; n < text.Length; n++)
            if (ImGui.CalcTextSize("..." + text[n..]).X <= width)
                return "..." + text[n..];
        return "...";
    }

    static float4 TileColor(string name)
    {
        var hash = 0u;
        foreach (var ch in name)
            hash = hash * 31 + ch;
        var hue = hash % 360 / 360f;
        // A muted colour at that hue
        float Channel(float offset) => 0.35f + 0.3f * MathF.Cos(MathF.Tau * (hue + offset));
        return ImGuiHost.Color(Channel(0), Channel(1 / 3f), Channel(2 / 3f));
    }

    static string Initials(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length switch
        {
            0 => "?",
            1 => words[0][..Math.Min(2, words[0].Length)].ToUpperInvariant(),
            _ => string.Concat(words.Take(2).Select(w => char.ToUpperInvariant(w[0]))),
        };
    }

    void SideButtons()
    {
        var entry = _entries.FirstOrDefault(e => e.Path == _selected);
        var project = entry == null ? null : Info(entry.Path);
        var width = ImGui.GetContentRegionAvail().X;
        ImGui.BeginDisabled(project == null);
        if (Icons.TextButton("Edit", Icon.Edit, width)) Edit(entry!);
        if (Icons.TextButton("Run", Icon.Play, width)) RunGame(project!);
        if (Icons.TextButton("Rename", Icon.Rename, width))
        {
            _nameEdit.SetValue(project!.Name.AsSpan());
            OpenModal("rename");
        }
        if (Icons.TextButton("Duplicate", Icon.Duplicate, width))
        {
            _nameEdit.SetValue((project!.Name + " Copy").AsSpan());
            OpenModal("duplicate");
        }
        if (Icons.TextButton("Manage Tags", Icon.Tag, width))
        {
            _tagEdit.SetValue("".AsSpan());
            OpenModal("tags");
        }
        ImGui.EndDisabled();
        ImGui.BeginDisabled(entry == null);
        if (Icons.TextButton("Remove", Icon.Trash, width))
            OpenModal("remove");
        ImGui.EndDisabled();
        var missing = _entries.Count(e => Info(e.Path) == null);
        ImGui.BeginDisabled(missing == 0);
        if (Icons.TextButton("Remove Missing", Icon.Broom, width))
        {
            _entries.RemoveAll(e => Info(e.Path) == null);
            Save();
        }
        ImGui.EndDisabled();
        if (missing > 0 && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip($"{missing} project(s) whose folder is gone");
    }

    void Shortcuts()
    {
        if (_modal != null || ImGui.GetIO().WantTextInput)
            return;
        var entry = _entries.FirstOrDefault(e => e.Path == _selected);
        if (entry != null && ImGui.IsKeyPressed(ImGuiKey.Enter) && Info(entry.Path) != null)
            Edit(entry);
        if (entry != null && ImGui.IsKeyPressed(ImGuiKey.Delete))
            OpenModal("remove");
        if (ImGui.IsKeyPressed(ImGuiKey.DownArrow) || ImGui.IsKeyPressed(ImGuiKey.UpArrow))
        {
            var list = Visible().ToList();
            var i = list.FindIndex(e => e.Path == _selected);
            i = Math.Clamp(i + (ImGui.IsKeyPressed(ImGuiKey.DownArrow) ? 1 : -1), 0, list.Count - 1);
            if (list.Count > 0)
                _selected = list[i].Path;
        }
    }

    void Edit(ProjectEntry e)
    {
        if (Info(e.Path) is not { } project)
            return;
        e.LastOpened = DateTime.UtcNow;
        Save();
        _result = project;
    }

    void RunGame(EditorProject project)
    {
        if (!File.Exists(project.GamePath))
        {
            Log.Error("projects", $"no game project at {project.GamePath} (set \"game\" in trinity.json)");
            return;
        }
        Log.Info("projects", $"running {project.Name}");
        ProjectTemplate.CheckEngine(project.Root);
        var start = new ProcessStartInfo("dotnet", ["run", "--project", project.GamePath])
        {
            WorkingDirectory = project.Root,
            UseShellExecute = false,
        };
        ProjectTemplate.UseEngine(start);
        Process.Start(start)?.Dispose();
    }

    string DefaultLocation()
    {
        var last = _settings.Get("project-manager", EditorJson.Default.DictionaryStringString).GetValueOrDefault("location");
        if (last != null && Directory.Exists(last))
            return last;
        var projects = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Projects");
        return Directory.Exists(projects) ? projects : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    void Remember(string key, string value)
    {
        var section = _settings.Get("project-manager", EditorJson.Default.DictionaryStringString);
        section[key] = value;
        _settings.Set("project-manager", section, EditorJson.Default.DictionaryStringString);
        _settings.Save();
    }

    // --- Dialogs ----------------------------------------------------------------------------------------

    void OpenModal(string name)
    {
        _openModal = name;
        _error = "";
    }

    void Modals()
    {
        if (_openModal != null)
        {
            ImGui.OpenPopup(_openModal);
            _modal = _openModal;
            _openModal = null;
        }
        var centre = ImGui.GetMainViewport().GetCenter();
        foreach (var name in (ReadOnlySpan<string>)["create", "import", "scan", "rename", "duplicate", "tags", "remove", "settings"])
        {
            ImGui.SetNextWindowPos(centre, ImGuiCond.Appearing, new float2(0.5f, 0.5f));
            if (!ImGui.BeginPopupModal(name, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar))
                continue;
            var done = name switch
            {
                "create" => CreateDialog(),
                "import" => ImportDialog(),
                "scan" => ScanDialog(),
                "rename" => RenameDialog(),
                "duplicate" => DuplicateDialog(),
                "tags" => TagsDialog(),
                "remove" => RemoveDialog(),
                _ => SettingsDialog(),
            };
            if (_error.Length > 0)
                ImGui.TextColored(ImGuiHost.Color(0.93f, 0.40f, 0.36f), _error);
            if (done)
            {
                _modal = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
    }

    static bool Cancel() => ImGui.Button("Cancel", new float2(110, 0)) || ImGui.IsKeyPressed(ImGuiKey.Escape);

    bool CreateDialog()
    {
        ImGui.Text("Create a new project");
        ImGui.Separator();
        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(480);
        ImGui.InputText("Name", _nameEdit);
        ImGui.SetNextItemWidth(400);
        ImGui.InputText("##location", _locationEdit);
        ImGui.SameLine();
        if (ImGui.Button("Browse..."))
        {
            _picker.Start("Where the project goes", _locationEdit.ToString(), wantProject: false);
            ImGui.OpenPopup("browse-create");
        }
        ImGui.SameLine();
        ImGui.Text("Location");
        if (_picker.Draw("browse-create") is { } picked)
            _locationEdit.SetValue(picked.AsSpan());
        ImGui.Checkbox("Create a folder for it", ref _createFolder);
        var name = _nameEdit.ToString().Trim();
        var folder = _createFolder ? Path.Combine(_locationEdit.ToString(), ProjectTemplate.FolderName(name)) : _locationEdit.ToString();
        ImGui.TextDisabled($"Project folder: {folder}");
        var engine = ProjectTemplate.EngineRoot(_settings);
        if (engine == null)
            ImGui.TextColored(ImGuiHost.Color(0.95f, 0.72f, 0.33f), "Trinity's source folder isn't known: set it in Settings, so the game can reference the engine");
        else
            ImGui.TextDisabled($"Engine: {engine}");
        var problem = name.Length == 0 ? "Give it a name"
            : Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any() ? "That folder isn't empty"
            : engine == null ? "Set Trinity's folder in Settings first"
            : null;
        ImGui.Separator();
        ImGui.BeginDisabled(problem != null);
        if (ImGui.Button("Create & Edit", new float2(140, 0)))
        {
            try
            {
                ProjectTemplate.Create(folder, name, engine!);
                Remember("location", _locationEdit.ToString());
                Add(folder);
                Edit(_entries.First(e => e.Path == Path.GetFullPath(folder).TrimEnd('/')));
                return true;
            }
            catch (Exception e)
            {
                _error = e.Message;
            }
        }
        ImGui.EndDisabled();
        if (problem != null)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(problem);
        }
        ImGui.SameLine(ImGui.GetWindowWidth() - 126);
        return Cancel();
    }

    bool ImportDialog()
    {
        if (_picker.DrawInline() is not { } result)
            return false;
        if (result.Length == 0)
            return true;
        var folder = File.Exists(result) ? Path.GetDirectoryName(result)! : result;
        if (!File.Exists(EditorProject.FileIn(folder)))
        {
            _error = $"No trinity.json in {folder}";
            _picker.Start(_picker.Title, folder, wantProject: true);
            return false;
        }
        Add(folder);
        Remember("location", Path.GetDirectoryName(folder) ?? folder);
        return true;
    }

    bool ScanDialog()
    {
        if (_picker.DrawInline() is not { } folder)
            return false;
        if (folder.Length == 0)
            return true;
        var found = FindProjects(folder, depth: 5).ToList();
        foreach (var f in found)
            if (_entries.All(e => e.Path != f))
                _entries.Add(new ProjectEntry { Path = f });
        Save();
        Log.Info("projects", $"scan of {folder}: {found.Count} project(s)");
        return true;
    }

    static IEnumerable<string> FindProjects(string folder, int depth)
    {
        if (File.Exists(EditorProject.FileIn(folder)))
        {
            yield return Path.GetFullPath(folder).TrimEnd('/');
            yield break; // projects don't nest
        }
        if (depth == 0)
            yield break;
        IEnumerable<string> subfolders;
        try
        {
            subfolders = Directory.EnumerateDirectories(folder).ToList();
        }
        catch (Exception)
        {
            yield break;
        }
        foreach (var d in subfolders)
            if (!ProjectFiles.Ignored(folder, Path.GetFileName(d)) && Path.GetFileName(d) != "node_modules")
                foreach (var p in FindProjects(d, depth - 1))
                    yield return p;
    }

    bool RenameDialog()
    {
        ImGui.Text("Rename project");
        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(360);
        var enter = ImGui.InputText("##name", _nameEdit, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
        var name = _nameEdit.ToString().Trim();
        ImGui.BeginDisabled(name.Length == 0);
        if (ImGui.Button("Rename", new float2(110, 0)) || enter && name.Length > 0)
        {
            try
            {
                EditorProject.Edit(_selected!, json => json["name"] = name);
                return true;
            }
            catch (Exception e)
            {
                _error = e.Message;
            }
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        return Cancel();
    }

    bool DuplicateDialog()
    {
        ImGui.Text("Duplicate project (beside the original)");
        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(360);
        ImGui.InputText("##name", _nameEdit, ImGuiInputTextFlags.AutoSelectAll);
        var name = _nameEdit.ToString().Trim();
        var target = Path.Combine(Path.GetDirectoryName(_selected!)!, ProjectTemplate.FolderName(name));
        ImGui.TextDisabled(target);
        var exists = Directory.Exists(target);
        if (exists)
            ImGui.TextColored(ImGuiHost.Color(0.95f, 0.72f, 0.33f), "A folder with that name exists");
        ImGui.BeginDisabled(name.Length == 0 || exists);
        if (ImGui.Button("Duplicate", new float2(110, 0)))
        {
            try
            {
                ProjectFiles.Copy(_selected!, target);
                EditorProject.Edit(target, json => json["name"] = name);
                Add(target);
                return true;
            }
            catch (Exception e)
            {
                _error = e.Message;
            }
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        return Cancel();
    }

    bool TagsDialog()
    {
        var project = _selected == null ? null : Info(_selected);
        if (project == null)
            return true;
        ImGui.Text($"Tags of {project.Name}");
        ImGui.Separator();
        string? remove = null;
        foreach (var tag in project.Tags)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, TileColor(tag) with { W = 0.55f });
            if (ImGui.SmallButton(tag + "  x"))
                remove = tag;
            ImGui.PopStyleColor();
            ImGui.SameLine();
        }
        if (project.Tags.Length == 0)
            ImGui.TextDisabled("No tags");
        else
            ImGui.NewLine();
        ImGui.SetNextItemWidth(220);
        var enter = ImGui.InputTextWithHint("##tag", "new tag", _tagEdit, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        var tagText = _tagEdit.ToString().Trim().ToLowerInvariant().Replace(' ', '-');
        var add = (ImGui.Button("Add") || enter) && tagText.Length > 0 && !project.Tags.Contains(tagText);
        if (add || remove != null)
        {
            var tags = project.Tags.Where(t => t != remove).Concat(add ? [tagText] : []).ToArray();
            try
            {
                EditorProject.Edit(_selected!, json => json["tags"] = new JsonArray(tags.Select(t => (JsonNode)JsonValue.Create(t)).ToArray()));
                _tagEdit.SetValue("".AsSpan());
            }
            catch (Exception e)
            {
                _error = e.Message;
            }
        }
        ImGui.Separator();
        return ImGui.Button("Done", new float2(110, 0)) || ImGui.IsKeyPressed(ImGuiKey.Escape);
    }

    bool RemoveDialog()
    {
        ImGui.Text($"Remove {Info(_selected ?? "")?.Name ?? _selected ?? "the project"} from the list?");
        ImGui.TextDisabled("The project's files stay where they are; Import brings it back.");
        if (ImGui.Button("Remove", new float2(110, 0)))
        {
            _entries.RemoveAll(e => e.Path == _selected);
            _selected = null;
            Save();
            return true;
        }
        ImGui.SameLine();
        return Cancel();
    }

    bool SettingsDialog()
    {
        ImGui.Text("Settings");
        ImGui.Separator();
        ImGui.Text("Trinity source folder (new projects reference the engine from here)");
        if (ImGui.IsWindowAppearing())
            _locationEdit.SetValue((ProjectTemplate.EngineRoot(_settings) ?? "").AsSpan());
        ImGui.SetNextItemWidth(460);
        ImGui.InputText("##engine", _locationEdit);
        ImGui.SameLine();
        if (ImGui.Button("Browse..."))
        {
            _picker.Start("Trinity's source folder", _locationEdit.ToString(), wantProject: false);
            ImGui.OpenPopup("browse-engine");
        }
        if (_picker.Draw("browse-engine") is { } picked)
            _locationEdit.SetValue(picked.AsSpan());
        var valid = ProjectTemplate.IsEngineRoot(_locationEdit.ToString());
        if (!valid)
            ImGui.TextColored(ImGuiHost.Color(0.95f, 0.72f, 0.33f), "No src/Trinity/Trinity.csproj there");
        ImGui.Separator();
        ImGui.BeginDisabled(!valid);
        if (ImGui.Button("Save", new float2(110, 0)))
        {
            Remember("engine", Path.GetFullPath(_locationEdit.ToString()));
            ProjectTemplate.Current = ProjectTemplate.EngineRoot(_settings);
            return true;
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        return Cancel();
    }

    public void Dispose()
    {
        _gpu.WaitIdle();
        _ui.Dispose();
        _gpu.Dispose();
        _window.Dispose();
    }
}

/// <summary>A folder browser drawn with ImGui, so the editor needs no file-dialog library.</summary>
sealed class FolderPicker
{
    string _current = "/";
    readonly ImInputString _path = new(1024);
    bool _wantProject;
    public string Title { get; private set; } = "";

    public void Start(string title, string folder, bool wantProject)
    {
        Title = title;
        _wantProject = wantProject;
        _current = Directory.Exists(folder) ? Path.GetFullPath(folder) : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _path.SetValue(_current.AsSpan());
    }

    /// <summary>The browser inside an already-open modal. Returns the chosen folder, "" if cancelled, or null while choosing.</summary>
    public string? DrawInline()
    {
        ImGui.Text(Title);
        ImGui.Separator();
        if (Icons.Button("up", Icon.Back, Icons.FileColor, "Up a folder") && Path.GetDirectoryName(_current) is { } up)
            Go(up);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(560);
        if (ImGui.InputText("##path", _path, ImGuiInputTextFlags.EnterReturnsTrue))
        {
            var typed = _path.ToString().Trim();
            if (typed.StartsWith('~'))
                typed = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + typed[1..];
            if (Directory.Exists(typed))
                Go(typed);
        }
        ImGui.BeginChild("folders", new float2(600, 340), ImGuiChildFlags.Borders);
        string[] folders = [], files = [];
        try
        {
            folders = Directory.GetDirectories(_current).Where(d => !Path.GetFileName(d).StartsWith('.')).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            files = Directory.GetFiles(_current).Where(f => !Path.GetFileName(f).StartsWith('.')).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception e)
        {
            ImGui.TextDisabled(e.Message);
        }
        string? chosen = null;
        foreach (var d in folders)
        {
            var isProject = File.Exists(EditorProject.FileIn(d));
            Icons.Inline(Icon.Folder, isProject ? Icons.Gold : Icons.FolderColor);
            ImGui.SameLine();
            if (ImGui.Selectable(Path.GetFileName(d) + (isProject ? "   (project)" : ""), false, ImGuiSelectableFlags.AllowDoubleClick))
            {
                if (isProject && _wantProject && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    chosen = d;
                else
                    Go(d);
            }
        }
        foreach (var f in files)
        {
            var (icon, color) = Icons.ForFile(f);
            Icons.Inline(icon, color);
            ImGui.SameLine();
            ImGui.TextDisabled(Path.GetFileName(f));
        }
        ImGui.EndChild();
        var here = _wantProject ? File.Exists(EditorProject.FileIn(_current)) : true;
        ImGui.BeginDisabled(!here);
        if (ImGui.Button(_wantProject ? "Import this folder" : "Use this folder", new float2(180, 0)))
            chosen = _current;
        ImGui.EndDisabled();
        if (_wantProject && !here)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(no trinity.json here: open a project's folder, or double-click it)");
        }
        ImGui.SameLine(ImGui.GetWindowWidth() - 126);
        if (ImGui.Button("Cancel", new float2(110, 0)) || ImGui.IsKeyPressed(ImGuiKey.Escape))
            return "";
        return chosen;
    }

    /// <summary>The browser as its own modal, opened with ImGui.OpenPopup(id). Returns the folder when one is chosen.</summary>
    public string? Draw(string id)
    {
        if (!ImGui.BeginPopupModal(id, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar))
            return null;
        var result = DrawInline();
        if (result != null)
            ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
        return result is { Length: > 0 } ? result : null;
    }

    void Go(string folder)
    {
        _current = Path.GetFullPath(folder);
        _path.SetValue(_current.AsSpan());
    }
}

/// <summary>A new project: trinity.json, a game project that references Trinity, a first scene, and folders.</summary>
static class ProjectTemplate
{
    /// <summary>"My Game" as a folder and C# name: "MyGame".</summary>
    public static string FolderName(string name)
    {
        var sb = new StringBuilder();
        foreach (var word in name.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries))
            foreach (var (ch, i) in word.Select((c, i) => (c, i)))
                if (char.IsLetterOrDigit(ch))
                    sb.Append(i == 0 ? char.ToUpperInvariant(ch) : ch);
        if (sb.Length == 0 || char.IsDigit(sb[0]))
            sb.Insert(0, "Game");
        return sb.ToString();
    }

    public static bool IsEngineRoot(string folder) =>
        folder.Length > 0 && File.Exists(Path.Combine(folder, "src", "Trinity", "Trinity.csproj"));

    /// <summary>Trinity's source folder: set in the Project Manager's settings, or found above the editor's own folder.</summary>
    public static string? EngineRoot(Settings settings)
    {
        if (settings.Get("project-manager", EditorJson.Default.DictionaryStringString).GetValueOrDefault("engine") is { } set && IsEngineRoot(set))
            return set;
        for (var d = AppContext.BaseDirectory; d != null; d = Path.GetDirectoryName(d.TrimEnd('/')))
            if (IsEngineRoot(d))
                return Path.GetFullPath(d).TrimEnd('/');
        return null;
    }

    public static void Create(string folder, string name, string engine)
    {
        var code = FolderName(name);
        Directory.CreateDirectory(folder);
        foreach (var sub in (ReadOnlySpan<string>)["Assets", "Scenes", "Scripts"])
            Directory.CreateDirectory(Path.Combine(folder, sub));
        var version = typeof(ProjectTemplate).Assembly.GetName().Version?.ToString(2) ?? "0.1";
        Write(folder, "trinity.json", $$"""
            {
              "name": {{JsonValue.Create(name)!.ToJsonString()}},
              "game": "{{code}}.csproj",
              "assets": "Assets",
              "scenes": "Scenes",
              "scripts": "Scripts",
              "engine": "{{version}}",
              "tags": []
            }
            """);
        // Trinity's build settings and targets (shaders, script registration, licences) come from the engine's folder
        Write(folder, "Directory.Build.props", EngineProps(engine));
        Write(folder, "Directory.Build.targets", """
            <Project>
              <Import Project="$(TrinityRoot)Directory.Build.targets" />
            </Project>
            """);
        Write(folder, code + ".csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <AssemblyName>{code}</AssemblyName>
                <RootNamespace>{code}</RootNamespace>
                <PublishAot>true</PublishAot>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="$(TrinityRoot)src/Trinity/Trinity.csproj" />
              </ItemGroup>
              <ItemGroup>
                <None Include="Assets/**;Scenes/**" CopyToOutputDirectory="PreserveNewest" />
              </ItemGroup>
            </Project>
            """);
        Write(folder, "Program.cs", $$"""
            using Trinity;
            using Trinity.Scenes;

            // {{name}}: the scenes made in Trinity Editor, brought to life by the scripts in Scripts/.
            Engine.Run(new SceneGame("Scenes/main.scene.json"), new EngineOptions { Title = {{JsonValue.Create(name)!.ToJsonString()}}, AppName = "{{code.ToLowerInvariant()}}" });
            """);
        Write(folder, ".gitignore", "bin/\nobj/\n*.autosave.scene.json\n*.unsaved\n");

        // A first scene: a camera looking at the origin, and the sun
        var world = new World();
        var camera = world.Create("Camera");
        world.SetPosition(camera, new System.Numerics.Vector3(0, 3, 8));
        world.SetRotation(camera, new System.Numerics.Vector3(-15, 0, 0));
        world.Add(camera, new Camera3D { Current = true });
        var sun = world.Create("Sun");
        world.SetRotation(sun, new System.Numerics.Vector3(-50, 40, 0));
        world.Add(sun, new DirectionalLight());
        new SceneLibrary(folder).Save("Scenes/main.scene.json", SceneLibrary.Write(world, new SceneEnvironment()));
    }

    /// <summary>
    /// The project's Directory.Build.props: where Trinity is. TRINITY_ROOT, which the editor sets for the
    /// builds it starts, wins over the path written here, so a project builds against the editor's own
    /// Trinity even if the written path is out of date; the editor also rewrites a path that no longer
    /// leads to Trinity (<see cref="RepairEngine"/>).
    /// </summary>
    static string EngineProps(string engine) => $"""
        <Project>
          <PropertyGroup>
            <!-- Where Trinity's source is. TRINITY_ROOT (set by Trinity Editor) wins; the editor keeps this path up to date. -->
            <TrinityRoot Condition="'$(TRINITY_ROOT)' != ''">$([MSBuild]::EnsureTrailingSlash('$(TRINITY_ROOT)'))</TrinityRoot>
            <TrinityRoot Condition="'$(TrinityRoot)' == ''">{engine}/</TrinityRoot>
          </PropertyGroup>
          <Import Project="$(TrinityRoot)Directory.Build.props" />
        </Project>
        """;

    /// <summary>The engine folder the editor builds and runs games with (set at startup).</summary>
    public static string? Current;

    /// <summary>Points a build or run of a game at <see cref="Current"/>.</summary>
    public static void UseEngine(ProcessStartInfo start)
    {
        if (Current != null)
            start.Environment["TRINITY_ROOT"] = Current;
    }

    /// <summary>Repairs a project's engine path if Trinity moved (<see cref="RepairEngine"/>), saying so in the log.</summary>
    public static void CheckEngine(string projectFolder)
    {
        if (Current == null)
            return;
        try
        {
            if (RepairEngine(projectFolder, Current) is { } old)
                Log.Info("projects", $"{projectFolder}: Trinity was at {old}, now at {Current}; Directory.Build.props updated");
        }
        catch (Exception e)
        {
            Log.Warn("projects", $"couldn't update {projectFolder}'s engine path: {e.Message}");
        }
    }

    /// <summary>
    /// If the project's Directory.Build.props points at a folder that isn't Trinity any more (Trinity was
    /// moved), points it at <paramref name="engine"/>. Returns the old path when it changed it.
    /// </summary>
    public static string? RepairEngine(string projectFolder, string engine)
    {
        var props = Path.Combine(projectFolder, "Directory.Build.props");
        if (!File.Exists(props))
            return null;
        var text = File.ReadAllText(props);
        // The written path: <TrinityRoot>path/</TrinityRoot>, with or without a condition (not the $(...) one)
        var match = System.Text.RegularExpressions.Regex.Match(text, @"<TrinityRoot(?: Condition=""'\$\(TrinityRoot\)' == ''"")?>(?<path>[^$<][^<]*)</TrinityRoot>");
        if (!match.Success)
            return null;
        var written = match.Groups["path"].Value.TrimEnd('/');
        if (IsEngineRoot(written) || !IsEngineRoot(engine))
            return null;
        var temp = props + ".tmp";
        File.WriteAllText(temp, EngineProps(engine).ReplaceLineEndings("\n") + "\n");
        File.Move(temp, props, overwrite: true);
        return written;
    }

    static void Write(string folder, string file, string text) => File.WriteAllText(Path.Combine(folder, file), text.ReplaceLineEndings("\n") + "\n");
}
