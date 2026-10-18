using System.Diagnostics;
using System.Numerics;
using System.Text;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Brutal.StbApi;
using Trinity.Animation;
using Trinity.Editor.Scripting;
using Trinity.Graphics;
using Trinity.Scenes;

namespace Trinity.Editor;

/// <summary>
/// The FileSystem dock: a content browser for the project's files.
/// <list type="bullet">
/// <item>Toolbar: Add (new folder, scene, script) and Import, Save All, bookmarks, back, forward and up,
/// and the path as buttons (each one goes there; the arrow after it lists its folders).</item>
/// <item>Left: the project's folders as a tree, with a search box. Middle: the folder's contents as tiles
/// (with thumbnails) or a list (with sizes and dates), a type filter and a search that looks through
/// subfolders. Right: a preview of what's selected: folder contents, an image, code in the script editor's
/// colours, a scene's nodes, a model's numbers. Bottom: the item count and View Options.</item>
/// <item>Click selects (Ctrl adds, Shift a range), double-click opens, drag into the viewport, onto a node
/// or an asset field, or onto a folder to move; right-click for everything you can do with an item.</item>
/// </list>
/// Moving and renaming update every scene that refers to the file; deleting moves to the desktop trash.
/// Beside it, the History dock lists the undo steps; click one to go back (or forward) to it.
/// </summary>
sealed partial class EditorApp
{
    const string FileSystemPanel = "FileSystem", HistoryPanel = "History";
    const string AssetPayload = "trinity-asset";

    sealed record FsEntry(string Path, string Name, bool Folder, long Size, DateTime Modified, bool Hidden);

    enum FsSort { Natural, Alphabetical, Modified, Extension, Size }
    enum FsInput { None, Create, Rename }
    enum FsView { Tiles, List }
    enum FsKind { All, Folders, Scenes, Models, Images, Scripts, Other }

    string _fsDir = "";
    readonly Dictionary<string, string> _fsCursorIn = []; // folder -> name of the current item there
    readonly Dictionary<string, (double At, List<FsEntry> Entries)> _fsListings = [];
    readonly HashSet<string> _fsSelected = [];
    readonly HashSet<string> _fsTreeOpen = [""];
    (List<string> Paths, bool Cut)? _fsYank;
    readonly Stack<string> _fsBack = new(), _fsForward = new();
    FsSort _fsSort = FsSort.Natural;
    FsView _fsView = FsView.Tiles;
    FsKind _fsKind = FsKind.All;
    float _fsTileSize = 72;
    bool _fsReverse, _fsShowHidden, _fsShowPreview = true, _fsScrollToCursor = true, _fsFocused, _fsFocusSearch;
    string _fsAnchor = ""; // the item a Shift-selection starts from
    int _fsColumns = 1; // tiles per row
    readonly ImInputString _fsSearch = new(128), _fsPathSearch = new(128);
    FsInput _fsInput;
    bool _fsInputOpen, _fsBookmarksOpen, _fsImportOpen, _fsViewLoaded;
    readonly ImInputString _fsInputEdit = new(256);
    readonly FolderPicker _fsImport = new();
    List<string> _favorites = [];
    string _draggedAsset = "";
    bool _showFileSystem = true, _showHistory = true;
    (List<string> Paths, List<string> UsedBy)? _fsDelete;
    bool _fsHelp;

    static readonly float4 Accent = ImGuiHost.Color(0.88f, 0.66f, 0.36f);
    static readonly float4 AccentDim = ImGuiHost.Color(0.43f, 0.33f, 0.19f);

    // --- Listing --------------------------------------------------------------------------------------

    static string Join(string folder, string name) => folder.Length == 0 ? name : folder + "/" + name;

    static string Parent(string path) => path.LastIndexOf('/') is var i and > 0 ? path[..i] : "";

    /// <summary>A folder's entries, read again at most once a second (and after anything changes them).</summary>
    List<FsEntry> Listing(string folder)
    {
        var now = ImGui.GetTime();
        if (_fsListings.TryGetValue(folder, out var cached) && now - cached.At < 1)
            return cached.Entries;
        var entries = new List<FsEntry>();
        var full = _project.Absolute(folder);
        try
        {
            foreach (var d in new DirectoryInfo(full).EnumerateFileSystemInfos())
            {
                var hidden = d.Name.StartsWith('.');
                if (!hidden && ProjectFiles.Ignored(full, d.Name))
                    continue; // build output and the editor's working files never show
                var isFolder = d is DirectoryInfo;
                entries.Add(new FsEntry(Join(folder, d.Name), d.Name, isFolder, isFolder ? 0 : ((FileInfo)d).Length, d.LastWriteTimeUtc, hidden));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        _fsListings[folder] = (now, entries);
        return entries;
    }

    void FsRefresh() => _fsListings.Clear();

    static FsKind KindOf(FsEntry e) =>
        e.Folder ? FsKind.Folders
        : e.Name.EndsWith(".scene.json", StringComparison.Ordinal) ? FsKind.Scenes
        : IsModel(e.Name) ? FsKind.Models
        : IsImage(e.Name) ? FsKind.Images
        : e.Name.EndsWith(".cs", StringComparison.Ordinal) ? FsKind.Scripts
        : FsKind.Other;

    static string KindLabel(FsEntry e) => KindOf(e) switch
    {
        FsKind.Folders => "Folder",
        FsKind.Scenes => "Scene",
        FsKind.Models => "Model",
        FsKind.Images => "Image",
        FsKind.Scripts => "Script",
        _ => Path.GetExtension(e.Name).TrimStart('.').ToUpperInvariant() is { Length: > 0 } ext ? ext : "File",
    };

    /// <summary>
    /// What the content view shows: a folder's entries, or with a search, the matching files and folders
    /// in it and everything under it; then the type filter, hidden files only when shown, and the sort
    /// (folders first).
    /// </summary>
    List<FsEntry> Visible(string folder, bool forContent = true)
    {
        var search = forContent ? _fsSearch.ToString().Trim() : "";
        IEnumerable<FsEntry> entries = search.Length > 0 ? Everything(folder, 3000).Where(e => e.Name.Contains(search, StringComparison.OrdinalIgnoreCase)) : Listing(folder);
        entries = entries.Where(e => _fsShowHidden || !e.Hidden);
        if (forContent && _fsKind != FsKind.All)
            entries = entries.Where(e => KindOf(e) == _fsKind);
        var list = entries.ToList();
        list.Sort((a, b) =>
        {
            if (a.Folder != b.Folder)
                return a.Folder ? -1 : 1;
            var c = _fsSort switch
            {
                FsSort.Alphabetical => string.CompareOrdinal(a.Name, b.Name),
                FsSort.Modified => b.Modified.CompareTo(a.Modified), // newest first
                FsSort.Extension => string.CompareOrdinal(Extension(a.Name), Extension(b.Name)) is var x and not 0 ? x : Natural(a.Name, b.Name),
                FsSort.Size => b.Size.CompareTo(a.Size), // largest first
                _ => Natural(a.Name, b.Name),
            };
            return _fsReverse ? -c : c;
        });
        return list;
    }

    /// <summary>Everything under a folder (for searching), up to a limit.</summary>
    IEnumerable<FsEntry> Everything(string folder, int limit)
    {
        var queue = new Queue<string>([folder]);
        var n = 0;
        while (queue.Count > 0 && n < limit)
            foreach (var e in Listing(queue.Dequeue()))
            {
                if (e.Folder && (_fsShowHidden || !e.Hidden))
                    queue.Enqueue(e.Path);
                if (++n > limit)
                    yield break;
                yield return e;
            }
    }

    static string Extension(string name) => name.EndsWith(".scene.json", StringComparison.Ordinal) ? ".scene.json" : Path.GetExtension(name);

    /// <summary>A name without the extension its type label already says (rock.glb shows as rock, Model).</summary>
    static string DisplayName(FsEntry e) =>
        e.Folder || KindOf(e) is FsKind.Other || e.Hidden ? e.Name : e.Name[..^Extension(e.Name).Length];

    /// <summary>Natural order, case-sensitive: "rock 2" before "rock 10".</summary>
    internal static int Natural(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                var si = i;
                var sj = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                var na = a.AsSpan(si, i - si).TrimStart('0');
                var nb = b.AsSpan(sj, j - sj).TrimStart('0');
                if (na.Length != nb.Length)
                    return na.Length - nb.Length;
                if (na.SequenceCompareTo(nb) is var d and not 0)
                    return d;
                continue;
            }
            if (a[i] != b[j])
                return a[i] - b[j];
            i++;
            j++;
        }
        return (a.Length - i) - (b.Length - j);
    }

    int CursorIn(List<FsEntry> entries, string folder)
    {
        if (_fsCursorIn.TryGetValue(folder, out var path) && entries.FindIndex(e => e.Path == path) is var i and >= 0)
            return i;
        return 0;
    }

