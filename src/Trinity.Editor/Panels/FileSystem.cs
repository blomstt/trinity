using System.Diagnostics;
using System.Numerics;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Trinity.Scenes;

namespace Trinity.Editor;

/// <summary>
/// The FileSystem dock: the project's folders and files as a tree, with favourites at the
/// top, a path bar with back and forward, and a filter. Double-click opens a scene or script, or places
/// a model; drag a model or scene into the viewport, onto a node in the Scene dock or onto an asset
/// field in the Inspector; drag anything onto a folder to move it. Moving and renaming update every
/// scene that refers to the file. Deleting moves to the desktop trash.
/// Beside it, the History dock lists the undo steps; click one to go back (or forward) to it.
/// </summary>
sealed partial class EditorApp
{
    const string FileSystemPanel = "FileSystem", HistoryPanel = "History";
    const string AssetPayload = "trinity-asset";

    sealed class FsNode(string path, string name, bool folder, DateTime modified)
    {
        public readonly string Path = path, Name = name;
        public readonly bool Folder = folder;
        public readonly DateTime Modified = modified;
        public readonly List<FsNode> Children = [];
    }

    enum FsSort { Name, Type, Modified }

    FsNode? _fsRoot;
    double _fsScanned = -10;
    string _fsSelected = "";
    readonly Stack<string> _fsBack = new(), _fsForward = new();
    readonly HashSet<string> _fsOpen = [""];
    readonly ImInputString _fsFilter = new(128), _fsPathEdit = new(512), _fsPromptEdit = new(256);
    FsSort _fsSort;
    bool _fsPathActive;
    List<string> _favorites = [];
    string _draggedAsset = "";
    bool _showFileSystem = true, _showHistory = true;

    // A name prompt (new folder, rename...) and a delete confirmation, shown as modals
    (string Title, Action<string> Done)? _fsPrompt;
    bool _fsPromptOpen;
    (string Path, List<string> UsedBy)? _fsDelete;

    void ScanFileSystem(bool force = false)
    {
        if (!force && ImGui.GetTime() - _fsScanned < 2)
            return;
        _fsScanned = ImGui.GetTime();
        _fsRoot = Scan(_project.Root, "", _project.Name);
    }

    FsNode Scan(string full, string relative, string name)
    {
        var node = new FsNode(relative, name, true, Directory.GetLastWriteTimeUtc(full));
        try
        {
            foreach (var d in Directory.EnumerateDirectories(full))
                if (!ProjectFiles.Ignored(full, Path.GetFileName(d)))
                    node.Children.Add(Scan(d, Join(relative, Path.GetFileName(d)), Path.GetFileName(d)));
            foreach (var f in Directory.EnumerateFiles(full))
                if (!ProjectFiles.Ignored(full, Path.GetFileName(f)))
                    node.Children.Add(new FsNode(Join(relative, Path.GetFileName(f)), Path.GetFileName(f), false, File.GetLastWriteTimeUtc(f)));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return node;
    }

    static string Join(string folder, string name) => folder.Length == 0 ? name : folder + "/" + name;

    static string Parent(string path) => path.LastIndexOf('/') is var i and > 0 ? path[..i] : "";

    IEnumerable<FsNode> Sorted(List<FsNode> children) =>
        children.OrderByDescending(c => c.Folder).ThenBy(c => _fsSort switch
        {
            FsSort.Type => c.Folder ? "" : TypeOf(c.Name),
            _ => "",
        }, StringComparer.Ordinal).ThenByDescending(c => _fsSort == FsSort.Modified ? c.Modified : default)
        .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase);

    static string TypeOf(string name) => name.EndsWith(".scene.json", StringComparison.Ordinal) ? ".scene.json" : Path.GetExtension(name).ToLowerInvariant();

    void SelectFile(string path, bool remember = true)
    {
        if (path == _fsSelected)
            return;
        if (remember)
        {
            _fsBack.Push(_fsSelected);
            _fsForward.Clear();
        }
        _fsSelected = path;
        // Show it: open the folders down to it
        for (var p = Parent(path); p.Length > 0; p = Parent(p))
            _fsOpen.Add(p);
    }

