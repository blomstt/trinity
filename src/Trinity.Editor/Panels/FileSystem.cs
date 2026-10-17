using System.Diagnostics;
using System.Numerics;
using System.Text;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Brutal.StbApi;
using Trinity.Editor.Scripting;
using Trinity.Graphics;
using Trinity.Scenes;

namespace Trinity.Editor;

/// <summary>
/// The FileSystem dock, laid out and driven like yazi: three columns (the parent folder, the folder
/// you're in, and a preview of what the cursor is on: a folder's contents, an image, code with colours,
/// a scene's nodes or a model's numbers), a header with the path, and a status line. It works from the
/// keyboard with yazi's keys (~ or the ? button lists them): hjkl to move, Space and v to select, y x p
/// to copy, cut and paste, d to trash, a to create, r to rename, / and f to find and filter, comma to
/// sort. The mouse works too: click, double-click to open, drag a model or scene into the viewport, onto a
/// node or onto an asset field, and right-click for a menu. Moving and renaming update every scene that
/// refers to the file; deleting moves to the desktop trash.
/// Beside it, the History dock lists the undo steps; click one to go back (or forward) to it.
/// </summary>
sealed partial class EditorApp
{
    const string FileSystemPanel = "FileSystem", HistoryPanel = "History";
    const string AssetPayload = "trinity-asset";

    /// <summary>The column widths, as yazi's ratio: parent, current, preview.</summary>
    static readonly float[] FsRatio = [2, 2, 4];
    /// <summary>Rows kept between the cursor and the column's edge while moving (yazi's scrolloff).</summary>
    const int FsScrolloff = 5;

    sealed record FsEntry(string Path, string Name, bool Folder, long Size, DateTime Modified, bool Hidden);

    enum FsSort { Natural, Alphabetical, Modified, Extension, Size }
    enum FsInput { None, Find, FindBack, Filter, Create, Rename }

    string _fsDir = "";
    readonly Dictionary<string, string> _fsCursorIn = []; // folder -> name the cursor was on there
    readonly Dictionary<string, (double At, List<FsEntry> Entries)> _fsListings = [];
    readonly HashSet<string> _fsSelected = [];
    (List<string> Paths, bool Cut)? _fsYank;
    readonly Stack<string> _fsBack = new(), _fsForward = new();
    FsSort _fsSort = FsSort.Natural;
    bool _fsReverse, _fsShowHidden, _fsVisual, _fsScrollToCursor = true, _fsFocused;
    int _fsVisualAnchor;
    string _fsFilter = "", _fsFind = "";
    FsInput _fsInput;
    bool _fsInputOpen, _fsBookmarksOpen;
    readonly ImInputString _fsInputEdit = new(256);
    char _fsPending; // the first key of a two-key command (g, c, comma)
    List<string> _favorites = [];
    string _draggedAsset = "";
    bool _showFileSystem = true, _showHistory = true;
    (List<string> Paths, List<string> UsedBy)? _fsDelete;
    bool _fsHelp;

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

    /// <summary>What a column shows: sorted as set (folders first), hidden files only when shown, filtered for the current folder.</summary>
    List<FsEntry> Visible(string folder)
    {
        IEnumerable<FsEntry> entries = Listing(folder).Where(e => _fsShowHidden || !e.Hidden);
        if (folder == _fsDir && _fsFilter.Length > 0)
            entries = entries.Where(e => e.Name.Contains(_fsFilter, StringComparison.OrdinalIgnoreCase));
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

    static string Extension(string name) => name.EndsWith(".scene.json", StringComparison.Ordinal) ? ".scene.json" : Path.GetExtension(name);

    /// <summary>Natural order, case-sensitive (as yazi's sort_by = "natural"): "rock 2" before "rock 10".</summary>
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
        if (_fsCursorIn.TryGetValue(folder, out var name) && entries.FindIndex(e => e.Name == name) is var i and >= 0)
            return i;
        return 0;
    }

    void SetCursor(List<FsEntry> entries, int index)
    {
        if (entries.Count == 0)
            return;
        index = Math.Clamp(index, 0, entries.Count - 1);
        _fsCursorIn[_fsDir] = entries[index].Name;
        _fsScrollToCursor = true;
        if (_fsVisual)
        {
            // Visual mode: everything between where it started and the cursor is selected
            _fsSelected.Clear();
            for (var i = Math.Min(_fsVisualAnchor, index); i <= Math.Max(_fsVisualAnchor, index); i++)
                _fsSelected.Add(entries[i].Path);
        }
    }

