using System.Diagnostics;
using System.Text;
using Brutal.ImGuiApi;
using Brutal.Numerics;

namespace Trinity.Editor.Scripting;

/// <summary>A compiler or runtime message at a place in a file. Line and column count from 0.</summary>
readonly record struct Diagnostic(string File, int Line, int Column, bool Error, string Code, string Message);

readonly record struct Pos(int Line, int Col) : IComparable<Pos>
{
    public int CompareTo(Pos o) => Line != o.Line ? Line.CompareTo(o.Line) : Col.CompareTo(o.Col);
    public static bool operator <(Pos a, Pos b) => a.CompareTo(b) < 0;
    public static bool operator >(Pos a, Pos b) => a.CompareTo(b) > 0;
}

/// <summary>
/// A C# code editor drawn with ImGui: syntax colours, line numbers, selection, undo, clipboard, find,
/// auto-indent, comment toggling, and compiler errors marked where they are. Tabs become four spaces.
/// It draws only when the editor redraws, so an open script costs nothing while idle.
/// </summary>
sealed class CodeEditor
{
    public readonly string FilePath;
    public string Name => Path.GetFileName(FilePath);
    public bool Dirty { get; private set; }
    public bool HasFocus { get; private set; }
    public IReadOnlyList<Diagnostic> Diagnostics = [];
    public Pos Cursor => _cursor;

    List<string> _lines = [""];
    Pos _cursor, _anchor;
    int _preferredCol = -1;
    DateTime _diskTime;

    enum Edit { None, Typing, Deleting, Other }

    sealed record Snapshot(string[] Lines, Pos Cursor, Pos Anchor);

    readonly List<Snapshot> _undo = [], _redo = [];
    Edit _lastEdit;
    double _lastEditTime;
    static readonly Stopwatch Clock = Stopwatch.StartNew();

    // Lexer state at the start of each line, valid up to _statesValid
    readonly List<LexState> _states = [LexState.Normal];
    int _statesValid;
    readonly List<Token> _tokens = [];

    int _version, _widthVersion = -1, _maxLength;
    bool _scrollToCursor, _focusNext;
    bool _findOpen, _focusFind, _findActive;
    readonly ImInputString _find = new(256);

    public CodeEditor(string path)
    {
        FilePath = path;
        Load();
    }

    // --- File -----------------------------------------------------------------------------------

    void Load()
    {
        var text = File.Exists(FilePath) ? File.ReadAllText(FilePath) : "";
        _lines = Split(text);
        _diskTime = File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : default;
        _cursor = Clamp(_cursor);
        _anchor = _cursor;
        Dirty = false;
        Changed(0, dirty: false);
    }

    static List<string> Split(string text) => text.Replace("\r\n", "\n").Replace("\t", "    ").Split('\n').ToList();

    public string Text => string.Join('\n', _lines);

