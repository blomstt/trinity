using System.Text;
using Brutal.ImGuiApi;
using Brutal.Numerics;

namespace Trinity.Editor.Scripting;

/// <summary>
/// The Script panel, Trinity's take on Godot's script editor: the project's scripts on the left, open files
/// in tabs, problems underneath. Saving a script (Ctrl+S) checks it with the compiler, or, while the game
/// runs, hot-reloads it into the game.
/// </summary>
sealed class ScriptPanel
{
    public const string Title = "Script";

    readonly EditorProject _project;
    readonly ImFontPtr _font;
    const float FontSize = 16f;
    public readonly ScriptIndex Index;
    public readonly ScriptChecker Checker;
    readonly List<CodeEditor> _open = [];
    CodeEditor? _active, _select, _closing;
    double _diskChecked;
    public bool Show = true;

    // The "new script" dialog, opened from here or from the Inspector
    readonly ImInputString _newName = new(64);
    bool _openNewScript;
    Action<string>? _onCreated;
    string _newError = "";

    public ScriptPanel(EditorProject project, ImFontPtr font)
    {
        _project = project;
        _font = font;
        Index = new ScriptIndex(project.Root);
        Checker = new ScriptChecker(project, Index);
    }

    public bool HasFocus => _open.Any(e => e.HasFocus);
    public bool AnyDirty => _open.Any(e => e.Dirty);

    public void Open(string path, int line = -1, int col = 0)
    {
        var editor = _open.FirstOrDefault(e => e.FilePath == path);
        if (editor == null)
        {
            editor = new CodeEditor(path);
            _open.Add(editor);
        }
        if (line >= 0)
            editor.GoTo(line, col);
        else
            editor.Focus();
        _select = editor;
        _focusEditor = editor;
        Show = true;
        _bringToFront = 10;
    }

    // Frames left to bring the panel forward: at start-up the saved layout picks its tabs first
    int _bringToFront;
    CodeEditor? _focusEditor;

    /// <summary>Asks for a name, creates <c>Scripts/&lt;Name&gt;.cs</c> from the template, opens it and reports the class name.</summary>
    public void NewScript(string suggested, Action<string>? created = null)
    {
        _newName.SetValue(suggested.AsSpan());
        _onCreated = created;
        _newError = "";
        _openNewScript = true;
    }

    public void SaveAll()
    {
        foreach (var e in _open.Where(e => e.Dirty))
            e.Save();
    }

    /// <summary>Unsaved edits are kept next to the file as <c>.unsaved</c> rather than lost or written over the script.</summary>
    public void KeepUnsaved()
    {
        foreach (var e in _open.Where(e => e.Dirty))
        {
            File.WriteAllText(e.FilePath + ".unsaved", e.Text);
            Log.Warn("editor", $"unsaved script changes kept in {_project.Relative(e.FilePath)}.unsaved");
        }
    }

    /// <summary>After a script is saved (the editor restarts a running game when there's no hot reload).</summary>
    public event Action? Saved;

    void Save(CodeEditor e)
    {
        e.Save();
        Log.Info("scripts", $"saved {_project.Relative(e.FilePath)}");
        Index.Refresh(0, force: true);
        Checker.Check();
        Saved?.Invoke();
    }

    bool _checkedOnce;