    void SetCursor(List<FsEntry> entries, int index, bool extend = false)
    {
        if (entries.Count == 0)
            return;
        index = Math.Clamp(index, 0, entries.Count - 1);
        var before = CursorIn(entries, _fsDir);
        _fsCursorIn[_fsDir] = entries[index].Path;
        _fsScrollToCursor = true;
        if (extend)
        {
            // Shift: a range, from where it started to here; from the current item if nothing is selected,
            // or if a search or the filter now hides where it started
            var from = _fsSelected.Count > 0 ? entries.FindIndex(e => e.Path == _fsAnchor) : -1;
            if (from < 0)
            {
                from = before;
                _fsAnchor = entries[before].Path;
            }
            _fsSelected.Clear();
            for (var i = Math.Min(from, index); i <= Math.Max(from, index); i++)
                _fsSelected.Add(entries[i].Path);
        }
    }

    /// <summary>Goes into a folder (remembering where we were, for back and forward).</summary>
    void Cd(string folder, bool remember = true)
    {
        if (folder == _fsDir || !Directory.Exists(_project.Absolute(folder)))
            return;
        if (remember)
        {
            _fsBack.Push(_fsDir);
            _fsForward.Clear();
        }
        _fsDir = folder;
        _fsSearch.SetValue("".AsSpan());
        _fsSelected.Clear();
        _fsScrollToCursor = true;
        // The tree shows where we are
        for (var p = folder; p.Length > 0; p = Parent(p))
            _fsTreeOpen.Add(Parent(p));
    }

    void Back()
    {
        if (_fsBack.Count == 0)
            return;
        _fsForward.Push(_fsDir);
        Cd(_fsBack.Pop(), remember: false);
    }

    void Forward()
    {
        if (_fsForward.Count == 0)
            return;
        _fsBack.Push(_fsDir);
        Cd(_fsForward.Pop(), remember: false);
    }

    /// <summary>Shows a project file in the FileSystem dock (--browse, for checking without a person).</summary>
    public void Browse(string path) => Reveal(SceneLibrary.Normalize(path).Trim('/'));

    /// <summary>Shows a file or folder: goes to its folder with it as the current item.</summary>
    void Reveal(string path)
    {
        Cd(Parent(path));
        _fsCursorIn[Parent(path)] = path;
        _fsScrollToCursor = true;
    }

    /// <summary>What an operation acts on: the selection, or the current item.</summary>
    List<string> Targets(FsEntry? cursor) =>
        _fsSelected.Count > 0 ? [.. _fsSelected] : cursor != null ? [cursor.Path] : [];

    // --- Drawing ------------------------------------------------------------------------------------

    void FileSystem()
    {
        if (!_fsViewLoaded)
            LoadView();
        if (!ImGui.Begin(FileSystemPanel, ref _showFileSystem))
        {
            _fsFocused = false;
            ImGui.End();
            return;
        }
        _fsFocused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
        if (!Directory.Exists(_project.Absolute(_fsDir)))
            _fsDir = ""; // its folder was moved or deleted

        var entries = Visible(_fsDir);
        // The selection is of what shows: an item a search, the filter or the relist hides leaves it
        if (_fsSelected.Count > 0)
            _fsSelected.IntersectWith(entries.Select(e => e.Path));
        if (_fsFocused && _fsInput == FsInput.None && !ImGui.GetIO().WantTextInput && !ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId))
        {
            FsKeys(entries);
            entries = Visible(_fsDir); // the keys may have changed the folder or its contents
        }
        var cursorIndex = CursorIn(entries, _fsDir);
        var cursor = entries.Count > 0 ? entries[cursorIndex] : null;

        Toolbar();
        var avail = ImGui.GetContentRegionAvail();
        var bottom = ImGui.GetFrameHeight() + 4;
        var height = Math.Max(60, avail.Y - bottom - ImGui.GetStyle().ItemSpacing.Y);
        const float gap = 6;
        var treeWidth = Math.Clamp(avail.X * 0.18f, 150, 280);
        var preview = _fsShowPreview && avail.X > 640;
        var previewWidth = preview ? Math.Clamp(avail.X * 0.30f, 220, 520) : 0;
        var contentWidth = avail.X - treeWidth - previewWidth - gap * (preview ? 2 : 1);

        FolderTree(treeWidth, height);
        ImGui.SameLine(0, gap);
        ContentView(entries, cursorIndex, contentWidth, height);
        if (preview)
        {
            ImGui.SameLine(0, gap);
            PreviewPane(cursor, previewWidth, height);
        }
        BottomBar(entries, cursor);