    void FileSystem()
    {
        if (!ImGui.Begin(FileSystemPanel, ref _showFileSystem))
        {
            ImGui.End();
            return;
        }
        ScanFileSystem();

        // Back, forward, and the path of the selection (type a path and press Enter to go there)
        ImGui.BeginDisabled(_fsBack.Count == 0);
        if (Icons.Button("back", Icon.Back, Icons.FileColor, "Back") && _fsBack.Count > 0)
        {
            _fsForward.Push(_fsSelected);
            SelectFile(_fsBack.Pop(), remember: false);
        }
        ImGui.EndDisabled();
        ImGui.SameLine(0, 2);
        ImGui.BeginDisabled(_fsForward.Count == 0);
        if (Icons.Button("forward", Icon.Forward, Icons.FileColor, "Forward") && _fsForward.Count > 0)
        {
            _fsBack.Push(_fsSelected);
            SelectFile(_fsForward.Pop(), remember: false);
        }
        ImGui.EndDisabled();
        ImGui.SameLine(0, 4);
        if (!_fsPathActive)
            _fsPathEdit.SetValue(_fsSelected.AsSpan());
        ImGui.SetNextItemWidth(-ImGui.GetFrameHeight() - 4);
        var go = ImGui.InputText("##path", _fsPathEdit, ImGuiInputTextFlags.EnterReturnsTrue);
        _fsPathActive = ImGui.IsItemActive();
        if (go)
        {
            var typed = SceneLibrary.Normalize(_fsPathEdit.ToString().Trim().Trim('/'));
            if (File.Exists(_project.Absolute(typed)) || Directory.Exists(_project.Absolute(typed)))
                SelectFile(typed);
            else
                Log.Warn("editor", $"no {typed} in the project");
        }
        ImGui.SameLine(0, 4);
        if (Icons.Button("sort", Icon.Sort, Icons.FileColor, $"Sort by {_fsSort.ToString().ToLowerInvariant()} (click to change)"))
            _fsSort = (FsSort)(((int)_fsSort + 1) % 3);

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##filter", "Filter files", _fsFilter);

        ImGui.BeginChild("files");
        if (_fsRoot != null)
        {
            if (!_fsFilter.IsEmpty)
                FilterResults(_fsRoot, _fsFilter.ToString());
            else
            {
                Favorites();
                FsTree(_fsRoot, isRoot: true);
            }
        }
        ImGui.EndChild();
        FsPrompt();
        FsConfirmDelete();
        ImGui.End();
    }

    void Favorites()
    {
        var flags = ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.FramePadding | (_favorites.Count == 0 ? ImGuiTreeNodeFlags.Leaf : ImGuiTreeNodeFlags.DefaultOpen);
        var open = IconTreeNode("favorites", "Favorites", Icon.Star, Icons.Gold, flags);
        if (!open)
            return;
        foreach (var f in _favorites.ToList())
        {
            var folder = Directory.Exists(_project.Absolute(f));
            if (!folder && !File.Exists(_project.Absolute(f)))
                continue;
            var node = folder ? new FsNode(f, f, true, default) : new FsNode(f, f, false, default);
            FsRow(node, leafOnly: true);
        }
        ImGui.TreePop();
    }

    /// <summary>A tree node with an icon drawn before its label.</summary>
    static bool IconTreeNode(string id, string label, Icon icon, float4 color, ImGuiTreeNodeFlags flags)
    {
        ImGui.PushID(id);
        var open = ImGui.TreeNodeEx("        " + label, flags | ImGuiTreeNodeFlags.AllowOverlap);
        var min = ImGui.GetItemRectMin();
        var height = ImGui.GetItemRectMax().Y - min.Y;
        var size = ImGui.GetTextLineHeight();
        Icons.Draw(ImGui.GetWindowDrawList(), icon, new float2(min.X + ImGui.GetTreeNodeToLabelSpacing() + 2, min.Y + (height - size) / 2), size, color);
        ImGui.PopID();
        return open;
    }

    void FsTree(FsNode node, bool isRoot = false)
    {
        var open = FsRow(node, isRoot: isRoot);
        if (!open)
            return;
        foreach (var c in Sorted(node.Children))
            if (c.Folder)
                FsTree(c);
            else
                FsRow(c);
        ImGui.TreePop();
    }