    public void Draw(double now, ImGuiID dockNextTo)
    {
        // Check the project once at start, so existing errors show (and Play's build is warm)
        if (!_checkedOnce)
        {
            _checkedOnce = true;
            Checker.Check();
        }
        DrawPopups();
        if (!Show)
            return;
        ImGui.SetNextWindowDockID(dockNextTo, ImGuiCond.FirstUseEver);
        if (_bringToFront > 0)
        {
            ImGui.SetNextWindowFocus();
            // Focusing the panel takes focus from the code, so give it back once done
            if (--_bringToFront == 0)
                _focusEditor?.Focus();
        }
        if (!ImGui.Begin(Title, ref Show))
        {
            ImGui.End();
            return;
        }
        Index.Refresh(now);
        if (now - _diskChecked > 1)
        {
            _diskChecked = now;
            foreach (var e in _open)
                e.ReloadIfChangedOnDisk();
        }

        FileList();
        ImGui.SameLine();
        ImGui.BeginGroup();
        Editors();
        ImGui.EndGroup();

        if (HasFocus && ImGui.GetIO().KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.S) && _active != null)
            Save(_active);
        ImGui.End();
    }

    void FileList()
    {
        ImGui.BeginChild("files", new float2(200, 0), ImGuiChildFlags.ResizeX);
        if (ImGui.Button("New script"))
            NewScript("NewScript");
        ImGui.SeparatorText("Scripts");
        var scripts = Index.All.ToList();
        if (scripts.Count == 0)
            ImGui.TextDisabled("None yet");
        foreach (var s in scripts)
        {
            if (ImGui.Selectable(s.Name, _active?.FilePath == s.File))
                Open(s.File, s.Line);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(_project.Relative(s.File));
        }
        if (ImGui.CollapsingHeader("Other C# files"))
        {
            var scriptFiles = scripts.Select(s => s.File).ToHashSet();
            foreach (var f in Index.SourceFiles().Where(f => !scriptFiles.Contains(f)).Order())
                if (ImGui.Selectable(_project.Relative(f), _active?.FilePath == f))
                    Open(f);
        }
        ImGui.EndChild();
    }

    void Editors()
    {
        if (_open.Count == 0)
        {
            ImGui.TextDisabled("Open a script from the list, or attach one to an object in the Inspector.");
            ImGui.TextDisabled(GameRunner.HotReload
                ? "Saving (Ctrl+S) checks it; while the game runs, saving applies the change to the running game."
                : "Saving (Ctrl+S) checks it; while the game runs, saving restarts it with the change.");
            return;
        }
        var problems = Checker.All;
        var lineH = ImGui.GetTextLineHeightWithSpacing();
        var problemsHeight = problems.Count == 0 ? 0 : Math.Min(problems.Count, 5) * lineH + 10;

        if (ImGui.BeginTabBar("tabs", ImGuiTabBarFlags.Reorderable | ImGuiTabBarFlags.FittingPolicyScroll))
        {
            foreach (var e in _open.ToList())
            {
                var open = true;
                var flags = e.Dirty ? ImGuiTabItemFlags.UnsavedDocument : ImGuiTabItemFlags.None;
                if (_select == e)
                    flags |= ImGuiTabItemFlags.SetSelected;
                if (ImGui.BeginTabItem($"{e.Name}###{e.FilePath}", ref open, flags))
                {
                    _active = e;
                    e.Diagnostics = Checker.For(e.FilePath);
                    var avail = ImGui.GetContentRegionAvail();
                    e.Draw(_font, FontSize, new float2(avail.X, avail.Y - lineH - problemsHeight - 4));
                    ImGui.TextDisabled($"Ln {e.Cursor.Line + 1}, Col {e.Cursor.Col + 1}");
                    ImGui.SameLine();
                    ImGui.TextDisabled($"  {_project.Relative(e.FilePath)}");
                    ImGui.SameLine();
                    var status = !Checker.Watching ? Checker.Status
                        : GameRunner.HotReload ? "game running: saving applies changes live"
                        : "game running: saving restarts it";
                    if (status.Length > 0)
                    {
                        ImGui.SameLine(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(status).X - 4);
                        var bad = Checker.All.Any(d => d.Error);
                        ImGui.TextColored(bad ? ImGuiHost.Color(0.93f, 0.40f, 0.36f) : ImGuiHost.Color(0.55f, 0.80f, 0.55f), status);
                    }
                    ImGui.EndTabItem();
                }
                if (!open)
                {
                    if (e.Dirty)
                        _closing = e;
                    else
                        _open.Remove(e);
                }
            }
            _select = null;
            ImGui.EndTabBar();
        }

        if (problems.Count > 0)
        {
            ImGui.BeginChild("problems", new float2(0, problemsHeight), ImGuiChildFlags.Borders);
            foreach (var d in problems)
            {
                var color = d.Error ? ImGuiHost.Color(0.93f, 0.40f, 0.36f) : ImGuiHost.Color(0.95f, 0.72f, 0.33f);
                ImGui.PushStyleColor(ImGuiCol.Text, color);
                if (ImGui.Selectable($"{Path.GetFileName(d.File)}:{d.Line + 1}  {d.Code}  {d.Message}##{d.File}{d.Line}{d.Column}{d.Code}"))
                    Open(d.File, d.Line, d.Column);
                ImGui.PopStyleColor();
            }
            ImGui.EndChild();
        }
    }

    /// <summary>Dialogs, drawn outside any panel so the Inspector can open them too.</summary>
    void DrawPopups()
    {
        if (_openNewScript)
        {
            ImGui.OpenPopup("New script");
            _openNewScript = false;
        }
        var keep = true;
        if (ImGui.BeginPopupModal("New script", ref keep, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text("Class name");
            if (ImGui.IsWindowAppearing())
                ImGui.SetKeyboardFocusHere();
            var enter = ImGui.InputText("##name", _newName, ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.TextDisabled($"Creates {_project.Relative(_project.ScriptsPath)}/{Clean(_newName.ToString())}.cs");
            if (_newError.Length > 0)
                ImGui.TextColored(ImGuiHost.Color(0.93f, 0.40f, 0.36f), _newError);
            if (ImGui.Button("Create") || enter)
            {
                var name = Clean(_newName.ToString());
                var path = Path.Combine(_project.ScriptsPath, name + ".cs");
                if (name.Length == 0 || CSharpLexer.IsKeyword(name))
                    _newError = "Give it a name that's a valid C# class name";
                else if (Index.Find(name) != null)
                    _newError = $"There's already a script called {name}";
                else
                {
                    if (!File.Exists(path))
                    {
                        Directory.CreateDirectory(_project.ScriptsPath);
                        File.WriteAllText(path, ScriptIndex.Template(name));
                    }
                    Index.Refresh(0, force: true);
                    Log.Info("scripts", $"created {_project.Relative(path)}");
                    _onCreated?.Invoke(name);
                    Open(path, 10, 5); // inside Ready()
                    ImGui.CloseCurrentPopup();
                }
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        if (_closing is { } closing)
        {
            ImGui.OpenPopup("Unsaved script");
            var stay = true;
            if (ImGui.BeginPopupModal("Unsaved script", ref stay, ImGuiWindowFlags.AlwaysAutoResize))
            {
                ImGui.Text($"Save changes to {closing.Name}?");
                if (ImGui.Button("Save"))
                {
                    Save(closing);
                    _open.Remove(closing);
                    _closing = null;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();
                if (ImGui.Button("Don't save"))
                {
                    _open.Remove(closing);
                    _closing = null;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel"))
                {
                    _closing = null;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.EndPopup();
            }
            if (!stay)
                _closing = null;
        }
    }

    /// <summary>A C# class name from what was typed: letters, digits and underscores, starting with a capital.</summary>
    public static string Clean(string name)
    {
        var sb = new StringBuilder();
        var upper = true;
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                sb.Append(upper ? char.ToUpperInvariant(c) : c);
                upper = false;
            }
            else
            {
                upper = true;
            }
        }
        var s = sb.ToString();
        return s.Length > 0 && char.IsDigit(s[0]) ? "_" + s : s;
    }
}