    public void Save()
    {
        // Through a temporary file, so a crash or a full disk can't leave half a script
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, Text);
        File.Move(temp, FilePath, overwrite: true);
        _diskTime = File.GetLastWriteTimeUtc(FilePath);
        Dirty = false;
    }

    /// <summary>Picks up changes made by another program, unless there are unsaved edits here.</summary>
    public void ReloadIfChangedOnDisk()
    {
        if (!Dirty && File.Exists(FilePath) && File.GetLastWriteTimeUtc(FilePath) != _diskTime)
            Load();
    }

    public void GoTo(int line, int col)
    {
        _cursor = _anchor = Clamp(new Pos(line, col));
        _scrollToCursor = true;
        _focusNext = true;
    }

    public void Focus() => _focusNext = true;

    // --- Text model -------------------------------------------------------------------------------

    Pos Clamp(Pos p)
    {
        var line = Math.Clamp(p.Line, 0, _lines.Count - 1);
        return new Pos(line, Math.Clamp(p.Col, 0, _lines[line].Length));
    }

    bool HasSelection => _cursor != _anchor;
    (Pos Start, Pos End) Selection => _cursor < _anchor ? (_cursor, _anchor) : (_anchor, _cursor);
    Pos End => new(_lines.Count - 1, _lines[^1].Length);

    string GetText(Pos a, Pos b)
    {
        if (a.Line == b.Line)
            return _lines[a.Line][a.Col..b.Col];
        var sb = new StringBuilder(_lines[a.Line][a.Col..]);
        for (var l = a.Line + 1; l < b.Line; l++)
            sb.Append('\n').Append(_lines[l]);
        return sb.Append('\n').Append(_lines[b.Line][..b.Col]).ToString();
    }

    void Delete(Pos a, Pos b)
    {
        if (a == b)
            return;
        _lines[a.Line] = _lines[a.Line][..a.Col] + _lines[b.Line][b.Col..];
        if (b.Line > a.Line)
            _lines.RemoveRange(a.Line + 1, b.Line - a.Line);
    }

    Pos Insert(Pos at, string text)
    {
        var pieces = Split(text);
        var line = _lines[at.Line];
        var rest = line[at.Col..];
        _lines[at.Line] = line[..at.Col] + pieces[0];
        for (var i = 1; i < pieces.Count; i++)
            _lines.Insert(at.Line + i, pieces[i]);
        var lastLine = at.Line + pieces.Count - 1;
        var end = new Pos(lastLine, _lines[lastLine].Length);
        _lines[lastLine] += rest;
        return end;
    }

    void BeginEdit(Edit kind)
    {
        var now = Clock.Elapsed.TotalSeconds;
        // Typing (or deleting) in a run counts as one step for undo
        if (kind == _lastEdit && kind is Edit.Typing or Edit.Deleting && now - _lastEditTime < 1.5 && !HasSelection)
        {
            _lastEditTime = now;
            return;
        }
        _undo.Add(new Snapshot([.. _lines], _cursor, _anchor));
        if (_undo.Count > 300)
            _undo.RemoveAt(0);
        _redo.Clear();
        _lastEdit = kind;
        _lastEditTime = now;
    }

    void Changed(int fromLine, bool dirty = true)
    {
        Dirty |= dirty;
        _statesValid = Math.Min(_statesValid, fromLine);
        _scrollToCursor = true;
        _version++;
    }

    /// <summary>Replaces the selection (or inserts at the cursor) with <paramref name="text"/>.</summary>
    void Replace(string text, Edit kind)
    {
        BeginEdit(kind);
        var (start, end) = Selection;
        Delete(start, end);
        _cursor = _anchor = Insert(start, text);
        _preferredCol = -1;
        Changed(start.Line);
    }

    void Restore(List<Snapshot> from, List<Snapshot> to)
    {
        if (from.Count == 0)
            return;
        to.Add(new Snapshot([.. _lines], _cursor, _anchor));
        var s = from[^1];
        from.RemoveAt(from.Count - 1);
        _lines = [.. s.Lines];
        _cursor = s.Cursor;
        _anchor = s.Anchor;
        _lastEdit = Edit.None;
        Changed(0);
    }

    static int Indent(string line)
    {
        var i = 0;
        while (i < line.Length && line[i] == ' ')
            i++;
        return i;
    }

    static int Class(char c) => char.IsWhiteSpace(c) ? 0 : char.IsLetterOrDigit(c) || c == '_' ? 1 : 2;

    Pos WordLeft(Pos p)
    {
        if (p.Col == 0)
            return p.Line > 0 ? new Pos(p.Line - 1, _lines[p.Line - 1].Length) : p;
        var line = _lines[p.Line];
        var c = p.Col;
        while (c > 0 && Class(line[c - 1]) == 0)
            c--;
        if (c > 0)
        {
            var k = Class(line[c - 1]);
            while (c > 0 && Class(line[c - 1]) == k)
                c--;
        }
        return p with { Col = c };
    }

    Pos WordRight(Pos p)
    {
        var line = _lines[p.Line];
        if (p.Col >= line.Length)
            return p.Line < _lines.Count - 1 ? new Pos(p.Line + 1, 0) : p;
        var c = p.Col;
        var k = Class(line[c]);
        while (c < line.Length && Class(line[c]) == k)
            c++;
        while (c < line.Length && Class(line[c]) == 0)
            c++;
        return p with { Col = c };
    }

    void SelectWord(Pos p)
    {
        var line = _lines[p.Line];
        if (line.Length == 0)
            return;
        var c = Math.Min(p.Col, line.Length - 1);
        var k = Class(line[c]);
        var a = c;
        var b = c;
        while (a > 0 && Class(line[a - 1]) == k)
            a--;
        while (b < line.Length && Class(line[b]) == k)
            b++;
        _anchor = p with { Col = a };
        _cursor = p with { Col = b };
    }

    // --- Commands -----------------------------------------------------------------------------------

    void Type(string text)
    {
        // A closing brace on an otherwise empty line lines up with its opening one
        if (text == "}" && !HasSelection)
        {
            var line = _lines[_cursor.Line];
            if (_cursor.Col >= 4 && line[.._cursor.Col].All(c => c == ' '))
            {
                BeginEdit(Edit.Other);
                _lines[_cursor.Line] = line[4..];
                _cursor = _anchor = _cursor with { Col = _cursor.Col - 4 };
                Changed(_cursor.Line);
            }
        }
        Replace(text, Edit.Typing);
    }

    void NewLine()
    {
        var (start, end) = Selection;
        var line = _lines[start.Line];
        var indent = Math.Min(Indent(line), start.Col);
        var before = line[..start.Col].TrimEnd();
        // Text moved to the new line loses its leading spaces; the new indent replaces them
        var afterLine = _lines[end.Line];
        var skip = end.Col;
        while (skip < afterLine.Length && afterLine[skip] == ' ')
            skip++;
        _anchor = start;
        _cursor = end with { Col = skip };
        var pad = new string(' ', indent);
        if (before.EndsWith('{') && afterLine[skip..].StartsWith('}'))
        {
            Replace("\n" + pad + "    \n" + pad, Edit.Other);
            _cursor = _anchor = new Pos(_cursor.Line - 1, indent + 4);
            return;
        }
        Replace("\n" + pad + (before.EndsWith('{') ? "    " : ""), Edit.Other);
    }

    void Backspace(bool word)
    {
        if (HasSelection)
        {
            Replace("", Edit.Deleting);
            return;
        }
        var p = _cursor;
        Pos from;
        if (word)
            from = WordLeft(p);
        else if (p.Col > 0 && _lines[p.Line][..p.Col].All(c => c == ' '))
            from = p with { Col = (p.Col - 1) / 4 * 4 }; // back to the previous indent stop
        else if (p.Col > 0)
            from = p with { Col = p.Col - 1 };
        else if (p.Line > 0)
            from = new Pos(p.Line - 1, _lines[p.Line - 1].Length);
        else
            return;
        _anchor = from;
        Replace("", Edit.Deleting);
    }

    void DeleteForward(bool word)
    {
        if (HasSelection)
        {
            Replace("", Edit.Deleting);
            return;
        }
        var p = _cursor;
        var to = word ? WordRight(p)
            : p.Col < _lines[p.Line].Length ? p with { Col = p.Col + 1 }
            : p.Line < _lines.Count - 1 ? new Pos(p.Line + 1, 0)
            : p;
        if (to == p)
            return;
        _anchor = to;
        Replace("", Edit.Deleting);
    }

    (int First, int Last) SelectedLines()
    {
        var (s, e) = Selection;
        var last = e.Line > s.Line && e.Col == 0 ? e.Line - 1 : e.Line;
        return (s.Line, last);
    }

    void IndentLines(bool outdent)
    {
        BeginEdit(Edit.Other);
        var (first, last) = SelectedLines();
        for (var l = first; l <= last; l++)
        {
            var line = _lines[l];
            int delta;
            if (outdent)
            {
                delta = -Math.Min(4, Indent(line));
                _lines[l] = line[-delta..];
            }
            else
            {
                delta = line.Length == 0 ? 0 : 4;
                _lines[l] = new string(' ', delta) + line;
            }
            if (_cursor.Line == l)
                _cursor = _cursor with { Col = Math.Max(0, _cursor.Col + delta) };
            if (_anchor.Line == l)
                _anchor = _anchor with { Col = Math.Max(0, _anchor.Col + delta) };
        }
        Changed(first);
    }

    void ToggleComment()
    {
        var (first, last) = SelectedLines();
        var lines = Enumerable.Range(first, last - first + 1).Where(l => _lines[l].Trim().Length > 0).ToList();
        if (lines.Count == 0)
            return;
        BeginEdit(Edit.Other);
        var indent = lines.Min(l => Indent(_lines[l]));
        var commented = lines.All(l => _lines[l].AsSpan(Indent(_lines[l])).StartsWith("//"));
        foreach (var l in lines)
        {
            var line = _lines[l];
            if (commented)
            {
                var at = Indent(line);
                var length = line.AsSpan(at).StartsWith("// ") ? 3 : 2;
                _lines[l] = line.Remove(at, length);
            }
            else
            {
                _lines[l] = line.Insert(indent, "// ");
            }
        }
        _cursor = Clamp(_cursor);
        _anchor = Clamp(_anchor);
        Changed(first);
    }

    void MoveLines(int direction)
    {
        var (first, last) = SelectedLines();
        if (first + direction < 0 || last + direction >= _lines.Count)
            return;
        BeginEdit(Edit.Other);
        if (direction < 0)
        {
            var moved = _lines[first - 1];
            _lines.RemoveAt(first - 1);
            _lines.Insert(last, moved);
        }
        else
        {
            var moved = _lines[last + 1];
            _lines.RemoveAt(last + 1);
            _lines.Insert(first, moved);
        }
        _cursor = _cursor with { Line = _cursor.Line + direction };
        _anchor = _anchor with { Line = _anchor.Line + direction };
        Changed(Math.Max(0, first - 1));
    }

    void Copy(bool cut)
    {
        string text;
        if (HasSelection)
        {
            var (s, e) = Selection;
            text = GetText(s, e);
        }
        else
        {
            // Nothing selected: the whole line, as most code editors do
            text = _lines[_cursor.Line] + "\n";
            if (cut)
            {
                _anchor = new Pos(_cursor.Line, 0);
                _cursor = _cursor.Line < _lines.Count - 1 ? new Pos(_cursor.Line + 1, 0) : _cursor with { Col = _lines[_cursor.Line].Length };
            }
        }
        ImGui.SetClipboardText(text);
        if (cut)
            Replace("", Edit.Other);
    }

    void Paste()
    {
        var text = Encoding.UTF8.GetString(ImGui.GetClipboardText().AsSpan());
        if (text.Length > 0)
            Replace(text, Edit.Other);
    }

    void FindNext(int direction)
    {
        var needle = _find.ToString();
        if (needle.Length == 0)
            return;
        var (s, e) = Selection;
        var from = direction > 0 ? e : s;
        for (var i = 0; i <= _lines.Count; i++)
        {
            var l = ((from.Line + direction * i) % _lines.Count + _lines.Count) % _lines.Count;
            var line = _lines[l];
            int at;
            if (direction > 0)
            {
                var start = i == 0 ? from.Col : 0;
                at = start <= line.Length ? line.IndexOf(needle, start, StringComparison.OrdinalIgnoreCase) : -1;
            }
            else
            {
                var start = i == 0 ? from.Col - 1 : line.Length - 1;
                at = start >= 0 && line.Length > 0 ? line.LastIndexOf(needle, Math.Min(start, line.Length - 1), StringComparison.OrdinalIgnoreCase) : -1;
                if (at >= 0 && i == 0 && at + needle.Length > from.Col)
                    at = -1;
            }
            if (at >= 0)
            {
                _anchor = new Pos(l, at);
                _cursor = new Pos(l, at + needle.Length);
                _scrollToCursor = true;
                return;
            }
        }
    }

    void OpenFind()
    {
        _findOpen = true;
        _focusFind = true;
        var (s, e) = Selection;
        if (HasSelection && s.Line == e.Line)
            _find.SetValue(GetText(s, e).AsSpan());
    }

    void Move(Pos to, bool select)
    {
        _cursor = Clamp(to);
        if (!select)
            _anchor = _cursor;
        _lastEdit = Edit.None;
        _scrollToCursor = true;
    }

    void HandleKeys(int visibleLines)
    {
        var io = ImGui.GetIO();
        bool Key(ImGuiKey k) => ImGui.IsKeyPressed(k);
        var ctrl = io.KeyCtrl;
        var shift = io.KeyShift;
        var alt = io.KeyAlt;

        foreach (var ch in io.InputQueueCharacters)
            if (ch >= 32 && ch != 127 && !ctrl)
                Type(char.ConvertFromUtf32((int)ch));

        var p = _cursor;
        var keepColumn = false;
        if (Key(ImGuiKey.LeftArrow))
            Move(HasSelection && !shift ? Selection.Start : ctrl ? WordLeft(p) : p.Col > 0 ? p with { Col = p.Col - 1 } : p.Line > 0 ? new Pos(p.Line - 1, _lines[p.Line - 1].Length) : p, shift);
        else if (Key(ImGuiKey.RightArrow))
            Move(HasSelection && !shift ? Selection.End : ctrl ? WordRight(p) : p.Col < _lines[p.Line].Length ? p with { Col = p.Col + 1 } : p.Line < _lines.Count - 1 ? new Pos(p.Line + 1, 0) : p, shift);
        else if (alt && Key(ImGuiKey.UpArrow))
            MoveLines(-1);
        else if (alt && Key(ImGuiKey.DownArrow))
            MoveLines(1);
        else if (Key(ImGuiKey.UpArrow) || Key(ImGuiKey.DownArrow) || Key(ImGuiKey.PageUp) || Key(ImGuiKey.PageDown))
        {
            var lines = Key(ImGuiKey.UpArrow) ? -1 : Key(ImGuiKey.DownArrow) ? 1 : Key(ImGuiKey.PageUp) ? -visibleLines : visibleLines;
            if (_preferredCol < 0)
                _preferredCol = p.Col;
            Move(new Pos(p.Line + lines, _preferredCol), shift);
            keepColumn = true;
        }
        else if (Key(ImGuiKey.Home))
        {
            var indent = Indent(_lines[p.Line]);
            Move(ctrl ? new Pos(0, 0) : p with { Col = p.Col == indent ? 0 : indent }, shift);
        }
        else if (Key(ImGuiKey.End))
            Move(ctrl ? End : p with { Col = _lines[p.Line].Length }, shift);
        else if (Key(ImGuiKey.Backspace))
            Backspace(ctrl);
        else if (Key(ImGuiKey.Delete))
            DeleteForward(ctrl);
        else if (Key(ImGuiKey.Enter) || Key(ImGuiKey.KeypadEnter))
            NewLine();
        else if (Key(ImGuiKey.Tab))
        {
            var (s, e) = Selection;
            if (shift || s.Line != e.Line)
                IndentLines(outdent: shift);
            else
                Replace(new string(' ', 4 - p.Col % 4), Edit.Typing);
        }
        else if (ctrl && Key(ImGuiKey.A))
        {
            _anchor = new Pos(0, 0);
            _cursor = End;
        }
        else if (ctrl && Key(ImGuiKey.C))
            Copy(cut: false);
        else if (ctrl && Key(ImGuiKey.X))
            Copy(cut: true);
        else if (ctrl && Key(ImGuiKey.V))
            Paste();
        else if (ctrl && (Key(ImGuiKey.Y) || (shift && Key(ImGuiKey.Z))))
            Restore(_redo, _undo);
        else if (ctrl && Key(ImGuiKey.Z))
            Restore(_undo, _redo);
        else if (ctrl && Key(ImGuiKey.Slash))
            ToggleComment();
        else if (ctrl && Key(ImGuiKey.F))
            OpenFind();
        else if (Key(ImGuiKey.F3))
            FindNext(shift ? -1 : 1);
        else if (Key(ImGuiKey.Escape))
            _findOpen = false;
        if (!keepColumn && _cursor != p)
            _preferredCol = -1;
    }

    // --- Drawing ------------------------------------------------------------------------------------

    static ImColor8 C(float r, float g, float b, float a = 1) => (ImColor8)ImGuiHost.Color(r, g, b, a);

    internal static readonly ImColor8[] TokenColors =
    [
        C(0.86f, 0.87f, 0.89f), // plain
        C(1.00f, 0.47f, 0.55f), // keyword
        C(0.56f, 0.94f, 0.85f), // type
        C(0.40f, 0.72f, 1.00f), // method
        C(1.00f, 0.88f, 0.60f), // string
        C(0.48f, 0.52f, 0.57f), // comment
        C(0.68f, 1.00f, 0.86f), // number
        C(0.80f, 0.62f, 1.00f), // preprocessor
    ];

    static readonly ImColor8 Background = C(0.055f, 0.06f, 0.075f);
    static readonly ImColor8 GutterBack = C(0.07f, 0.075f, 0.09f);
    static readonly ImColor8 LineNumber = C(0.36f, 0.39f, 0.44f);
    static readonly ImColor8 LineNumberCurrent = C(0.80f, 0.82f, 0.86f);
    static readonly ImColor8 CurrentLine = C(1f, 1f, 1f, 0.035f);
    static readonly ImColor8 SelectionColor = C(0.43f, 0.33f, 0.19f, 0.75f);
    static readonly ImColor8 MatchColor = C(0.88f, 0.66f, 0.36f, 0.22f);
    static readonly ImColor8 CaretColor = C(0.95f, 0.75f, 0.45f);
    static readonly ImColor8 ErrorColor = C(0.93f, 0.36f, 0.33f);
    static readonly ImColor8 WarningColor = C(0.95f, 0.72f, 0.33f);
    static readonly ImColor8 ErrorLine = C(0.93f, 0.36f, 0.33f, 0.07f);

    void EnsureStates(int upTo)
    {
        while (_states.Count < _lines.Count + 1)
            _states.Add(LexState.Normal);
        if (_states.Count > _lines.Count + 1)
            _states.RemoveRange(_lines.Count + 1, _states.Count - _lines.Count - 1);
        for (var l = _statesValid; l <= upTo && l < _lines.Count; l++)
            _states[l + 1] = CSharpLexer.Line(_lines[l], _states[l], null);
        _statesValid = Math.Max(_statesValid, Math.Min(upTo + 1, _lines.Count));
    }

    /// <summary>Draws the editor filling <paramref name="size"/>, and handles its input when focused.</summary>
    public void Draw(ImFontPtr font, float fontSize, float2 size)
    {
        if (_findOpen)
            DrawFindBar(ref size);

        ImGui.PushFont(font, fontSize);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, ImGuiHost.Color(0.055f, 0.06f, 0.075f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new float2(0, 4));
        ImGui.BeginChild("code", size, ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar | ImGuiWindowFlags.NoNav);
        if (_focusNext)
        {
            ImGui.SetWindowFocus();
            _focusNext = false;
        }
        HasFocus = ImGui.IsWindowFocused();

        // Measure and draw at the scaled size, so glyphs land where the columns are
        fontSize = ImGui.GetFontSize();
        var charW = font.CalcTextSizeA(fontSize, float.MaxValue, 0, "MMMMMMMMMM").X / 10;
        var lineH = ImGui.GetTextLineHeight() + 3;
        var digits = Math.Max(3, _lines.Count.ToString().Length);
        var gutter = (digits + 3) * charW;
        var origin = ImGui.GetCursorScreenPos();
        var winH = ImGui.GetWindowHeight();
        var winW = ImGui.GetWindowWidth();
        if (_widthVersion != _version)
        {
            _maxLength = _lines.Max(l => l.Length);
            _widthVersion = _version;
        }
        var avail = ImGui.GetContentRegionAvail();
        ImGui.InvisibleButton("##text", new float2(MathF.Max(gutter + (_maxLength + 4) * charW, avail.X), MathF.Max(_lines.Count * lineH + winH * 0.4f, avail.Y)));
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        var visibleLines = Math.Max(1, (int)(winH / lineH) - 1);

        // Mouse: click places the cursor, Shift+click and dragging select, double-click selects a word
        var mouse = ImGui.GetMousePos();
        Pos MouseAt() => Clamp(new Pos((int)((mouse.Y - origin.Y) / lineH), (int)MathF.Round((mouse.X - origin.X - gutter) / charW)));
        if (hovered)
            ImGui.SetMouseCursor(ImGuiMouseCursor.TextInput);
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            var at = MouseAt();
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                SelectWord(at);
            else
                Move(at, ImGui.GetIO().KeyShift);
            _preferredCol = -1;
        }
        else if (active && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            _cursor = MouseAt();
        }

        if (HasFocus && !_findActive)
            HandleKeys(visibleLines);

        // Keep the cursor in view after it moves
        var scrollX = ImGui.GetScrollX();
        var scrollY = ImGui.GetScrollY();
        // (not while the window is still opening, when its size isn't real yet)
        if (_scrollToCursor && winW > gutter + 20 * charW && winH > 4 * lineH)
        {
            _scrollToCursor = false;
            var top = _cursor.Line * lineH;
            if (top < scrollY)
                ImGui.SetScrollY(top);
            else if (top + 2 * lineH > scrollY + winH)
                ImGui.SetScrollY(top + 2 * lineH - winH);
            var x = _cursor.Col * charW;
            if (x < scrollX)
                ImGui.SetScrollX(Math.Max(0, x - 4 * charW));
            else if (x + gutter + 2 * charW > scrollX + winW)
                ImGui.SetScrollX(x + gutter + 6 * charW - winW);
        }

        var dl = ImGui.GetWindowDrawList();
        var first = Math.Max(0, (int)(scrollY / lineH) - 1);
        var last = Math.Min(_lines.Count - 1, first + (int)(winH / lineH) + 2);
        EnsureStates(last);
        var textX = origin.X + gutter;
        var (selStart, selEnd) = Selection;
        var needle = _findOpen ? _find.ToString() : "";
        string? tooltip = null;

        for (var l = first; l <= last; l++)
        {
            var y = origin.Y + l * lineH;
            var line = _lines[l];
            if (l == _cursor.Line && HasFocus && !HasSelection)
                dl.AddRectFilled(new float2(origin.X, y), new float2(origin.X + scrollX + winW, y + lineH), CurrentLine);

            var lineDiagnostics = Diagnostics.Where(d => d.Line == l).ToList();
            if (lineDiagnostics.Any(d => d.Error))
                dl.AddRectFilled(new float2(origin.X, y), new float2(origin.X + scrollX + winW, y + lineH), ErrorLine);

            // Selection, including the line break when it continues to the next line
            if (HasSelection && l >= selStart.Line && l <= selEnd.Line)
            {
                var a = l == selStart.Line ? selStart.Col : 0;
                var b = l == selEnd.Line ? selEnd.Col : line.Length + 1;
                dl.AddRectFilled(new float2(textX + a * charW, y), new float2(textX + b * charW, y + lineH), SelectionColor);
            }

            if (needle.Length > 0)
                for (var at = line.IndexOf(needle, StringComparison.OrdinalIgnoreCase); at >= 0; at = line.IndexOf(needle, at + 1, StringComparison.OrdinalIgnoreCase))
                    dl.AddRectFilled(new float2(textX + at * charW, y), new float2(textX + (at + needle.Length) * charW, y + lineH), MatchColor);

            // Text, one draw per run of tokens of the same colour
            _tokens.Clear();
            CSharpLexer.Line(line, _states[l], _tokens);
            for (var t = 0; t < _tokens.Count;)
            {
                var kind = _tokens[t].Kind;
                var start = _tokens[t].Start;
                var end = start + _tokens[t].Length;
                for (t++; t < _tokens.Count && _tokens[t].Kind == kind; t++)
                    end = _tokens[t].Start + _tokens[t].Length;
                dl.AddText(font, fontSize, new float2(textX + start * charW, y + 1.5f), TokenColors[(int)kind], line.AsSpan(start, end - start));
            }

            // Squiggles under what the compiler complained about
            foreach (var d in lineDiagnostics)
            {
                var a = Math.Min(d.Column, line.Length);
                var b = a;
                while (b < line.Length && Class(line[b]) == Class(line[Math.Min(a, line.Length - 1)]) && Class(line[b]) != 0)
                    b++;
                b = Math.Max(b, a + 1);
                var x0 = textX + a * charW;
                var x1 = textX + b * charW;
                var wy = y + lineH - 2;
                for (var x = x0; x < x1; x += 4)
                    dl.AddLine(new float2(x, wy), new float2(MathF.Min(x + 2, x1), wy - 2), d.Error ? ErrorColor : WarningColor, 1.2f);
                for (var x = x0 + 2; x < x1; x += 4)
                    dl.AddLine(new float2(x, wy - 2), new float2(MathF.Min(x + 2, x1), wy), d.Error ? ErrorColor : WarningColor, 1.2f);
            }
            if (hovered && lineDiagnostics.Count > 0 && mouse.Y >= y && mouse.Y < y + lineH)
                tooltip = string.Join("\n", lineDiagnostics.Select(d => $"{(d.Error ? "error" : "warning")} {d.Code}: {d.Message}"));
        }

        if (HasFocus)
        {
            var cx = textX + _cursor.Col * charW;
            var cy = origin.Y + _cursor.Line * lineH;
            dl.AddRectFilled(new float2(cx - 1, cy + 1), new float2(cx + 1, cy + lineH - 1), CaretColor);
        }

        // The gutter stays put when scrolling sideways, over the text
        var gx = origin.X + scrollX;
        dl.AddRectFilled(new float2(gx, origin.Y + first * lineH - 8), new float2(gx + gutter - charW, origin.Y + (last + 2) * lineH), GutterBack);
        for (var l = first; l <= last; l++)
        {
            var y = origin.Y + l * lineH;
            var number = (l + 1).ToString();
            dl.AddText(font, fontSize, new float2(gx + (digits + 1 - number.Length) * charW, y + 1.5f), l == _cursor.Line ? LineNumberCurrent : LineNumber, number);
            var worst = Diagnostics.Where(d => d.Line == l).OrderByDescending(d => d.Error).FirstOrDefault();
            if (worst.File != null)
                dl.AddRectFilled(new float2(gx + 3, y + 4), new float2(gx + 3 + charW * 0.6f, y + lineH - 4), worst.Error ? ErrorColor : WarningColor, 2f);
        }

        ImGui.EndChild();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
        ImGui.PopFont();
        if (tooltip != null)
            ImGui.SetTooltip(tooltip);
    }

    void DrawFindBar(ref float2 size)
    {
        var start = ImGui.GetCursorPosY();
        if (_focusFind)
        {
            ImGui.SetKeyboardFocusHere();
            _focusFind = false;
        }
        ImGui.SetNextItemWidth(260);
        if (ImGui.InputText("##find", _find, ImGuiInputTextFlags.EnterReturnsTrue))
        {
            FindNext(ImGui.GetIO().KeyShift ? -1 : 1);
            _focusFind = true; // stay in the box for the next Enter
        }
        _findActive = ImGui.IsItemActive();
        if (_findActive && ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            _findOpen = false;
            _focusNext = true;
        }
        ImGui.SameLine();
        if (ImGui.Button("Previous"))
            FindNext(-1);
        ImGui.SameLine();
        if (ImGui.Button("Next"))
            FindNext(1);
        ImGui.SameLine();
        var needle = _find.ToString();
        var matches = needle.Length == 0 ? 0 : _lines.Sum(l => Count(l, needle));
        ImGui.TextDisabled(needle.Length == 0 ? "Find (Enter: next, Shift+Enter: previous)" : $"{matches} found");
        ImGui.SameLine();
        if (ImGui.Button("x"))
        {
            _findOpen = false;
            _focusNext = true;
        }
        size.Y -= ImGui.GetCursorPosY() - start;
    }

    static int Count(string line, string needle)
    {
        var n = 0;
        for (var at = line.IndexOf(needle, StringComparison.OrdinalIgnoreCase); at >= 0; at = line.IndexOf(needle, at + 1, StringComparison.OrdinalIgnoreCase))
            n++;
        return n;
    }
}