        FsInputBox(entries);
        Bookmarks();
        ImportDialog();
        FsConfirmDelete();
        FsHelpWindow();
        ImGui.End();
    }

    void Toolbar()
    {
        // Add and Import, Save All
        ImGui.PushStyleColor(ImGuiCol.Button, AccentDim);
        if (Icons.TextButton("Add", Icon.Plus, 0, ImGuiHost.Color(1, 1, 1)))
            ImGui.OpenPopup("fs-add");
        ImGui.PopStyleColor();
        if (ImGui.BeginPopup("fs-add"))
        {
            NewItems();
            ImGui.Separator();
            if (ImGui.MenuItem("Import files...")) StartImport();
            ImGui.EndPopup();
        }
        ImGui.SameLine();
        if (Icons.TextButton("Import", Icon.Import))
            StartImport();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Copy files from elsewhere into this folder");
        ImGui.SameLine();
        if (Icons.TextButton("Save All", Icon.Save))
        {
            SaveScene();
            _scripts.SaveAll();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Save the scene and every open script");

        // Back, forward, up
        ImGui.SameLine(0, 16);
        ImGui.BeginDisabled(_fsBack.Count == 0);
        if (Icons.Button("back", Icon.Back, Icons.FileColor, "Back (Alt+Left)")) Back();
        ImGui.EndDisabled();
        ImGui.SameLine(0, 2);
        ImGui.BeginDisabled(_fsForward.Count == 0);
        if (Icons.Button("forward", Icon.Forward, Icons.FileColor, "Forward (Alt+Right)")) Forward();
        ImGui.EndDisabled();
        ImGui.SameLine(0, 2);
        ImGui.BeginDisabled(_fsDir.Length == 0);
        if (Icons.Button("up", Icon.Up, Icons.FileColor, "Up a folder (Backspace)")) Leave();
        ImGui.EndDisabled();
        ImGui.SameLine(0, 2);
        if (Icons.Button("bookmarks", Icon.Star, Icons.Gold, "Bookmarks"))
            _fsBookmarksOpen = true;

        // The path: each part goes there; the arrow after it lists the folders in it
        ImGui.SameLine(0, 12);
        Icons.Inline(Icon.Folder, Icons.FolderColor);
        var parts = new List<(string Label, string Path)> { (_project.Name, "") };
        var acc = "";
        foreach (var part in _fsDir.Split('/', StringSplitOptions.RemoveEmptyEntries))
            parts.Add((part, acc = Join(acc, part)));
        foreach (var (label, path) in parts)
        {
            ImGui.SameLine(0, 4);
            ImGui.PushStyleColor(ImGuiCol.Button, float4.Zero);
            if (ImGui.Button(label + "##crumb" + path))
                Cd(path);
            FolderDropTarget(path);
            ImGui.SameLine(0, 0);
            if (ImGui.Button(">##sub" + path))
                ImGui.OpenPopup("sub" + path);
            ImGui.PopStyleColor();
            if (ImGui.BeginPopup("sub" + path))
            {
                var folders = Visible(path, forContent: false).Where(e => e.Folder).ToList();
                if (folders.Count == 0)
                    ImGui.TextDisabled("No folders");
                foreach (var f in folders)
                {
                    Icons.Inline(Icon.Folder, Icons.FolderColor);
                    ImGui.SameLine();
                    if (ImGui.Selectable(f.Name, f.Path == _fsDir))
                        Cd(f.Path);
                }
                ImGui.EndPopup();
            }
        }
        ImGui.SameLine(ImGui.GetWindowWidth() - 28);
        if (ImGui.SmallButton("?"))
            _fsHelp = true;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How to use it (F1)");
    }

    void FolderTree(float width, float height)
    {
        ImGui.BeginChild("tree", new float2(width, height), ImGuiChildFlags.Borders);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##paths", "Search folders", _fsPathSearch, ImGuiInputTextFlags.EscapeClearsAll);
        var filter = _fsPathSearch.ToString().Trim();
        HashSet<string>? shown = null;
        if (filter.Length > 0)
        {
            // Folders whose names match, and the folders above them
            shown = [""];
            foreach (var e in Everything("", 5000))
                if (e.Folder && e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    for (var p = e.Path; p.Length > 0 && shown.Add(p); p = Parent(p)) { }
        }
        TreeFolder(new FsEntry("", _project.Name, true, 0, default, false), shown);
        ImGui.EndChild();
    }

    void TreeFolder(FsEntry folder, HashSet<string>? shown)
    {
        var children = Visible(folder.Path, forContent: false).Where(e => e.Folder && (shown == null || shown.Contains(e.Path))).ToList();
        var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.AllowOverlap;
        if (children.Count == 0)
            flags |= ImGuiTreeNodeFlags.Leaf;
        if (folder.Path == _fsDir)
            flags |= ImGuiTreeNodeFlags.Selected;
        ImGui.SetNextItemOpen(shown != null || _fsTreeOpen.Contains(folder.Path));
        ImGui.PushID("t" + folder.Path);
        var selected = folder.Path == _fsDir;
        if (selected)
            ImGui.PushStyleColor(ImGuiCol.Header, AccentDim);
        var open = ImGui.TreeNodeEx("        " + folder.Name, flags);
        if (selected)
            ImGui.PopStyleColor();
        var min = ImGui.GetItemRectMin();
        var size = ImGui.GetTextLineHeight();
        Icons.Draw(ImGui.GetWindowDrawList(), open && children.Count > 0 ? Icon.FolderOpen : Icon.Folder,
            new float2(min.X + ImGui.GetTreeNodeToLabelSpacing() + 2, min.Y + (ImGui.GetItemRectMax().Y - min.Y - size) / 2), size, Icons.FolderColor);
        if (ImGui.IsItemToggledOpen())
        {
            if (open) _fsTreeOpen.Add(folder.Path);
            else _fsTreeOpen.Remove(folder.Path);
        }
        else if (ImGui.IsItemClicked())
            Cd(folder.Path);
        FolderDropTarget(folder.Path);
        if (folder.Path.Length > 0 && ImGui.BeginPopupContextItem("menu"))
        {
            FileMenu(folder);
            ImGui.EndPopup();
        }
        if (open)
        {
            foreach (var c in children)
                TreeFolder(c, shown);
            ImGui.TreePop();
        }
        ImGui.PopID();
    }

    void ContentView(List<FsEntry> entries, int cursorIndex, float width, float height)
    {
        // A group keeps the search row and the items in this column (lines would otherwise start at the dock's edge)
        ImGui.BeginGroup();
        // Filters and search, as the content browser's
        if (ImGui.Button((_fsKind == FsKind.All ? "Filters" : _fsKind.ToString()) + "##filters"))
            ImGui.OpenPopup("fs-filters");
        if (ImGui.BeginPopup("fs-filters"))
        {
            foreach (var kind in Enum.GetValues<FsKind>())
                if (ImGui.MenuItem(kind == FsKind.All ? "Everything" : kind.ToString(), default, _fsKind == kind))
                    _fsKind = kind;
            ImGui.EndPopup();
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(width - ImGui.GetItemRectSize().X - 8);
        if (_fsFocusSearch)
        {
            ImGui.SetKeyboardFocusHere();
            _fsFocusSearch = false;
        }
        var searchLabel = _fsDir.Length == 0 ? "Search " + _project.Name : "Search " + Path.GetFileName(_fsDir);
        if (ImGui.InputTextWithHint("##search", searchLabel + " and its folders (Ctrl+F)", _fsSearch, ImGuiInputTextFlags.EscapeClearsAll))
            _fsScrollToCursor = true;

        var boxHeight = height - ImGui.GetFrameHeight() - ImGui.GetStyle().ItemSpacing.Y;
        ImGui.BeginChild("content", new float2(width, boxHeight), ImGuiChildFlags.Borders);
        if (entries.Count == 0)
            ImGui.TextDisabled(_fsSearch.IsEmpty && _fsKind == FsKind.All ? "This folder is empty. Add or Import puts things here." : "Nothing matches.");
        var searching = !_fsSearch.IsEmpty;
        if (_fsView == FsView.Tiles)
            Tiles(entries, cursorIndex, searching);
        else
            ListRows(entries, cursorIndex, searching);

        // The space after the items: click clears the selection, drop moves here, right-click adds
        var space = ImGui.GetContentRegionAvail();
        if (space.Y > 4)
        {
            ImGui.InvisibleButton("##space", new float2(Math.Max(1, space.X), space.Y));
            if (ImGui.IsItemClicked())
                _fsSelected.Clear();
            FolderDropTarget(_fsDir);
            if (ImGui.BeginPopupContextItem("space"))
            {
                NewItems();
                if (ImGui.MenuItem("Import files...")) StartImport();
                if (ImGui.MenuItem("Paste", "Ctrl+V", false, _fsYank != null)) Paste(_fsDir);
                ImGui.EndPopup();
            }
        }
        ImGui.EndChild();
        ImGui.EndGroup();
    }

    /// <summary>Handles a click on an item (<paramref name="click"/> 1): plain selects it alone, Ctrl adds or removes it, Shift selects the range to it; a double-click (2) opens it.</summary>
    void Clicked(List<FsEntry> entries, int index, int click)
    {
        var io = ImGui.GetIO();
        var e = entries[index];
        if (click == 2)
            Open(e); // the first click already selected it
        else if (io.KeyShift)
            SetCursor(entries, index, extend: true);
        else if (io.KeyCtrl)
        {
            var current = entries[CursorIn(entries, _fsDir)].Path;
            if (_fsSelected.Count == 0)
                _fsSelected.Add(current); // the item that was current joins the selection
            if (!_fsSelected.Remove(e.Path))
                _fsSelected.Add(e.Path);
            _fsCursorIn[_fsDir] = e.Path;
            _fsAnchor = e.Path;
        }
        else
        {
            _fsSelected.Clear();
            _fsCursorIn[_fsDir] = e.Path;
            _fsAnchor = e.Path;
        }
    }

    void Tiles(List<FsEntry> entries, int cursorIndex, bool searching)
    {
        var style = ImGui.GetStyle();
        var line = ImGui.GetTextLineHeight();
        var tileWidth = _fsTileSize + 24;
        var tileHeight = _fsTileSize + line * 2 + 18;
        const float spacing = 8;
        var columns = Math.Max(1, (int)((ImGui.GetContentRegionAvail().X + spacing) / (tileWidth + spacing)));
        _fsColumns = columns;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new float2(spacing, spacing));
        for (var i = 0; i < entries.Count; i++)
        {
            if (i % columns != 0)
                ImGui.SameLine();
            var e = entries[i];
            var p = ImGui.GetCursorScreenPos();
            ImGui.PushID("tile" + e.Path);
            var clicked = ImGui.InvisibleButton("##tile", new float2(tileWidth, tileHeight));
            var hovered = ImGui.IsItemHovered();
            var dl = ImGui.GetWindowDrawList();
            var end = new float2(p.X + tileWidth, p.Y + tileHeight);
            var current = i == cursorIndex;
            var selected = _fsSelected.Contains(e.Path);
            dl.AddRectFilled(p, end, ImGui.GetColorU32(ImGuiHost.Color(0.11f, 0.12f, 0.15f)), 6);
            if (selected || current && _fsSelected.Count == 0)
                dl.AddRectFilled(p, end, ImGui.GetColorU32(AccentDim), 6);
            else if (hovered)
                dl.AddRectFilled(p, end, ImGui.GetColorU32(ImGuiCol.HeaderHovered, 0.7f), 6);
            if (current)
                dl.AddRect(p, end, ImGui.GetColorU32(Accent), 6, default, 2);

            // The picture: a thumbnail for images, otherwise the kind's icon (also while it decodes, or
            // if it can't). Only tiles on screen ask for one, so a big folder doesn't decode them all.
            var art = new float2(p.X + 12, p.Y + 8);
            var artEnd = new float2(art.X + _fsTileSize, art.Y + _fsTileSize);
            if (IsImage(e.Name) && ImGui.IsItemVisible() && Thumbnail(e, 160) is { Texture: { } tex, Ref: { } texRef })
            {
                var (tw, th) = (tex.Width, tex.Height);
                var scale = Math.Min(_fsTileSize / tw, _fsTileSize / th);
                var w = tw * scale;
                var h = th * scale;
                var at = new float2(art.X + (_fsTileSize - w) / 2, art.Y + (_fsTileSize - h) / 2);
                dl.AddImageRounded(texRef, at, new float2(at.X + w, at.Y + h), float2.Zero, float2.One, ImGui.GetColorU32(ImGuiHost.Color(1, 1, 1)), 4);
            }
            else
            {
                var (icon, color) = e.Folder ? (Icon.Folder, Icons.FolderColor) : Icons.ForFile(e.Name);
                var iconSize = _fsTileSize * 0.62f;
                Icons.Draw(dl, icon, new float2(art.X + (_fsTileSize - iconSize) / 2, art.Y + (_fsTileSize - iconSize) / 2), iconSize, color);
            }
            YankMarker(dl, e, new float2(end.X - 12, p.Y + 10));

            // The name (up to two lines) and the kind
            var name = DisplayName(e);
            var textWidth = tileWidth - 10;
            var (first, second) = SplitName(name, textWidth);
            var y = artEnd.Y + 4;
            var nameColor = ImGui.GetColorU32(current || selected ? ImGuiHost.Color(1, 1, 1) : ImGuiHost.Color(0.86f, 0.87f, 0.90f));
            CenteredText(dl, first, p.X, tileWidth, y, nameColor);
            if (second.Length > 0)
                CenteredText(dl, second, p.X, tileWidth, y + line, nameColor);
            else
                CenteredText(dl, KindLabel(e), p.X, tileWidth, y + line, ImGui.GetColorU32(Icons.Dim));
            if (hovered)
                ImGui.SetTooltip(searching ? $"{e.Path}\n{KindLabel(e)}{(e.Folder ? "" : ", " + FormatSize(e.Size))}" : $"{e.Name}\n{KindLabel(e)}{(e.Folder ? "" : ", " + FormatSize(e.Size))}");

            ItemInteractions(e, IconFor(e));
            // The button reports a click when the mouse is let go; a double-click shows on the second press
            if (clicked)
                Clicked(entries, i, 1);
            if (hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                Clicked(entries, i, 2);
            if (current && _fsScrollToCursor)
            {
                _fsScrollToCursor = false;
                ImGui.SetScrollHereY(0.5f);
            }
            ImGui.PopID();
        }
        ImGui.PopStyleVar();
    }

    static (string, string) SplitName(string name, float width)
    {
        if (ImGui.CalcTextSize(name).X <= width)
            return (name, "");
        // Break at the last space, underscore or dash that fits; otherwise at a character
        var cut = name.Length;
        while (cut > 1 && ImGui.CalcTextSize(name[..cut]).X > width)
            cut--;
        var soft = name.LastIndexOfAny([' ', '_', '-', '.'], cut - 1);
        if (soft > cut / 2)
            cut = soft + 1;
        return (name[..cut].TrimEnd(), Fit(name[cut..].TrimStart(), width));
    }

    static void CenteredText(ImDrawListPtr dl, string text, float x, float width, float y, ImColor8 color)
    {
        var w = ImGui.CalcTextSize(text).X;
        dl.AddText(new float2(x + (width - w) / 2, y), color, text);
    }

    (Icon, float4) IconFor(FsEntry e) => e.Folder ? (Icon.Folder, Icons.FolderColor) : Icons.ForFile(e.Name);

    void YankMarker(ImDrawListPtr dl, FsEntry e, float2 at)
    {
        if (_fsYank is not { } y || !y.Paths.Contains(e.Path))
            return;
        dl.AddCircleFilled(at, 5, ImGui.GetColorU32(y.Cut ? ImGuiHost.Color(0.93f, 0.40f, 0.36f) : ImGuiHost.Color(0.45f, 0.82f, 0.50f)));
    }

    void ListRows(List<FsEntry> entries, int cursorIndex, bool searching)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var rowHeight = ImGui.GetFrameHeight() + 1;
        _fsColumns = 1;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new float2(0, 1));
        // Column titles: click one to sort by it (again to reverse)
        var dateX = width - 150;
        var sizeX = width - 230;
        var kindX = width - 300;
        ImGui.TextDisabled("    Name");
        void Header(string label, float x, FsSort sort)
        {
            ImGui.SameLine(x);
            if (ImGui.SmallButton(label + (_fsSort == sort ? (_fsReverse ? " v" : " ^") : "")))
            {
                (_fsSort, _fsReverse) = (sort, _fsSort == sort && !_fsReverse);
                SaveView();
            }
        }
        if (width > 420)
        {
            Header("Type", kindX, FsSort.Extension);
            Header("Size", sizeX, FsSort.Size);
            Header("Modified", dateX, FsSort.Modified);
        }
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var p = ImGui.GetCursorScreenPos();
            var h = ImGui.GetFrameHeight();
            ImGui.PushID("row" + e.Path);
            var clicked = ImGui.InvisibleButton("##row", new float2(Math.Max(1, width - 2), h));
            var hovered = ImGui.IsItemHovered();
            var dl = ImGui.GetWindowDrawList();
            var end = new float2(p.X + width - 2, p.Y + h);
            var current = i == cursorIndex;
            var selected = _fsSelected.Contains(e.Path);
            if (selected || current && _fsSelected.Count == 0)
                dl.AddRectFilled(p, end, ImGui.GetColorU32(AccentDim), 3);
            else if (hovered)
                dl.AddRectFilled(p, end, ImGui.GetColorU32(ImGuiCol.HeaderHovered, 0.6f), 3);
            if (current)
                dl.AddRect(p, end, ImGui.GetColorU32(Accent), 3);
            var (icon, color) = IconFor(e);
            var size = ImGui.GetTextLineHeight();
            Icons.Draw(dl, icon, new float2(p.X + 8, p.Y + (h - size) / 2), size, color);
            var textY = p.Y + (h - ImGui.GetTextLineHeight()) / 2;
            var label = searching ? e.Path : e.Name;
            var nameEnd = width > 420 ? p.X + kindX - 8 : end.X - 8;
            dl.AddText(new float2(p.X + 14 + size, textY), ImGui.GetColorU32(e.Folder ? Icons.FolderColor : e.Hidden ? Icons.Dim : ImGuiHost.Color(0.86f, 0.87f, 0.90f)), Fit(label, nameEnd - p.X - 20 - size));
            if (width > 420)
            {
                var dim = ImGui.GetColorU32(Icons.Dim);
                dl.AddText(new float2(p.X + kindX, textY), dim, KindLabel(e));
                if (!e.Folder)
                    dl.AddText(new float2(p.X + sizeX, textY), dim, FormatSize(e.Size));
                dl.AddText(new float2(p.X + dateX, textY), dim, e.Modified.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
            }
            YankMarker(dl, e, new float2(p.X + 3, p.Y + h / 2));
            ItemInteractions(e, (icon, color));
            if (clicked)
                Clicked(entries, i, 1);
            if (hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                Clicked(entries, i, 2);
            ImGui.PopID();
        }
        ImGui.PopStyleVar();
        // Keep the current row in view, with a few rows of margin
        if (_fsScrollToCursor && entries.Count > 0)
        {
            _fsScrollToCursor = false;
            var top = (cursorIndex + 1) * rowHeight;
            var view = ImGui.GetWindowHeight();
            var margin = Math.Min(5, (int)(view / rowHeight / 2)) * rowHeight;
            var scroll = ImGui.GetScrollY();
            if (top - margin < scroll)
                ImGui.SetScrollY(Math.Max(0, top - margin));
            else if (top + rowHeight + margin > scroll + view)
                ImGui.SetScrollY(top + rowHeight + margin - view);
        }
    }

    /// <summary>Dragging an item (into the viewport, onto a node, an asset field or a folder), dropping onto a folder, and its menu.</summary>
    void ItemInteractions(FsEntry e, (Icon Icon, float4 Color) look)
    {
        if (ImGui.BeginDragDropSource())
        {
            _draggedAsset = e.Path;
            ImGui.SetDragDropPayload(AssetPayload, default, 0);
            Icons.Inline(look.Icon, look.Color);
            ImGui.SameLine();
            ImGui.Text(e.Name);
            ImGui.EndDragDropSource();
        }
        if (e.Folder)
            FolderDropTarget(e.Path);
        if (ImGui.BeginPopupContextItem("menu"))
        {
            if (!_fsSelected.Contains(e.Path))
            {
                _fsSelected.Clear();
                _fsCursorIn[_fsDir] = e.Path;
            }
            FileMenu(e);
            ImGui.EndPopup();
        }
    }

    void FolderDropTarget(string folder)
    {
        if (!ImGui.BeginDragDropTarget())
            return;
        if (!ImGui.AcceptDragDropPayload(AssetPayload).IsNull())
        {
            var moving = _draggedAsset;
            var to = Join(folder, Path.GetFileName(moving));
            if (moving == folder || Parent(moving) == folder || folder.StartsWith(moving + "/", StringComparison.Ordinal))
            { } // already there, or into itself
            else if (File.Exists(_project.Absolute(to)) || Directory.Exists(_project.Absolute(to)))
                Log.Warn("editor", $"{(folder.Length == 0 ? _project.Name : folder)} already has a {Path.GetFileName(moving)}; not moved");
            else
                _afterTree = () => MoveFile(moving, to);
        }
        ImGui.EndDragDropTarget();
    }

    internal static string FormatSize(long bytes) =>
        bytes < 1024 ? $"{bytes}B"
        : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#}K"
        : bytes < 1024L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024):0.#}M"
        : $"{bytes / (1024.0 * 1024 * 1024):0.#}G";

    void BottomBar(List<FsEntry> entries, FsEntry? cursor)
    {
        var count = $"{entries.Count} item{(entries.Count == 1 ? "" : "s")}";
        if (_fsSelected.Count > 0) count += $", {_fsSelected.Count} selected";
        if (_fsYank is { } yank) count += $", {yank.Paths.Count} {(yank.Cut ? "cut" : "copied")} (Ctrl+V pastes)";
        if (!_fsSearch.IsEmpty) count += $"   searching for \"{_fsSearch.ToString().Trim()}\"";
        ImGui.TextDisabled(count);
        if (cursor != null && !cursor.Folder)
        {
            ImGui.SameLine(0, 24);
            ImGui.TextDisabled($"{cursor.Name}   {FormatSize(cursor.Size)}   {cursor.Modified.ToLocalTime():yyyy-MM-dd HH:mm}");
        }
        ImGui.SameLine(ImGui.GetWindowWidth() - 130);
        if (ImGui.SmallButton("View Options"))
            ImGui.OpenPopup("fs-view");
        if (ImGui.BeginPopup("fs-view"))
        {
            var changed = false;
            ImGui.TextDisabled("View");
            if (ImGui.RadioButton("Tiles", _fsView == FsView.Tiles)) { _fsView = FsView.Tiles; changed = true; }
            ImGui.SameLine();
            if (ImGui.RadioButton("List", _fsView == FsView.List)) { _fsView = FsView.List; changed = true; }
            if (_fsView == FsView.Tiles)
            {
                ImGui.SetNextItemWidth(160);
                changed |= ImGui.SliderFloat("Tile size", ref _fsTileSize, 48, 160, "%.0f");
            }
            changed |= ImGui.Checkbox("Preview", ref _fsShowPreview);
            changed |= ImGui.Checkbox("Hidden files", ref _fsShowHidden);
            ImGui.Separator();
            ImGui.TextDisabled("Sort by");
            foreach (var sort in Enum.GetValues<FsSort>())
                if (ImGui.RadioButton(sort.ToString(), _fsSort == sort)) { _fsSort = sort; changed = true; }
            changed |= ImGui.Checkbox("Reversed", ref _fsReverse);
            ImGui.Separator();
            if (ImGui.MenuItem("How to use it...")) _fsHelp = true;
            ImGui.EndPopup();
            if (changed)
                SaveView();
        }
    }

    // --- Preview --------------------------------------------------------------------------------------

    static readonly HashSet<string> TextFiles =
        [".cs", ".json", ".md", ".txt", ".glsl", ".vert", ".frag", ".comp", ".toml", ".csproj", ".props", ".targets", ".xml", ".yml", ".yaml", ".ini", ".sh", ".py"];

    static bool IsImage(string name) => Path.GetExtension(name).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tga";

    void PreviewPane(FsEntry? e, float width, float height)
    {
        ImGui.BeginChild("preview", new float2(width, height), ImGuiChildFlags.Borders);
        if (e == null)
        {
            ImGui.TextDisabled("Nothing selected");
            ImGui.EndChild();
            return;
        }
        var (icon, color) = IconFor(e);
        Icons.Inline(icon, color);
        ImGui.SameLine();
        ImGui.Text(Fit(e.Name, width - 40));
        ImGui.TextDisabled(e.Folder ? $"Folder, {Visible(e.Path, forContent: false).Count} items" : $"{KindLabel(e)}, {FormatSize(e.Size)}, {e.Modified.ToLocalTime():yyyy-MM-dd HH:mm}");
        ImGui.Separator();
        if (e.Folder)
        {
            var inside = Visible(e.Path, forContent: false);
            if (inside.Count == 0)
                ImGui.TextDisabled("Empty folder");
            foreach (var c in inside.Take(200))
            {
                var (ci, cc) = IconFor(c);
                Icons.Inline(ci, cc);
                ImGui.SameLine();
                if (ImGui.Selectable(c.Name + "##p" + c.Path, false, ImGuiSelectableFlags.AllowDoubleClick))
                {
                    Cd(e.Path);
                    _fsCursorIn[e.Path] = c.Path;
                    if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                        Open(c);
                }
            }
        }
        else if (IsImage(e.Name))
            ImagePreview(e, width);
        else if (e.Name.EndsWith(".scene.json", StringComparison.Ordinal))
            ScenePreview(e);
        else if (IsModel(e.Name))
            ModelPreview(e);
        else if (TextFiles.Contains(Path.GetExtension(e.Name).ToLowerInvariant()) || e.Name.StartsWith('.') || Path.GetExtension(e.Name).Length == 0)
            TextPreview(e);
        else
            ImGui.TextDisabled("No preview for this kind of file");
        ImGui.EndChild();
    }

    // Text: the first part of the file, in the code font; C# in the script editor's colours
    readonly Dictionary<string, (DateTime Stamp, string[] Lines)> _textPreviews = [];

    void TextPreview(FsEntry e)
    {
        if (!_textPreviews.TryGetValue(e.Path, out var cached) || cached.Stamp != e.Modified)
        {
            if (_textPreviews.Count > 32)
                _textPreviews.Clear();
            string[] lines;
            try
            {
                using var stream = File.OpenRead(_project.Absolute(e.Path));
                var buffer = new byte[Math.Min(stream.Length, 64 * 1024)];
                var read = stream.Read(buffer);
                var text = Encoding.UTF8.GetString(buffer, 0, read);
                lines = text.Contains('\0') ? ["(binary file)"] : text.Replace("\t", "  ").Split('\n').Take(400).Select(l => l.TrimEnd('\r')).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lines = [$"(can't read it: {ex.Message})"];
            }
            _textPreviews[e.Path] = cached = (e.Modified, lines);
        }
        ImGui.PushFont(_ui.Mono, 15f);
        var dl = ImGui.GetWindowDrawList();
        var csharp = e.Name.EndsWith(".cs", StringComparison.Ordinal);
        var state = LexState.Normal;
        var tokens = new List<Token>();
        var fontSize = ImGui.GetFontSize();
        // Measured as the script editor does: the font's own advance at this size
        var charWidth = _ui.Mono.CalcTextSizeA(fontSize, float.MaxValue, 0, "MMMMMMMMMM").X / 10;
        var plain = ImGui.GetColorU32(ImGuiHost.Color(0.80f, 0.82f, 0.86f));
        foreach (var line in cached.Lines)
        {
            var p = ImGui.GetCursorScreenPos();
            if (csharp)
            {
                tokens.Clear();
                state = CSharpLexer.Line(line, state, tokens);
                foreach (var t in tokens)
                    dl.AddText(_ui.Mono, fontSize, new float2(p.X + 4 + t.Start * charWidth, p.Y), CodeEditor.TokenColors[(int)t.Kind], line.AsSpan(t.Start, t.Length));
            }
            else
                dl.AddText(_ui.Mono, fontSize, new float2(p.X + 4, p.Y), plain, line);
            ImGui.Dummy(new float2(4 + line.Length * charWidth, ImGui.GetTextLineHeight()));
        }
        ImGui.PopFont();
    }

    // Scenes: their nodes as a tree, with what each is
    readonly Dictionary<string, (DateTime Stamp, SceneDocument? Doc, string Problem)> _scenePreviews = [];

    void ScenePreview(FsEntry e)
    {
        if (!_scenePreviews.TryGetValue(e.Path, out var cached) || cached.Stamp != e.Modified)
        {
            if (_scenePreviews.Count > 16)
                _scenePreviews.Clear();
            try
            {
                var (doc, problems) = SceneLibrary.Parse(File.ReadAllText(_project.Absolute(e.Path)), e.Path);
                cached = (e.Modified, doc, problems.Ok ? "" : problems.Errors.FirstOrDefault() ?? "");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                cached = (e.Modified, null, ex.Message);
            }
            _scenePreviews[e.Path] = cached;
        }
        if (cached.Doc is not { } d)
        {
            ImGui.TextColored(Warning, cached.Problem.Length > 0 ? cached.Problem : "can't read this scene");
            return;
        }
        ImGui.TextDisabled($"{d.Nodes.Count} node{(d.Nodes.Count == 1 ? "" : "s")}   (double-click or Enter opens it)");
        var depth = new Dictionary<long, int>();
        foreach (var n in d.Nodes.Take(300))
        {
            var level = n.Parent != 0 && depth.TryGetValue(n.Parent, out var pd) ? pd + 1 : 0;
            depth[n.Id] = level;
            ImGui.SetCursorPosX(8 + level * 14);
            var c = n.Components ?? [];
            var icon = c.ContainsKey("Camera") ? Icon.Camera : c.ContainsKey("DirectionalLight") ? Icon.Light
                : c.ContainsKey("MeshRenderer") ? (c.ContainsKey("Animator") ? Icon.Character : Icon.Mesh)
                : n.Instance != null ? Icon.Instance : Icon.Node;
            Icons.Inline(icon, Icons.NodeColor);
            ImGui.SameLine();
            ImGui.Text(n.Name);
            if (n.Instance != null)
            {
                ImGui.SameLine();
                ImGui.TextDisabled(Path.GetFileName(n.Instance));
            }
        }
    }

    // Models: read on a worker thread for their numbers only; nothing goes to the GPU until one is placed
    sealed record ModelFacts(int Triangles, int Vertices, int Materials, int Textures, bool Rigged, int Bones, int FarTriangles, BakedClip[] Clips)
    {
        public static ModelFacts Of(SkinnedModel m) => new(
            m.Submeshes.Where(s => s.Lod == 0).Sum(s => s.IndexCount) / 3, m.Vertices.Length, m.Materials.Count, m.Textures.Count,
            m.IsRigged, m.JointCount, m.Lod1.IndexCount / 3, [.. m.Clips]);
    }

    readonly Dictionary<string, (DateTime Stamp, Task<ModelFacts> Reading)> _modelPreviews = [];

    void ModelPreview(FsEntry e)
    {
        if (!_modelPreviews.TryGetValue(e.Path, out var cached) || cached.Stamp != e.Modified)
        {
            if (_modelPreviews.Count > 16)
                _modelPreviews.Clear();
            var file = _project.Absolute(e.Path);
            cached = (e.Modified, InTurn(() => ModelFacts.Of(new SkinnedModel(file))));
            cached.Reading.ContinueWith(_ => PreviewReady());
            _modelPreviews[e.Path] = cached;
        }
        if (!cached.Reading.IsCompleted)
        {
            ImGui.TextDisabled("loading...");
            return;
        }
        if (cached.Reading.IsFaulted)
        {
            ImGui.TextColored(Warning, $"can't load it: {cached.Reading.Exception?.InnerException?.Message ?? "unknown error"}");
            return;
        }
        var m = cached.Reading.Result;
        ImGui.Text($"{m.Triangles:N0} triangles, {m.Vertices:N0} vertices");
        ImGui.Text($"{m.Materials} material{(m.Materials == 1 ? "" : "s")}, {m.Textures} texture{(m.Textures == 1 ? "" : "s")}");
        if (m.Rigged)
        {
            ImGui.Text($"{m.Bones} bones, far version {m.FarTriangles:N0} triangles");
            ImGui.Text("Clips:");
            foreach (var clip in m.Clips)
                ImGui.TextDisabled($"  {clip.Name}  {clip.Duration:0.00} s");
        }
        else
            ImGui.TextDisabled("Static model");
        ImGui.Spacing();
        ImGui.TextDisabled("Double-click or Enter places it; or drag it into the viewport");
    }

    // Images: decoded on a worker thread, shrunk, uploaded; recently used ones are kept (tiles show many)
    sealed class ImagePreviewEntry
    {
        public DateTime Stamp;
        public Task<(int W, int H, byte[] Rgba, int SourceW, int SourceH)>? Decoding;
        public Texture? Texture;
        public ImTextureRef? Ref;
        public int SourceW, SourceH;
        public string Error = "";
        public double Used;
        public volatile bool Dropped; // out of the cache: a decode still waiting for its turn is skipped
    }

    readonly Dictionary<string, ImagePreviewEntry> _images = [];
    const int MaxImages = 96;
    static readonly SemaphoreSlim PreviewTurns = new(2);

    /// <summary>A preview's work on a worker thread, two at a time, so a folder of images or models doesn't fill the thread pool.</summary>
    static Task<T> InTurn<T>(Func<T> work) => Task.Run(async () =>
    {
        await PreviewTurns.WaitAsync();
        try
        {
            return work();
        }
        finally
        {
            PreviewTurns.Release();
        }
    });

    volatile bool _previewReady;

    /// <summary>A preview finished on a worker thread: the next frame draws it (an idle editor draws only when something changed).</summary>
    void PreviewReady()
    {
        _previewReady = true;
        Window.Wake();
    }

    /// <summary>A decoded, uploaded picture of an image file at most <paramref name="max"/> pixels across, or null while it decodes.</summary>
    ImagePreviewEntry? Thumbnail(FsEntry e, int max)
    {
        var key = e.Path + "@" + max;
        if (!_images.TryGetValue(key, out var img) || img.Stamp != e.Modified)
        {
            if (img != null)
                ReleaseImage(img);
            var file = _project.Absolute(e.Path);
            var entry = img = new ImagePreviewEntry { Stamp = e.Modified, Used = ImGui.GetTime() }; // used now, so the trim keeps it
            img.Decoding = InTurn(() =>
            {
                if (entry.Dropped)
                    throw new OperationCanceledException(); // scrolled past before its turn came
                return DecodeSmall(file, max);
            });
            img.Decoding.ContinueWith(_ => PreviewReady());
            _images[key] = img;
            TrimImages();
        }
        img.Used = ImGui.GetTime();
        if (img.Decoding is { IsCompleted: true } done)
        {
            img.Decoding = null;
            if (!done.IsCompletedSuccessfully)
                img.Error = done.Exception?.InnerException?.Message ?? "can't read it";
            else
            {
                var (w, h, rgba, sw, sh) = done.Result;
                img.Texture = new Texture(_gpu, w, h, rgba);
                img.Ref = _ui.AddTexture(img.Texture.Sampler, img.Texture.View);
                (img.SourceW, img.SourceH) = (sw, sh);
            }
        }
        return img.Texture != null || img.Error.Length > 0 ? img : null;
    }

    void ImagePreview(FsEntry e, float width)
    {
        var img = Thumbnail(e, 512);
        if (img == null)
            ImGui.TextDisabled("loading...");
        else if (img.Error.Length > 0)
            ImGui.TextColored(Warning, img.Error);
        else if (img.Ref is { } texture && img.Texture is { } t)
        {
            var scale = Math.Min((width - 16) / t.Width, 2f);
            ImGui.Image(texture, new float2(t.Width * scale, t.Height * scale));
            ImGui.TextDisabled($"{img.SourceW} x {img.SourceH} pixels");
        }
    }

    /// <summary>An image file at most <paramref name="max"/> pixels across, box-filtered down, for a preview.</summary>
    internal static (int, int, byte[], int, int) DecodeSmall(string file, int max)
    {
        using var image = Stb.Load(file, desiredChannels: 4);
        if (image.IsNull())
            throw new InvalidDataException("not an image this can read");
        int sw = image.Width, sh = image.Height;
        var source = image.AsSpan();
        var scale = Math.Max(1, (int)MathF.Ceiling(Math.Max(sw, sh) / (float)max));
        int w = Math.Max(1, sw / scale), h = Math.Max(1, sh / scale);
        var rgba = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                int r = 0, g = 0, b = 0, a = 0, n = 0;
                for (var dy = 0; dy < scale && y * scale + dy < sh; dy++)
                    for (var dx = 0; dx < scale && x * scale + dx < sw; dx++)
                    {
                        var i = ((y * scale + dy) * sw + x * scale + dx) * 4;
                        r += source[i]; g += source[i + 1]; b += source[i + 2]; a += source[i + 3];
                        n++;
                    }
                var o = (y * w + x) * 4;
                rgba[o] = (byte)(r / n); rgba[o + 1] = (byte)(g / n); rgba[o + 2] = (byte)(b / n); rgba[o + 3] = (byte)(a / n);
            }
        return (w, h, rgba, sw, sh);
    }

    void TrimImages()
    {
        while (_images.Count > MaxImages)
        {
            var oldest = _images.MinBy(kv => kv.Value.Used);
            if (oldest.Value.Used >= ImGui.GetTime())
                break; // every one is on screen this frame: keep them until some aren't
            ReleaseImage(oldest.Value);
            _images.Remove(oldest.Key);
        }
    }

    void ReleaseImage(ImagePreviewEntry img)
    {
        img.Dropped = true;
        var (texture, reference) = (img.Texture, img.Ref);
        img.Texture = null;
        img.Ref = null;
        if (texture == null)
            return;
        // Frames in flight may still draw it
        _gpu.Release(() =>
        {
            if (reference is { } r)
                _ui.RemoveTexture(r);
            texture.Dispose();
        });
    }

    /// <summary>At shutdown, with the GPU idle: frees every preview now, while the UI that owns their descriptors still exists.</summary>
    void ReleaseImages()
    {
        foreach (var img in _images.Values)
        {
            if (img.Ref is { } r)
                _ui.RemoveTexture(r);
            img.Texture?.Dispose();
            img.Texture = null;
            img.Ref = null;
        }
        _images.Clear();
    }

    // --- Keys -----------------------------------------------------------------------------------------

    /// <summary>The dock's keys: the usual ones of a desktop file browser.</summary>
    void FsKeys(List<FsEntry> entries)
    {
        var io = ImGui.GetIO();
        var cursorIndex = CursorIn(entries, _fsDir);
        var cursor = entries.Count > 0 ? entries[cursorIndex] : null;
        static bool Key(ImGuiKey k) => ImGui.IsKeyPressed(k);
        var tiles = _fsView == FsView.Tiles;
        var row = tiles ? _fsColumns : 1;
        var shift = io.KeyShift;

        // Arrows move over the tiles (left and right along a row, up and down by rows) or the list
        if (io.KeyAlt)
        {
            if (Key(ImGuiKey.LeftArrow)) Back();
            if (Key(ImGuiKey.RightArrow)) Forward();
            if (Key(ImGuiKey.UpArrow)) Leave();
            return;
        }
        if (Key(ImGuiKey.DownArrow)) SetCursor(entries, cursorIndex + row, shift);
        if (Key(ImGuiKey.UpArrow)) SetCursor(entries, cursorIndex - row, shift);
        if (tiles)
        {
            if (Key(ImGuiKey.RightArrow)) SetCursor(entries, cursorIndex + 1, shift);
            if (Key(ImGuiKey.LeftArrow)) SetCursor(entries, cursorIndex - 1, shift);
        }
        else
        {
            if (Key(ImGuiKey.LeftArrow)) Leave();
            if (Key(ImGuiKey.RightArrow) && cursor is { Folder: true }) Cd(cursor.Path);
        }
        if (Key(ImGuiKey.PageDown) || io.KeyCtrl && Key(ImGuiKey.D)) SetCursor(entries, cursorIndex + Math.Max(row, 10), shift);
        if (Key(ImGuiKey.PageUp) || io.KeyCtrl && Key(ImGuiKey.U)) SetCursor(entries, cursorIndex - Math.Max(row, 10), shift);
        if (Key(ImGuiKey.Home)) SetCursor(entries, 0, shift);
        if (Key(ImGuiKey.End)) SetCursor(entries, entries.Count - 1, shift);
        if (Key(ImGuiKey.Backspace)) Leave();
        if ((Key(ImGuiKey.Enter) || Key(ImGuiKey.KeypadEnter)) && cursor != null) Open(cursor);
        if (Key(ImGuiKey.Delete) && Targets(cursor) is { Count: > 0 } toTrash) AskTrash(toTrash);
        if (Key(ImGuiKey.F2) && cursor != null) StartInput(FsInput.Rename, cursor.Name, cursor.Path);
        if (Key(ImGuiKey.F1)) _fsHelp = true;
        if (Key(ImGuiKey.Escape))
        {
            if (!_fsSearch.IsEmpty) _fsSearch.SetValue("".AsSpan());
            else _fsSelected.Clear();
        }
        if (io.KeyCtrl)
        {
            // The usual desktop shortcuts
            if (Key(ImGuiKey.A)) foreach (var e in entries) _fsSelected.Add(e.Path);
            if (Key(ImGuiKey.C) && Targets(cursor) is { Count: > 0 } copy) Yank(copy, cut: false);
            if (Key(ImGuiKey.X) && Targets(cursor) is { Count: > 0 } cut) Yank(cut, cut: true);
            if (Key(ImGuiKey.V)) Paste(_fsDir);
            if (Key(ImGuiKey.F)) _fsFocusSearch = true;
        }

    }

    static void Copy(string text)
    {
        ImGui.SetClipboardText(text);
        Log.Info("editor", $"copied {text}");
    }

    void Yank(List<string> paths, bool cut)
    {
        _fsYank = (paths, cut);
        _fsSelected.Clear();
    }

    void ToggleBookmark(string path)
    {
        if (!_favorites.Remove(path))
            _favorites.Add(path);
        SaveFavorites();
        Log.Info("editor", _favorites.Contains(path) ? $"bookmarked {path} (the star in the toolbar lists bookmarks)" : $"removed the bookmark on {path}");
    }

    void Leave()
    {
        if (_fsDir.Length == 0)
            return;
        var from = _fsDir;
        Cd(Parent(_fsDir));
        _fsCursorIn[_fsDir] = from;
    }

    /// <summary>Double-click or Enter: into a folder, or open a file (a scene in the editor, a script in the script editor, a model placed in the scene, anything else with the desktop's app).</summary>
    void Open(FsEntry e)
    {
        if (e.Folder)
            Cd(e.Path);
        else
            ActivateFile(e.Path);
    }

    void ActivateFile(string path)
    {
        if (path.EndsWith(".scene.json", StringComparison.Ordinal))
            _afterTree = () => OpenScene(path);
        else if (path.EndsWith(".cs", StringComparison.Ordinal))
            _pendingScript = (_project.Absolute(path), -1);
        else if (IsModel(path))
            _afterTree = () => Place(path, NewNodePosition(), _scene.Primary);
        else
            OnDesktop(path);
    }

    /// <summary>Opens a file or folder with the desktop's app; without xdg-open (xdg-utils) it says so instead of closing the editor.</summary>
    void OnDesktop(string path) =>
        Safely(() => Process.Start(new ProcessStartInfo("xdg-open", [_project.Absolute(path)]) { UseShellExecute = false })?.Dispose());

    // --- View settings --------------------------------------------------------------------------------

    void LoadView()
    {
        _fsViewLoaded = true;
        var v = _settings.Get("filesystem", EditorJson.Default.DictionaryStringString);
        if (v.GetValueOrDefault("view") is { } view && Enum.TryParse<FsView>(view, out var fv)) _fsView = fv;
        if (v.GetValueOrDefault("sort") is { } sort && Enum.TryParse<FsSort>(sort, out var fs)) _fsSort = fs;
        if (v.GetValueOrDefault("tile") is { } tile && float.TryParse(tile, System.Globalization.CultureInfo.InvariantCulture, out var ft)) _fsTileSize = Math.Clamp(ft, 48, 160);
        _fsReverse = v.GetValueOrDefault("reverse") == "true";
        _fsShowHidden = v.GetValueOrDefault("hidden") == "true";
        _fsShowPreview = v.GetValueOrDefault("preview") != "false";
    }

    void SaveView()
    {
        _settings.Set("filesystem", new Dictionary<string, string>
        {
            ["view"] = _fsView.ToString(),
            ["sort"] = _fsSort.ToString(),
            ["tile"] = _fsTileSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["reverse"] = _fsReverse ? "true" : "false",
            ["hidden"] = _fsShowHidden ? "true" : "false",
            ["preview"] = _fsShowPreview ? "true" : "false",
        }, EditorJson.Default.DictionaryStringString);
        _settings.Save();
    }

    // --- Operations ----------------------------------------------------------------------------------

    /// <summary>Paste: copies (or moves, after a cut) what was copied into a folder; a name that's taken gets _1, _2 and so on.</summary>
    void Paste(string folder)
    {
        if (_fsYank is not { } yank)
            return;
        _afterTree = () =>
        {
            foreach (var from in yank.Paths)
            {
                if (!File.Exists(_project.Absolute(from)) && !Directory.Exists(_project.Absolute(from)))
                    continue;
                if (yank.Cut && (Parent(from) == folder || folder == from || folder.StartsWith(from + "/", StringComparison.Ordinal)))
                    continue; // already here, or into itself
                if (!yank.Cut && (folder == from || folder.StartsWith(from + "/", StringComparison.Ordinal)))
                {
                    Log.Warn("editor", $"can't copy {from} into itself");
                    continue;
                }
                var to = ProjectFiles.FreeName(_project.Root, folder, Path.GetFileName(from));
                if (yank.Cut)
                    MoveFile(from, to);
                else
                {
                    ProjectFiles.Copy(_project.Absolute(from), _project.Absolute(to));
                    Log.Info("editor", $"copied {from} to {to}");
                }
                _fsCursorIn[folder] = to;
            }
            if (yank.Cut)
                _fsYank = null;
            FsRefresh();
        };
    }

    void StartImport()
    {
        _fsImport.StartFiles($"Import into {(_fsDir.Length == 0 ? _project.Name : _fsDir)}: tick files (from any folder)",
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        _fsImportOpen = true;
    }

    void ImportDialog()
    {
        if (_fsImportOpen)
        {
            ImGui.OpenPopup("fs-import");
            _fsImportOpen = false;
        }
        if (!ImGui.BeginPopupModal("fs-import", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar))
            return;
        var result = _fsImport.DrawInline();
        if (result != null)
        {
            ImGui.CloseCurrentPopup();
            if (result == "files")
            {
                var folder = _fsDir;
                var files = _fsImport.Files.ToList();
                _afterTree = () =>
                {
                    foreach (var file in files)
                    {
                        var to = ProjectFiles.FreeName(_project.Root, folder, Path.GetFileName(file));
                        File.Copy(file, _project.Absolute(to));
                        _fsCursorIn[folder] = to;
                        Log.Info("editor", $"imported {Path.GetFileName(file)} as {to}");
                    }
                    FsRefresh();
                };
            }
        }
        ImGui.EndPopup();
    }

    void AskTrash(List<string> paths)
    {
        if (paths.Count == 0)
            return;
        var usedBy = paths.SelectMany(p => ProjectFiles.ReferencesTo(_library, p)).Where(s => !paths.Contains(s)).Distinct().ToList();
        _fsDelete = (paths, usedBy);
    }

    string _fsInputInitial = "", _fsInputTarget = ""; // what the box opened with; what a rename renames

    /// <summary>Opens the name box: <paramref name="initial"/> is "New folder/" or "new.scene.json" for a new one; <paramref name="target"/> is the item a rename renames.</summary>
    void StartInput(FsInput kind, string initial, string target = "")
    {
        _fsInput = kind;
        _fsInputInitial = initial;
        _fsInputTarget = target;
        _fsInputEdit.SetValue(initial.AsSpan());
        _fsInputOpen = true;
    }

    /// <summary>The input box for naming something new, or renaming.</summary>
    void FsInputBox(List<FsEntry> entries)
    {
        if (_fsInputOpen)
        {
            ImGui.OpenPopup("fs-input");
            _fsInputOpen = false;
        }
        if (_fsInput == FsInput.None || !ImGui.BeginPopup("fs-input"))
        {
            if (_fsInput != FsInput.None && !ImGui.IsPopupOpen("fs-input"))
                _fsInput = FsInput.None; // closed by clicking elsewhere
            return;
        }
        var newFolder = _fsInputInitial.EndsWith('/');
        var title = _fsInput switch
        {
            FsInput.Create => newFolder ? "New folder:" : "New scene:",
            _ => "Rename:",
        };
        ImGui.Text(title);
        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(380);
        var enter = ImGui.InputText("##input", _fsInputEdit, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
        var name = _fsInputEdit.ToString().Trim();
        // A rename stays in its folder: a name with / or \ in it, or . or .., would move it
        var badName = _fsInput == FsInput.Rename && (name.IndexOfAny(['/', '\\']) >= 0 || name is "." or "..");
        if (badName && enter)
            ImGui.SetKeyboardFocusHere(-1); // Enter left the field: back to it
        if (badName)
            ImGui.TextColored(Warning, "A name can't have / or \\ in it, or be . or ..");
        if (ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            _fsInput = FsInput.None;
            ImGui.CloseCurrentPopup();
        }
        else if (enter && !badName)
        {
            var kind = _fsInput;
            _fsInput = FsInput.None;
            ImGui.CloseCurrentPopup();
            if (kind == FsInput.Create && name.Length > 0)
            {
                // Typing over the selected "New folder/" or "new.scene.json" still makes a folder or a scene
                var made = newFolder ? name.TrimEnd('/') + "/" : ProjectFiles.KeepExtension(_fsInputInitial, name);
                Safely(() => Create(made));
            }
            else if (kind == FsInput.Rename && name.Length > 0 && RenameTarget(entries) is { } e)
            {
                var to = Join(Parent(e.Path), e.Folder ? name : ProjectFiles.KeepExtension(e.Name, name));
                var folder = _fsDir;
                if (to != e.Path)
                    _afterTree = () =>
                    {
                        MoveFile(e.Path, to);
                        _fsCursorIn[folder] = to;
                    };
            }
        }
        ImGui.EndPopup();
    }

    /// <summary>The item the rename box was opened on, if it's still there: shown in the content view, or a folder from the tree's menu.</summary>
    FsEntry? RenameTarget(List<FsEntry> entries) =>
        _fsInputTarget.Length == 0 ? null
        : entries.Find(e => e.Path == _fsInputTarget)
            ?? (Directory.Exists(_project.Absolute(_fsInputTarget)) ? new FsEntry(_fsInputTarget, Path.GetFileName(_fsInputTarget), true, 0, default, false) : null);

    /// <summary>A folder (name ending in /), a scene (.scene.json, then opened), or an empty file.</summary>
    void Create(string name)
    {
        var path = ProjectFiles.Create(_project.Root, _fsDir, name);
        if (path.EndsWith(".scene.json", StringComparison.Ordinal))
            _afterTree = () => OpenScene(path);
        FsRefresh();
        Reveal(path);
        Log.Info("editor", $"created {path}");
    }

    void Bookmarks()
    {
        if (_fsBookmarksOpen)
        {
            ImGui.OpenPopup("bookmarks");
            _fsBookmarksOpen = false;
        }
        if (!ImGui.BeginPopup("bookmarks"))
            return;
        ImGui.TextDisabled("Bookmarks (Bookmark in an item's right-click menu adds one)");
        if (_favorites.Count == 0)
            ImGui.TextDisabled("  none yet");
        foreach (var f in _favorites.ToList())
        {
            var folder = Directory.Exists(_project.Absolute(f));
            var (icon, color) = folder ? (Icon.Folder, Icons.FolderColor) : Icons.ForFile(f);
            Icons.Inline(icon, color);
            ImGui.SameLine();
            if (ImGui.Selectable(f))
            {
                if (folder) Cd(f);
                else Reveal(f);
            }
        }
        ImGui.EndPopup();
    }

    void FileMenu(FsEntry e)
    {
        var path = e.Path;
        var targets = _fsSelected.Contains(path) ? [.. _fsSelected] : new List<string> { path };
        var many = targets.Count > 1 ? $" {targets.Count} items" : "";
        if (e.Folder)
        {
            if (ImGui.MenuItem("Open", "Enter")) Cd(path);
        }
        else if (path.EndsWith(".scene.json", StringComparison.Ordinal))
        {
            if (ImGui.MenuItem("Open scene", "Enter")) _afterTree = () => OpenScene(path);
            if (ImGui.MenuItem("Instance in scene", default, false, path != _scene.Path))
                _afterTree = () => InstanceScene(path, NewNodePosition(), _scene.Primary);
        }
        else if (IsModel(path))
        {
            if (ImGui.MenuItem("Place in scene", "Enter")) _afterTree = () => Place(path, NewNodePosition(), _scene.Primary);
        }
        else if (path.EndsWith(".cs", StringComparison.Ordinal))
        {
            if (ImGui.MenuItem("Open script", "Enter")) _pendingScript = (_project.Absolute(path), -1);
        }
        else if (ImGui.MenuItem("Open with the desktop's app", "Enter"))
            ActivateFile(path);
        ImGui.Separator();
        if (ImGui.MenuItem("Copy" + many, "Ctrl+C")) Yank(targets, cut: false);
        if (ImGui.MenuItem("Cut" + many, "Ctrl+X")) Yank(targets, cut: true);
        if (ImGui.MenuItem("Paste", "Ctrl+V", false, _fsYank != null)) Paste(e.Folder ? path : _fsDir); // a folder's menu pastes into it
        if (ImGui.MenuItem("Rename", "F2", false, targets.Count == 1)) StartInput(FsInput.Rename, e.Name, path);
        if (ImGui.MenuItem("Move to trash" + many, "Delete")) AskTrash(targets);
        ImGui.Separator();
        if (ImGui.MenuItem(_favorites.Contains(path) ? "Remove bookmark" : "Bookmark")) ToggleBookmark(path);
        if (ImGui.MenuItem("Copy path")) Copy(path);
        if (ImGui.MenuItem("Show in file manager"))
            OnDesktop(e.Folder ? path : Parent(path));
    }

    void NewItems()
    {
        if (ImGui.MenuItem("New folder")) StartInput(FsInput.Create, "New folder/");
        if (ImGui.MenuItem("New scene")) StartInput(FsInput.Create, "new.scene.json");
        if (ImGui.MenuItem("New script...")) _scripts.NewScript("NewScript");
    }

    void FsConfirmDelete()
    {
        if (_fsDelete != null && !ImGui.IsPopupOpen("fs-delete"))
            ImGui.OpenPopup("fs-delete");
        if (!ImGui.BeginPopupModal("fs-delete", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar))
            return;
        if (_fsDelete is { } d)
        {
            ImGui.Text(d.Paths.Count == 1 ? $"Move {d.Paths[0]} to the trash?" : $"Move these {d.Paths.Count} to the trash?");
            if (d.Paths.Count > 1)
                foreach (var p in d.Paths.Take(8))
                    ImGui.BulletText(p);
            if (d.UsedBy.Count > 0)
            {
                ImGui.TextColored(Warning, $"Used by {d.UsedBy.Count} scene{(d.UsedBy.Count == 1 ? "" : "s")}:");
                foreach (var s in d.UsedBy.Take(8))
                    ImGui.BulletText(s);
            }
            // The open scene (or a folder holding it) stays open: it's marked unsaved, and saving writes it back
            var holdsOpenScene = d.Paths.Any(p => ProjectFiles.Moved(_scene.Path, p, p) != null);
            if (holdsOpenScene)
                ImGui.TextColored(Warning, d.Paths.Contains(_scene.Path)
                    ? "This includes the open scene. It stays open, marked unsaved; saving writes it back."
                    : $"This holds the open scene ({_scene.Path}). It stays open, marked unsaved; saving writes it back.");
            ImGui.TextDisabled("They can be restored from the trash in your file manager.");
            if (ImGui.Button("Move to trash", new float2(140, 0)) || ImGui.IsKeyPressed(ImGuiKey.Enter))
            {
                foreach (var path in d.Paths)
                {
                    try
                    {
                        ProjectFiles.Trash(_project.Absolute(path));
                        Log.Info("editor", $"moved {path} to the trash");
                        _favorites.Remove(path);
                        _fsSelected.Remove(path);
                    }
                    catch (Exception e)
                    {
                        Log.Error("editor", $"couldn't move {path} to the trash: {e.Message}");
                    }
                }
                if (holdsOpenScene && !File.Exists(_project.Absolute(_scene.Path)))
                {
                    _scene.Dirty = true;
                    Log.Warn("editor", $"{_scene.Path} is open but its file is in the trash: save to keep it, or open another scene to let it go");
                }
                SaveFavorites();
                FsRefresh();
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

    static readonly (string Keys, string What)[] FsKeyHelp =
    [
        ("Click, Ctrl+click, Shift+click", "select; add or remove; select the range"),
        ("Double-click, Enter", "open: into a folder, a scene, a script, place a model"),
        ("Drag", "into the viewport, onto a node or an asset field; onto a folder to move"),
        ("Right-click", "everything you can do with it"),
        ("Arrows", "move (left and right along a row of tiles)"),
        ("Backspace, Alt+Up", "up a folder"),
        ("Alt+Left, Alt+Right", "back, forward"),
        ("Ctrl+C  Ctrl+X  Ctrl+V", "copy, cut, paste"),
        ("F2  Delete", "rename (keeps the extension), move to trash"),
        ("Ctrl+A  Ctrl+F", "select all, search"),
        ("Esc", "clear the search or the selection"),
    ];

    void FsHelpWindow()
    {
        if (_fsHelp)
        {
            ImGui.OpenPopup("fs-help");
            _fsHelp = false;
        }
        if (!ImGui.BeginPopup("fs-help"))
            return;
        ImGui.TextDisabled("FileSystem: how to use it");
        ImGui.Separator();
        if (ImGui.BeginTable("keys", 2))
        {
            foreach (var (keys, what) in FsKeyHelp)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextColored(Accent, keys);
                ImGui.TableNextColumn();
                ImGui.Text(what);
            }
            ImGui.EndTable();
        }
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
        if (ProjectFiles.Moved(_fsDir, from, to) is { } dir)
            _fsDir = dir;
        _fsSelected.Clear();
        FsRefresh();
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