    /// <summary>One file or folder; returns whether a folder is open (then the caller pops it).</summary>
    bool FsRow(FsNode node, bool isRoot = false, bool leafOnly = false)
    {
        var flags = ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.FramePadding | ImGuiTreeNodeFlags.OpenOnArrow;
        if (!node.Folder || leafOnly)
            flags |= ImGuiTreeNodeFlags.Leaf;
        if (node.Path == _fsSelected)
            flags |= ImGuiTreeNodeFlags.Selected;
        if (node.Folder && !leafOnly)
            ImGui.SetNextItemOpen(_fsOpen.Contains(node.Path));
        var (icon, color) = node.Folder
            ? (_fsOpen.Contains(node.Path) && !leafOnly ? Icon.FolderOpen : Icon.Folder, Icons.FolderColor)
            : Icons.ForFile(node.Name);
        var label = isRoot ? node.Name + "/" : node.Name;
        var open = IconTreeNode(leafOnly ? "fav:" + node.Path : node.Path, label, icon, color, flags);
        if (node.Folder && !leafOnly && ImGui.IsItemToggledOpen())
        {
            if (open) _fsOpen.Add(node.Path);
            else _fsOpen.Remove(node.Path);
        }
        if (ImGui.IsItemClicked() && !ImGui.IsItemToggledOpen())
            SelectFile(node.Path);
        if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            ActivateFile(node.Path, node.Folder);
        if (ImGui.IsItemHovered() && !node.Folder && ImGui.GetIO().KeyCtrl)
            ImGui.SetTooltip(node.Path);

        if (!isRoot && ImGui.BeginDragDropSource())
        {
            _draggedAsset = node.Path;
            ImGui.SetDragDropPayload(AssetPayload, default, 0);
            Icons.Inline(icon, color);
            ImGui.SameLine();
            ImGui.Text(node.Name);
            ImGui.EndDragDropSource();
        }
        // Drop onto a folder: move there
        if (node.Folder && ImGui.BeginDragDropTarget())
        {
            if (!ImGui.AcceptDragDropPayload(AssetPayload).IsNull())
            {
                var moving = _draggedAsset;
                var to = Join(node.Path, Path.GetFileName(moving));
                if (File.Exists(_project.Absolute(to)) || Directory.Exists(_project.Absolute(to)))
                    Log.Warn("editor", $"{node.Path} already has a {Path.GetFileName(moving)}; not moved");
                else if (moving != node.Path && Parent(moving) != node.Path && !node.Path.StartsWith(moving + "/", StringComparison.Ordinal))
                    _afterTree = () => MoveFile(moving, to);
            }
            ImGui.EndDragDropTarget();
        }

        if (ImGui.BeginPopupContextItem())
        {
            SelectFile(node.Path);
            FileMenu(node, isRoot);
            ImGui.EndPopup();
        }
        if (open && (leafOnly || !node.Folder))
        {
            ImGui.TreePop();
            return false;
        }
        return open;
    }

    void FilterResults(FsNode node, string text)
    {
        foreach (var c in Sorted(node.Children))
        {
            if (!c.Folder && c.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                FsRow(c);
                ImGui.SameLine();
                ImGui.TextDisabled(Parent(c.Path));
            }
            if (c.Folder)
                FilterResults(c, text);
        }
    }

    /// <summary>Double-click: open a scene or script, place a model, or open or close a folder.</summary>
    void ActivateFile(string path, bool folder)
    {
        if (folder)
        {
            if (!_fsOpen.Remove(path))
                _fsOpen.Add(path);
        }
        else if (path.EndsWith(".scene.json", StringComparison.Ordinal))
            _afterTree = () => OpenScene(path);
        else if (path.EndsWith(".cs", StringComparison.Ordinal))
            _pendingScript = (_project.Absolute(path), -1);
        else if (IsModel(path))
            _afterTree = () => Place(path, NewNodePosition(), _scene.Primary);
    }