    /// <summary>Goes into a folder (remembering where we were, for H and L).</summary>
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
        _fsFilter = "";
        _fsVisual = false;
        _fsScrollToCursor = true;
    }

    /// <summary>Shows a project file in the FileSystem dock (--browse, for checking without a person).</summary>
    public void Browse(string path) => Reveal(SceneLibrary.Normalize(path).Trim('/'));

    /// <summary>Shows a file or folder: goes to its folder with the cursor on it.</summary>
    void Reveal(string path)
    {
        Cd(Parent(path));
        _fsCursorIn[Parent(path)] = Path.GetFileName(path);
        _fsScrollToCursor = true;
    }

    /// <summary>What an operation acts on: the selection, or the entry under the cursor.</summary>
    List<string> Targets(FsEntry? cursor) =>
        _fsSelected.Count > 0 ? [.. _fsSelected] : cursor != null ? [cursor.Path] : [];

    // --- Drawing ------------------------------------------------------------------------------------

    void FileSystem()
    {
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
        if (_fsFocused && _fsInput == FsInput.None && !ImGui.GetIO().WantTextInput && !ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId))
        {
            FsKeys(entries);
            entries = Visible(_fsDir); // the keys may have changed the folder or its contents
        }
        var cursorIndex = CursorIn(entries, _fsDir);
        var cursor = entries.Count > 0 ? entries[cursorIndex] : null;

        FsHeader();
        var avail = ImGui.GetContentRegionAvail();
        var statusHeight = ImGui.GetFrameHeight() + 2;
        var height = Math.Max(40, avail.Y - statusHeight - ImGui.GetStyle().ItemSpacing.Y);
        var total = FsRatio.Sum();
        const float spacing = 6f;
        var width = avail.X - spacing * 2;

        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new float2(0, 1));
        ParentColumn(width * FsRatio[0] / total, height);
        ImGui.SameLine(0, spacing);
        CurrentColumn(entries, cursorIndex, width * FsRatio[1] / total, height);
        ImGui.SameLine(0, spacing);
        PreviewColumn(cursor, width * FsRatio[2] / total, height);
        ImGui.PopStyleVar();

        FsStatus(entries, cursorIndex, cursor);
        FsInputBox(entries);
        Bookmarks();
        FsConfirmDelete();
        FsHelpWindow();
        ImGui.End();
    }

    void FsHeader()
    {
        var path = (_project.Name + "/" + _fsDir).TrimEnd('/');
        ImGui.TextColored(Icons.FolderColor, path);
        var notes = new List<string>();
        if (_fsFilter.Length > 0) notes.Add($"filter: {_fsFilter}");
        if (_fsFind.Length > 0) notes.Add($"find: {_fsFind}");
        if (_fsYank is { } yank) notes.Add($"{yank.Paths.Count} {(yank.Cut ? "cut" : "yanked")}");
        if (_fsSelected.Count > 0) notes.Add($"{_fsSelected.Count} selected");
        var right = string.Join("   ", notes);
        if (right.Length > 0)
        {
            ImGui.SameLine(Math.Max(ImGui.GetCursorPosX() + 20, ImGui.GetWindowWidth() - ImGui.CalcTextSize(right).X - 40));
            ImGui.TextDisabled(right);
        }
        ImGui.SameLine(ImGui.GetWindowWidth() - 28);
        if (ImGui.SmallButton("?"))
            _fsHelp = true;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Keys (~)");
    }

    void ParentColumn(float width, float height)
    {
        ImGui.BeginChild("parent", new float2(width, height));
        if (_fsDir.Length == 0)
        {
            // At the top: the project itself
            FsRow(new FsEntry("", _project.Name, true, 0, default, false), "p", width, cursor: true, dim: true);
        }
        else
        {
            var parent = Parent(_fsDir);
            foreach (var e in Visible(parent))
                if (FsRow(e, "p", width, cursor: e.Path == _fsDir, dim: true) is var click and not 0)
                {
                    if (e.Folder) Cd(e.Path);
                    else Reveal(e.Path);
                    if (click == 2 && !e.Folder) ActivateFile(e.Path);
                }
        }
        ImGui.EndChild();
    }

    void CurrentColumn(List<FsEntry> entries, int cursorIndex, float width, float height)
    {
        ImGui.BeginChild("current", new float2(width, height));
        if (entries.Count == 0)
            ImGui.TextDisabled(_fsFilter.Length > 0 ? "  Nothing matches the filter" : "  Empty folder (a creates)");
        var rowHeight = ImGui.GetFrameHeight() + 1;
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var click = FsRow(e, "c", width, cursor: i == cursorIndex, dim: false);
            if (click == 0)
                continue;
            if (ImGui.GetIO().KeyCtrl && !_fsSelected.Remove(e.Path))
                _fsSelected.Add(e.Path);
            _fsCursorIn[_fsDir] = e.Name;
            if (click == 2)
                Open(e);
        }
        // yazi's scrolloff: the cursor keeps FsScrolloff rows from the edges
        if (_fsScrollToCursor && entries.Count > 0)
        {
            _fsScrollToCursor = false;
            var top = cursorIndex * rowHeight;
            var view = ImGui.GetWindowHeight();
            var margin = Math.Min(FsScrolloff, (int)(view / rowHeight / 2)) * rowHeight;
            var scroll = ImGui.GetScrollY();
            if (top - margin < scroll)
                ImGui.SetScrollY(Math.Max(0, top - margin));
            else if (top + rowHeight + margin > scroll + view)
                ImGui.SetScrollY(top + rowHeight + margin - view);
        }
        // Empty space: drop here to move into this folder; right-click for New
        var space = ImGui.GetContentRegionAvail();
        if (space.Y > 4)
        {
            ImGui.InvisibleButton("##space", new float2(Math.Max(1, space.X), space.Y));
            FolderDropTarget(_fsDir);
            if (ImGui.BeginPopupContextItem("space"))
            {
                NewMenu();
                if (ImGui.MenuItem("Paste here", "p", false, _fsYank != null)) Paste();
                ImGui.EndPopup();
            }
        }
        ImGui.EndChild();
    }

    /// <summary>
    /// One entry, drawn like a yazi line: icon, name, size at the right; the cursor line filled, selected
    /// entries marked at the left (yanked ones in green, cut ones in red). Returns 1 for a click, 2 for a double-click.
    /// </summary>
    int FsRow(FsEntry e, string column, float width, bool cursor, bool dim)
    {
        var p = ImGui.GetCursorScreenPos();
        var h = ImGui.GetFrameHeight();
        ImGui.PushID(column + e.Path);
        var clicked = ImGui.InvisibleButton("##row", new float2(Math.Max(1, width - 4), h));
        var hovered = ImGui.IsItemHovered();
        var dl = ImGui.GetWindowDrawList();
        var end = new float2(p.X + width - 4, p.Y + h);
        if (cursor)
            dl.AddRectFilled(p, end, ImGui.GetColorU32(dim ? ImGuiHost.Color(0.18f, 0.19f, 0.22f) : ImGuiHost.Color(0.43f, 0.33f, 0.19f)), 3);
        else if (hovered)
            dl.AddRectFilled(p, end, ImGui.GetColorU32(ImGuiCol.HeaderHovered, 0.6f), 3);
        var yank = _fsYank is { } y && y.Paths.Contains(e.Path) ? (y.Cut ? 2 : 1) : 0;
        var selected = _fsSelected.Contains(e.Path);
        if (selected || yank != 0)
        {
            var marker = yank == 2 ? ImGuiHost.Color(0.93f, 0.40f, 0.36f) : yank == 1 ? ImGuiHost.Color(0.45f, 0.82f, 0.50f) : ImGuiHost.Color(0.88f, 0.66f, 0.36f);
            dl.AddRectFilled(p, new float2(p.X + 3, end.Y), ImGui.GetColorU32(marker), 1);
        }
        var (icon, iconColor) = e.Folder ? (Icon.Folder, Icons.FolderColor) : Icons.ForFile(e.Name);
        var size = ImGui.GetTextLineHeight();
        Icons.Draw(dl, icon, new float2(p.X + 8, p.Y + (h - size) / 2), size, iconColor);
        var info = e.Folder || dim ? "" : FormatSize(e.Size);
        var infoWidth = info.Length > 0 ? ImGui.CalcTextSize(info).X : 0;
        var nameX = p.X + 14 + size;
        var nameColor = selected ? ImGuiHost.Color(0.95f, 0.78f, 0.45f)
            : cursor && !dim ? ImGuiHost.Color(1, 1, 1)
            : e.Folder ? Icons.FolderColor
            : e.Hidden ? Icons.Dim
            : ImGuiHost.Color(0.84f, 0.85f, 0.88f);
        var textY = p.Y + (h - ImGui.GetTextLineHeight()) / 2;
        dl.AddText(new float2(nameX, textY), ImGui.GetColorU32(nameColor), Fit(e.Name, end.X - nameX - infoWidth - 12));
        if (info.Length > 0)
            dl.AddText(new float2(end.X - infoWidth - 6, textY), ImGui.GetColorU32(cursor ? ImGuiHost.Color(0.95f, 0.92f, 0.86f) : Icons.Dim), info);

        // Drag a file (into the viewport, onto a node or an asset field, or onto a folder to move it)
        if (e.Path.Length > 0 && ImGui.BeginDragDropSource())
        {
            _draggedAsset = e.Path;
            ImGui.SetDragDropPayload(AssetPayload, default, 0);
            Icons.Inline(icon, iconColor);
            ImGui.SameLine();
            ImGui.Text(e.Name);
            ImGui.EndDragDropSource();
        }
        if (e.Folder)
            FolderDropTarget(e.Path);
        if (e.Path.Length > 0 && ImGui.BeginPopupContextItem("menu"))
        {
            if (column == "c")
                _fsCursorIn[_fsDir] = e.Name;
            FileMenu(e);
            ImGui.EndPopup();
        }
        var result = clicked ? (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) ? 2 : 1) : 0;
        ImGui.PopID();
        return result;
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

    void FsStatus(List<FsEntry> entries, int cursorIndex, FsEntry? cursor)
    {
        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        var h = ImGui.GetFrameHeight();
        var w = ImGui.GetContentRegionAvail().X;
        // The mode, as yazi's: NOR, VIS (visual) or SEL (something selected)
        var (mode, modeColor) = _fsVisual ? ("VIS", ImGuiHost.Color(0.45f, 0.68f, 0.98f))
            : _fsSelected.Count > 0 ? ("SEL", ImGuiHost.Color(0.95f, 0.72f, 0.33f))
            : ("NOR", ImGuiHost.Color(0.88f, 0.66f, 0.36f));
        var modeWidth = ImGui.CalcTextSize(mode).X + 16;
        dl.AddRectFilled(p, new float2(p.X + modeWidth, p.Y + h), ImGui.GetColorU32(modeColor), 4);
        var textY = p.Y + (h - ImGui.GetTextLineHeight()) / 2;
        dl.AddText(new float2(p.X + 8, textY), ImGui.GetColorU32(ImGuiHost.Color(0.07f, 0.08f, 0.10f)), mode);
        var sort = _fsSort.ToString().ToLowerInvariant() + (_fsReverse ? " (reversed)" : "");
        var right = $"{(cursor == null ? "" : cursor.Modified.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + "   ")}{sort}{(_fsShowHidden ? "   hidden shown" : "")}   {(entries.Count == 0 ? 0 : cursorIndex + 1)}/{entries.Count}";
        var rightWidth = ImGui.CalcTextSize(right).X;
        var left = cursor == null ? "" : cursor.Folder ? cursor.Name + "/" : $"{FormatSize(cursor.Size)}   {cursor.Name}";
        dl.AddText(new float2(p.X + modeWidth + 10, textY), ImGui.GetColorU32(Icons.FileColor), Fit(left, w - modeWidth - rightWidth - 30));
        dl.AddText(new float2(p.X + w - rightWidth - 4, textY), ImGui.GetColorU32(Icons.Dim), right);
        ImGui.Dummy(new float2(w, h));
    }

    // --- Preview --------------------------------------------------------------------------------------

    static readonly HashSet<string> TextFiles =
        [".cs", ".json", ".md", ".txt", ".glsl", ".vert", ".frag", ".comp", ".toml", ".csproj", ".props", ".targets", ".xml", ".yml", ".yaml", ".ini", ".sh", ".py"];

    static bool IsImage(string name) => Path.GetExtension(name).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tga";

    void PreviewColumn(FsEntry? e, float width, float height)
    {
        ImGui.BeginChild("preview", new float2(width, height));
        if (e == null)
        {
            ImGui.EndChild();
            return;
        }
        if (e.Folder)
        {
            var inside = Visible(e.Path);
            if (inside.Count == 0)
                ImGui.TextDisabled("  Empty folder");
            var remembered = _fsCursorIn.ContainsKey(e.Path) ? CursorIn(inside, e.Path) : -1;
            for (var i = 0; i < inside.Count; i++)
                if (FsRow(inside[i], "v", width, cursor: i == remembered, dim: true) is var click and not 0)
                {
                    Cd(e.Path);
                    _fsCursorIn[e.Path] = inside[i].Name;
                    if (click == 2) Open(inside[i]);
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
            ImGui.TextDisabled($"  {FormatSize(e.Size)}, no preview for this kind of file");
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
            catch (IOException ex)
            {
                cached = (e.Modified, null, ex.Message);
            }
            _scenePreviews[e.Path] = cached;
        }
        if (cached.Doc is not { } d)
        {
            ImGui.TextColored(Warning, "  " + (cached.Problem.Length > 0 ? cached.Problem : "can't read this scene"));
            return;
        }
        ImGui.TextDisabled($"  {d.Nodes.Count} node{(d.Nodes.Count == 1 ? "" : "s")}   (Enter opens it)");
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

    void ModelPreview(FsEntry e)
    {
        if (_view.Model(e.Path) is not { } model)
        {
            ImGui.TextDisabled("  loading...");
            return;
        }
        var m = model.Model;
        ImGui.Text($"  {e.Name}");
        ImGui.TextDisabled($"  {FormatSize(e.Size)}");
        ImGui.Text($"  {model.Triangles:N0} triangles, {m.Vertices.Length:N0} vertices");
        ImGui.Text($"  {m.Materials.Count} material{(m.Materials.Count == 1 ? "" : "s")}, {m.Textures.Count} texture{(m.Textures.Count == 1 ? "" : "s")}");
        if (m.IsRigged)
        {
            ImGui.Text($"  {m.JointCount} bones, far version {m.Lod1.IndexCount / 3:N0} triangles");
            ImGui.Text("  Clips:");
            foreach (var clip in m.Clips)
                ImGui.TextDisabled($"    {clip.Name}  {clip.Duration:0.00} s");
        }
        else
            ImGui.TextDisabled("  static model");
        ImGui.Spacing();
        ImGui.TextDisabled("  Enter places it; or drag it into the viewport");
    }

    // Images: decoded on a worker thread, shrunk to at most 512 pixels, uploaded; the last few are kept
    sealed class ImagePreviewEntry
    {
        public DateTime Stamp;
        public Task<(int W, int H, byte[] Rgba, int SourceW, int SourceH)>? Decoding;
        public Texture? Texture;
        public ImTextureRef? Ref;
        public int SourceW, SourceH;
        public string Error = "";
        public double Used;
    }

    readonly Dictionary<string, ImagePreviewEntry> _images = [];

    void ImagePreview(FsEntry e, float width)
    {
        if (!_images.TryGetValue(e.Path, out var img) || img.Stamp != e.Modified)
        {
            if (img != null)
                ReleaseImage(img);
            var file = _project.Absolute(e.Path);
            img = new ImagePreviewEntry { Stamp = e.Modified, Decoding = Task.Run(() => DecodeSmall(file, 512)) };
            img.Decoding.ContinueWith(_ => Window.Wake());
            _images[e.Path] = img;
            TrimImages();
        }
        img.Used = ImGui.GetTime();
        if (img.Decoding is { IsCompleted: true } done)
        {
            img.Decoding = null;
            if (done.IsFaulted)
                img.Error = done.Exception?.InnerException?.Message ?? "can't read it";
            else
            {
                var (w, h, rgba, sw, sh) = done.Result;
                img.Texture = new Texture(_gpu, w, h, rgba);
                img.Ref = _ui.AddTexture(img.Texture.Sampler, img.Texture.View);
                (img.SourceW, img.SourceH) = (sw, sh);
            }
        }
        if (img.Error.Length > 0)
            ImGui.TextColored(Warning, "  " + img.Error);
        else if (img.Ref is { } texture && img.Texture is { } t)
        {
            var scale = Math.Min((width - 12) / t.Width, 2f);
            ImGui.SetCursorPosX(6);
            ImGui.Image(texture, new float2(t.Width * scale, t.Height * scale));
            ImGui.TextDisabled($"  {img.SourceW} x {img.SourceH}, {FormatSize(e.Size)}");
        }
        else
            ImGui.TextDisabled("  loading...");
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
        while (_images.Count > 8)
        {
            var oldest = _images.MinBy(kv => kv.Value.Used);
            ReleaseImage(oldest.Value);
            _images.Remove(oldest.Key);
        }
    }

    void ReleaseImage(ImagePreviewEntry img)
    {
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

    /// <summary>yazi's default keys (the ones that fit an editor; ~ shows them).</summary>
    void FsKeys(List<FsEntry> entries)
    {
        var io = ImGui.GetIO();
        var cursorIndex = CursorIn(entries, _fsDir);
        var cursor = entries.Count > 0 ? entries[cursorIndex] : null;
        static bool Key(ImGuiKey k) => ImGui.IsKeyPressed(k);
        var page = Math.Max(1, (int)(ImGui.GetWindowHeight() / (ImGui.GetFrameHeight() + 1)) / 2);

        // Keys that aren't characters
        if (Key(ImGuiKey.DownArrow)) SetCursor(entries, cursorIndex + 1);
        if (Key(ImGuiKey.UpArrow)) SetCursor(entries, cursorIndex - 1);
        if (Key(ImGuiKey.PageDown) || io.KeyCtrl && Key(ImGuiKey.D)) SetCursor(entries, cursorIndex + page);
        if (Key(ImGuiKey.PageUp) || io.KeyCtrl && Key(ImGuiKey.U)) SetCursor(entries, cursorIndex - page);
        if (Key(ImGuiKey.Home)) SetCursor(entries, 0);
        if (Key(ImGuiKey.End)) SetCursor(entries, entries.Count - 1);
        if (Key(ImGuiKey.LeftArrow) || Key(ImGuiKey.Backspace)) Leave();
        if (Key(ImGuiKey.RightArrow) && cursor is { Folder: true }) Cd(cursor.Path);
        if ((Key(ImGuiKey.Enter) || Key(ImGuiKey.KeypadEnter)) && cursor != null) Open(cursor);
        if (Key(ImGuiKey.Delete) && Targets(cursor) is { Count: > 0 } toTrash) AskTrash(toTrash);
        if (Key(ImGuiKey.F2) && cursor != null) StartInput(FsInput.Rename, cursor.Name);
        if (Key(ImGuiKey.F1)) _fsHelp = true;
        if (io.KeyCtrl && Key(ImGuiKey.A))
            foreach (var e in entries)
                _fsSelected.Add(e.Path);
        if (Key(ImGuiKey.Escape))
        {
            if (_fsVisual) _fsVisual = false;
            else if (_fsFilter.Length > 0) _fsFilter = "";
            else if (_fsFind.Length > 0) _fsFind = "";
            else _fsSelected.Clear();
            _fsPending = '\0';
        }
        if (Key(ImGuiKey.Space) && cursor != null)
        {
            if (!_fsSelected.Remove(cursor.Path))
                _fsSelected.Add(cursor.Path);
            SetCursor(entries, cursorIndex + 1);
        }
        if (io.KeyCtrl)
            return; // the rest are plain characters

        // Characters, read as typed, so the keys work on any keyboard layout
        foreach (var codepoint in io.InputQueueCharacters.Span)
        {
            var ch = (char)codepoint;
            if (ch == ' ')
                continue; // Space is handled as a key above
            cursorIndex = CursorIn(entries, _fsDir);
            cursor = entries.Count > 0 ? entries[cursorIndex] : null;
            if (_fsPending != '\0')
            {
                TwoKey(_fsPending, ch, entries, cursor);
                _fsPending = '\0';
                continue;
            }
            switch (ch)
            {
                case 'j': SetCursor(entries, cursorIndex + 1); break;
                case 'k': SetCursor(entries, cursorIndex - 1); break;
                case 'J': SetCursor(entries, cursorIndex + 5); break;
                case 'K': SetCursor(entries, cursorIndex - 5); break;
                case 'G': SetCursor(entries, entries.Count - 1); break;
                case 'h': Leave(); break;
                case 'l' when cursor != null:
                    if (cursor.Folder) Cd(cursor.Path);
                    else Open(cursor);
                    break;
                case 'o' when cursor != null: Open(cursor); break;
                case 'H' when _fsBack.Count > 0:
                    _fsForward.Push(_fsDir);
                    Cd(_fsBack.Pop(), remember: false);
                    break;
                case 'L' when _fsForward.Count > 0:
                    _fsBack.Push(_fsDir);
                    Cd(_fsForward.Pop(), remember: false);
                    break;
                case 'v':
                    _fsVisual = !_fsVisual;
                    _fsVisualAnchor = cursorIndex;
                    if (_fsVisual) SetCursor(entries, cursorIndex);
                    break;
                case 'y' or 'x' when Targets(cursor) is { Count: > 0 } targets:
                    _fsYank = (targets, ch == 'x');
                    _fsSelected.Clear();
                    _fsVisual = false;
                    break;
                case 'Y' or 'X': _fsYank = null; break;
                case 'p' or 'P': Paste(); break;
                case 'd' or 'D' when Targets(cursor) is { Count: > 0 } targets: AskTrash(targets); break;
                case 'a': StartInput(FsInput.Create, ""); break;
                case 'r' when cursor != null: StartInput(FsInput.Rename, cursor.Name); break;
                case '.': _fsShowHidden = !_fsShowHidden; break;
                case '/': StartInput(FsInput.Find, ""); break;
                case '?': StartInput(FsInput.FindBack, ""); break;
                case 'n': FindNext(entries, forward: true); break;
                case 'N': FindNext(entries, forward: false); break;
                case 'f': StartInput(FsInput.Filter, _fsFilter); break;
                case 'm' when cursor != null: ToggleBookmark(cursor.Path); break;
                case '\'': _fsBookmarksOpen = true; break;
                case '~': _fsHelp = true; break;
                case 'g' or 'c' or ',': _fsPending = ch; break;
            }
        }
    }

    void TwoKey(char first, char second, List<FsEntry> entries, FsEntry? cursor)
    {
        switch (first, second)
        {
            case ('g', 'g'): SetCursor(entries, 0); break;
            case ('g', 'h'): Cd(""); break; // home: the project's top
            case ('c', 'c') when cursor != null: Copy(cursor.Path); break;
            case ('c', 'd') when cursor != null: Copy(Parent(cursor.Path)); break;
            case ('c', 'f') when cursor != null: Copy(cursor.Name); break;
            case ('c', 'n') when cursor != null: Copy(cursor.Name[..^Extension(cursor.Name).Length]); break;
            case (',', var s):
                FsSort? sort = char.ToLowerInvariant(s) switch
                {
                    'n' => FsSort.Natural,
                    'a' => FsSort.Alphabetical,
                    'm' => FsSort.Modified,
                    'e' => FsSort.Extension,
                    's' => FsSort.Size,
                    _ => null,
                };
                if (sort is { } chosen)
                    (_fsSort, _fsReverse) = (chosen, char.IsUpper(s));
                break;
        }
    }

    static void Copy(string text)
    {
        ImGui.SetClipboardText(text);
        Log.Info("editor", $"copied {text}");
    }

    void ToggleBookmark(string path)
    {
        if (!_favorites.Remove(path))
            _favorites.Add(path);
        SaveFavorites();
        Log.Info("editor", _favorites.Contains(path) ? $"bookmarked {path} (' lists bookmarks)" : $"removed the bookmark on {path}");
    }

    void Leave()
    {
        if (_fsDir.Length == 0)
            return;
        var from = _fsDir;
        Cd(Parent(_fsDir));
        _fsCursorIn[_fsDir] = Path.GetFileName(from);
    }

    /// <summary>Enter, l or o: into a folder, or open a file (a scene in the editor, a script in the script editor, a model placed in the scene, anything else with the desktop's app).</summary>
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
            Process.Start(new ProcessStartInfo("xdg-open", [_project.Absolute(path)]) { UseShellExecute = false })?.Dispose();
    }

    void FindNext(List<FsEntry> entries, bool forward)
    {
        if (_fsFind.Length == 0 || entries.Count == 0)
            return;
        var start = CursorIn(entries, _fsDir);
        for (var n = 1; n <= entries.Count; n++)
        {
            var i = ((forward ? start + n : start - n) % entries.Count + entries.Count) % entries.Count;
            if (entries[i].Name.Contains(_fsFind, StringComparison.OrdinalIgnoreCase))
            {
                SetCursor(entries, i);
                return;
            }
        }
    }

    // --- Operations ----------------------------------------------------------------------------------

    /// <summary>p: copies (or moves, after x) what was yanked into this folder; a name that's taken gets _1, _2 and so on.</summary>
    void Paste()
    {
        if (_fsYank is not { } yank)
            return;
        var folder = _fsDir;
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
                _fsCursorIn[folder] = Path.GetFileName(to);
            }
            if (yank.Cut)
                _fsYank = null;
            FsRefresh();
        };
    }

    void AskTrash(List<string> paths)
    {
        if (paths.Count == 0)
            return;
        var usedBy = paths.SelectMany(p => ProjectFiles.ReferencesTo(_library, p)).Where(s => !paths.Contains(s)).Distinct().ToList();
        _fsDelete = (paths, usedBy);
    }

    void StartInput(FsInput kind, string initial)
    {
        _fsInput = kind;
        _fsInputEdit.SetValue(initial.AsSpan());
        _fsInputOpen = true;
    }

    /// <summary>The input box for find, filter, create and rename, popped up as yazi's.</summary>
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
        var title = _fsInput switch
        {
            FsInput.Find => "Find next:",
            FsInput.FindBack => "Find previous:",
            FsInput.Filter => "Filter:",
            FsInput.Create => "Create (name/ for a folder, name.scene.json for a scene):",
            _ => "Rename:",
        };
        ImGui.Text(title);
        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(380);
        var enter = ImGui.InputText("##input", _fsInputEdit, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
        var text = _fsInputEdit.ToString();
        // Find and filter act as you type
        if (_fsInput == FsInput.Filter)
            _fsFilter = text.Trim();
        if (_fsInput is FsInput.Find or FsInput.FindBack && text != _fsFind)
        {
            _fsFind = text;
            var start = CursorIn(entries, _fsDir);
            if (entries.Count > 0 && !entries[start].Name.Contains(text, StringComparison.OrdinalIgnoreCase))
                FindNext(entries, _fsInput == FsInput.Find);
        }
        if (ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            if (_fsInput == FsInput.Filter) _fsFilter = "";
            _fsInput = FsInput.None;
            ImGui.CloseCurrentPopup();
        }
        else if (enter)
        {
            var kind = _fsInput;
            _fsInput = FsInput.None;
            ImGui.CloseCurrentPopup();
            var name = text.Trim();
            if (kind == FsInput.Create && name.Length > 0)
                Safely(() => Create(name));
            else if (kind == FsInput.Rename && name.Length > 0 && entries.Count > 0)
            {
                var e = entries[CursorIn(entries, _fsDir)];
                var to = Join(_fsDir, e.Folder ? name : ProjectFiles.KeepExtension(e.Name, name));
                var folder = _fsDir;
                if (to != e.Path)
                    _afterTree = () =>
                    {
                        MoveFile(e.Path, to);
                        _fsCursorIn[folder] = Path.GetFileName(to);
                    };
            }
        }
        ImGui.EndPopup();
    }

    /// <summary>a: a folder (name ending in /), a scene (.scene.json, then opened), or an empty file.</summary>
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
        ImGui.TextDisabled("Bookmarks (m on a file or folder adds one)");
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
        if (!e.Folder)
        {
            if (path.EndsWith(".scene.json", StringComparison.Ordinal))
            {
                if (ImGui.MenuItem("Open scene", "Enter")) _afterTree = () => OpenScene(path);
                if (ImGui.MenuItem("Instance in scene", default, false, path != _scene.Path))
                    _afterTree = () => InstanceScene(path, NewNodePosition(), _scene.Primary);
            }
            else if (IsModel(path) && ImGui.MenuItem("Place in scene", "Enter"))
                _afterTree = () => Place(path, NewNodePosition(), _scene.Primary);
            else if (path.EndsWith(".cs", StringComparison.Ordinal) && ImGui.MenuItem("Open script", "Enter"))
                _pendingScript = (_project.Absolute(path), -1);
            ImGui.Separator();
        }
        if (ImGui.MenuItem("Yank (copy)", "y")) _fsYank = ([path], false);
        if (ImGui.MenuItem("Cut", "x")) _fsYank = ([path], true);
        if (ImGui.MenuItem("Paste here", "p", false, _fsYank != null)) Paste();
        if (ImGui.MenuItem("Rename", "r")) StartInput(FsInput.Rename, e.Name);
        if (ImGui.MenuItem("Move to trash", "d")) AskTrash([path]);
        ImGui.Separator();
        NewMenu();
        if (ImGui.MenuItem(_favorites.Contains(path) ? "Remove bookmark" : "Bookmark", "m")) ToggleBookmark(path);
        if (ImGui.MenuItem("Copy path", "c c")) Copy(path);
        if (ImGui.MenuItem("Show in file manager"))
            Process.Start(new ProcessStartInfo("xdg-open", [_project.Absolute(e.Folder ? path : Parent(path))]) { UseShellExecute = false })?.Dispose();
    }

    void NewMenu()
    {
        if (!ImGui.BeginMenu("New"))
            return;
        if (ImGui.MenuItem("Folder", "a  name/")) StartInput(FsInput.Create, "/");
        if (ImGui.MenuItem("Scene", "a  name.scene.json")) StartInput(FsInput.Create, ".scene.json");
        if (ImGui.MenuItem("Script...")) _scripts.NewScript("NewScript");
        ImGui.EndMenu();
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
            ImGui.TextDisabled("They can be restored from the trash in your file manager.   y: yes   n: no");
            if (ImGui.Button("Move to trash", new float2(140, 0)) || ImGui.IsKeyPressed(ImGuiKey.Y) || ImGui.IsKeyPressed(ImGuiKey.Enter))
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
                SaveFavorites();
                FsRefresh();
                _fsDelete = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel", new float2(100, 0)) || ImGui.IsKeyPressed(ImGuiKey.Escape) || ImGui.IsKeyPressed(ImGuiKey.N))
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
        ("j k  ↓ ↑", "move; J K by five; ctrl+d ctrl+u half a page"),
        ("gg  G", "top, bottom"),
        ("h  l  ← →", "parent folder, into a folder"),
        ("Enter  o", "open: a scene, a script, place a model"),
        ("H  L", "back, forward"),
        ("gh", "the project's top"),
        ("Space  v  ctrl+a", "select, visual select, select all"),
        ("y  x  p", "yank (copy), cut, paste here; Y X cancel"),
        ("d", "move to trash"),
        ("a", "create (name/ for a folder, name.scene.json for a scene)"),
        ("r", "rename (keeps the extension)"),
        ("/  ?  n  N", "find next, previous; next and previous match"),
        ("f", "filter"),
        (".", "show hidden files"),
        (",n ,a ,m ,e ,s", "sort: natural, alphabetical, modified, extension, size; capital reverses"),
        ("cc cd cf cn", "copy the path, folder, file name, name without extension"),
        ("m  '", "bookmark, bookmarks"),
        ("Esc", "leave visual mode, clear the filter, find or selection"),
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
        ImGui.TextDisabled("FileSystem keys (as yazi's)");
        ImGui.Separator();
        if (ImGui.BeginTable("keys", 2))
        {
            foreach (var (keys, what) in FsKeyHelp)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextColored(ImGuiHost.Color(0.88f, 0.66f, 0.36f), keys);
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