    void FileMenu(FsNode node, bool isRoot)
    {
        var path = node.Path;
        var folder = node.Folder ? path : Parent(path);
        if (!node.Folder)
        {
            if (path.EndsWith(".scene.json", StringComparison.Ordinal))
            {
                if (ImGui.MenuItem("Open scene")) _afterTree = () => OpenScene(path);
                if (ImGui.MenuItem("Instance in scene", default, false, path != _scene.Path))
                    _afterTree = () => InstanceScene(path, NewNodePosition(), _scene.Primary);
            }
            else if (IsModel(path) && ImGui.MenuItem("Place in scene"))
                _afterTree = () => Place(path, NewNodePosition(), _scene.Primary);
            else if (path.EndsWith(".cs", StringComparison.Ordinal) && ImGui.MenuItem("Open script"))
                _pendingScript = (_project.Absolute(path), -1);
            ImGui.Separator();
        }
        if (ImGui.BeginMenu("New"))
        {
            if (ImGui.MenuItem("Folder..."))
                Prompt("New folder", "", name => Directory.CreateDirectory(_project.Absolute(Join(folder, name))));
            if (ImGui.MenuItem("Scene..."))
                Prompt("New scene", "new", name =>
                {
                    var scene = Join(folder, name.EndsWith(".scene.json", StringComparison.Ordinal) ? name : name + ".scene.json");
                    if (File.Exists(_project.Absolute(scene)))
                        throw new IOException($"{scene} already exists");
                    _library.Save(scene, new SceneDocument());
                    OpenScene(scene);
                });
            if (ImGui.MenuItem("Script..."))
                _scripts.NewScript("NewScript");
            ImGui.EndMenu();
        }
        if (!isRoot)
        {
            if (ImGui.MenuItem("Rename..."))
                Prompt("Rename", node.Name, name => MoveFile(path, Join(Parent(path), node.Folder ? name : ProjectFiles.KeepExtension(node.Name, name))));
            if (ImGui.MenuItem("Duplicate..."))
                Prompt("Duplicate", CopyName(node.Name), name =>
                {
                    var to = _project.Absolute(Join(Parent(path), name));
                    if (File.Exists(to) || Directory.Exists(to))
                        throw new IOException($"{name} already exists");
                    ProjectFiles.Copy(_project.Absolute(path), to);
                });
            if (ImGui.MenuItem("Move to trash..."))
                _fsDelete = (path, ProjectFiles.ReferencesTo(_library, path));
            ImGui.Separator();
        }
        var favorite = _favorites.Contains(path);
        if (ImGui.MenuItem(favorite ? "Remove from favorites" : "Add to favorites"))
        {
            if (favorite) _favorites.Remove(path);
            else _favorites.Add(path);
            SaveFavorites();
        }
        if (ImGui.MenuItem("Copy path")) ImGui.SetClipboardText(path);
        if (ImGui.MenuItem("Show in file manager"))
            Process.Start(new ProcessStartInfo("xdg-open", [_project.Absolute(folder)]) { UseShellExecute = false })?.Dispose();
    }

    static string CopyName(string name)
    {
        var ext = name.EndsWith(".scene.json", StringComparison.Ordinal) ? ".scene.json" : Path.GetExtension(name);
        return name[..^ext.Length] + " copy" + ext;
    }

    void Prompt(string title, string initial, Action<string> done)
    {
        _fsPromptEdit.SetValue(initial.AsSpan());
        _fsPrompt = (title, done);
        _fsPromptOpen = true;
    }

    void FsPrompt()
    {
        if (_fsPromptOpen)
        {
            ImGui.OpenPopup("fs-prompt");
            _fsPromptOpen = false;
        }
        if (_fsPrompt is not { } prompt || !ImGui.BeginPopupModal("fs-prompt", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar))
            return;
        ImGui.Text(prompt.Title);
        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(320);
        var enter = ImGui.InputText("##name", _fsPromptEdit, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
        var name = _fsPromptEdit.ToString().Trim();
        var valid = name.Length > 0 && name.IndexOfAny(['/', '\\']) < 0 && name is not ("." or "..");
        ImGui.BeginDisabled(!valid);
        if (ImGui.Button("OK", new float2(100, 0)) || enter && valid)
        {
            try
            {
                prompt.Done(name);
                ScanFileSystem(force: true);
            }
            catch (Exception e)
            {
                Log.Error("editor", $"{prompt.Title}: {e.Message}");
            }
            _fsPrompt = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new float2(100, 0)) || ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            _fsPrompt = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    void FsConfirmDelete()
    {
        if (_fsDelete is { } pending && !ImGui.IsPopupOpen("fs-delete"))
            ImGui.OpenPopup("fs-delete");
        if (!ImGui.BeginPopupModal("fs-delete", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar))
            return;
        if (_fsDelete is { } d)
        {
            ImGui.Text($"Move {d.Path} to the trash?");
            if (d.UsedBy.Count > 0)
            {
                ImGui.TextColored(Warning, $"Used by {d.UsedBy.Count} scene{(d.UsedBy.Count == 1 ? "" : "s")}:");
                foreach (var s in d.UsedBy.Take(8))
                    ImGui.BulletText(s);
                if (d.UsedBy.Count > 8)
                    ImGui.TextDisabled($"and {d.UsedBy.Count - 8} more");
            }
            ImGui.TextDisabled("It can be restored from the trash in your file manager.");
            if (ImGui.Button("Move to trash", new float2(140, 0)))
            {
                try
                {
                    ProjectFiles.Trash(_project.Absolute(d.Path));
                    Log.Info("editor", $"moved {d.Path} to the trash");
                    _favorites.Remove(d.Path);
                    if (_fsSelected == d.Path)
                        _fsSelected = Parent(d.Path);
                    ScanFileSystem(force: true);
                }
                catch (Exception e)
                {
                    Log.Error("editor", $"couldn't move {d.Path} to the trash: {e.Message}");
                }
                _fsDelete = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel", new float2(100, 0)) || ImGui.IsKeyPressed(ImGuiKey.Escape))
            {
                _fsDelete = null;
                ImGui.CloseCurrentPopup();
            }
        }
        else
            ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    /// <summary>Moves or renames a file or folder, updating every scene that refers to it (the open one too).</summary>
    void MoveFile(string from, string to)
    {
        var (scenes, scripts) = ProjectFiles.Move(_library, from, to);
        _library.Invalidate(from);
        foreach (var s in scenes)
            _library.Invalidate(s);
        Log.Info("editor", $"moved {from} to {to}" + (scenes.Count > 0 ? $"; updated {string.Join(", ", scenes)}" : ""));
        foreach (var s in scripts)
            Log.Warn("editor", $"{s} mentions {from}: update it by hand if it loads that path");
        // The open scene: its file may have moved, and what it refers to in memory
        if (ProjectFiles.Moved(_scene.Path, from, to) is { } moved)
            _scene.Path = moved;
        if (ProjectFiles.Rewrite(_scene.World, from, to))
        {
            _scene.ClearHistory();
            _scene.Dirty = true;
            Log.Warn("editor", "undo history cleared: the scene's asset paths changed");
        }
        if (_favorites.Remove(from))
        {
            _favorites.Add(to);
            SaveFavorites();
        }
        if (ProjectFiles.Moved(_fsSelected, from, to) is { } selected)
            _fsSelected = selected;
        ScanFileSystem(force: true);
    }

    void LoadFavorites() =>
        _favorites = [.. _settings.Get("favorites", EditorJson.Default.DictionaryStringStringArray).GetValueOrDefault(_project.Root) ?? []];

    void SaveFavorites()
    {
        var all = _settings.Get("favorites", EditorJson.Default.DictionaryStringStringArray);
        all[_project.Root] = [.. _favorites];
        _settings.Set("favorites", all, EditorJson.Default.DictionaryStringStringArray);
        _settings.Save();
    }

    // --- Dropping assets ----------------------------------------------------------------------------

    /// <summary>In a drag-drop target: takes a dragged model or scene and adds it under <paramref name="parent"/>.</summary>
    void AcceptAssetDrop(EntityId parent, Vector3? at = null)
    {
        if (ImGui.AcceptDragDropPayload(AssetPayload).IsNull())
            return;
        var path = _draggedAsset;
        var position = at ?? NewNodePosition();
        if (path.EndsWith(".scene.json", StringComparison.Ordinal))
            _afterTree = () => InstanceScene(path, position, parent);
        else if (IsModel(path))
            _afterTree = () => Place(path, position, parent);
        else
            Log.Warn("editor", $"{path} can't go in a scene (drop a model or a scene)");
    }

    /// <summary>Where new nodes go: on the ground under the viewport camera's target.</summary>
    Vector3 NewNodePosition() => new(_view.Camera.Target.X, 0, _view.Camera.Target.Z);

    // --- History --------------------------------------------------------------------------------------

    void History()
    {
        if (!ImGui.Begin(HistoryPanel, ref _showHistory))
        {
            ImGui.End();
            return;
        }
        var done = _scene.UndoSteps.ToList();
        var redo = _scene.RedoSteps.ToList();
        var jump = -1;
        if (ImGui.Selectable($"Opened {Path.GetFileName(_scene.Path)}", done.Count == 0))
            jump = 0;
        for (var i = 0; i < done.Count; i++)
        {
            ImGui.PushID(i);
            if (ImGui.Selectable(Capital(done[i]), i == done.Count - 1))
                jump = i + 1;
            ImGui.PopID();
        }
        ImGui.PushStyleColor(ImGuiCol.Text, Icons.Dim);
        for (var i = 0; i < redo.Count; i++)
        {
            ImGui.PushID(1000 + i);
            if (ImGui.Selectable(Capital(redo[i])))
                jump = done.Count + i + 1;
            ImGui.PopID();
        }
        ImGui.PopStyleColor();
        if (done.Count + redo.Count == 0)
            ImGui.TextDisabled("Edits show here; click one to go back to it.");
        ImGui.End();
        if (jump >= 0)
            _scene.StepTo(jump);
    }

    static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
