using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Trinity.Editor;
using Trinity.Scenes;

namespace Trinity.Tests;

/// <summary>
/// The FileSystem dock on a temporary project, with no window and no GPU, for tests. The editor object is
/// made without its constructor (which opens a window), the fields the dock uses are set by reflection,
/// and Dear ImGui runs headless: a real context and real frames with key and mouse events, but nothing is
/// rendered. So only code that never reaches the GPU can run here (a decoded thumbnail can't, a failed one can).
/// Not thread-safe: ImGui's context is global, so tests that use this must not run in parallel.
/// </summary>
sealed unsafe class HeadlessDock : IDisposable
{
    static readonly Type App = typeof(EditorApp);
    const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    public readonly object Editor = RuntimeHelpers.GetUninitializedObject(typeof(EditorApp));
    /// <summary>The project's folder; its parent holds the settings file and stands for "outside the project".</summary>
    public readonly string Root;
    public readonly string SettingsFile;
    /// <summary>Everything the editor logged while this was open, as "level: message".</summary>
    public readonly List<string> Logged = [];
    readonly Action<string, string, string> _log;
    bool _midFrame;

    public HeadlessDock(string dir)
    {
        Root = Path.Combine(dir, "project");
        Directory.CreateDirectory(Root);
        System.IO.File.WriteAllText(Path.Combine(Root, "trinity.json"), """{ "name": "Test" }""");
        SettingsFile = Path.Combine(dir, "settings.json");

        // What the constructor and the field initialisers would have set
        foreach (var f in App.GetFields(All & ~BindingFlags.Static))
        {
            if (f.FieldType == typeof(string) && f.GetValue(Editor) == null)
                f.SetValue(Editor, "");
            else if (f.FieldType == typeof(ImInputString))
                f.SetValue(Editor, new ImInputString(256));
            else if (f.GetValue(Editor) == null && f.FieldType is { IsGenericType: true, Namespace: "System.Collections.Generic" } && f.FieldType.GetConstructor(Type.EmptyTypes) is { } make)
                f.SetValue(Editor, make.Invoke(null));
        }
        var library = new SceneLibrary(Root);
        Set("_project", EditorProject.Load(Root));
        Set("_library", library);
        Set("_settings", typeof(Settings).GetConstructors(All & ~BindingFlags.Static).Single().Invoke([SettingsFile, new JsonObject()]));
        Set("_scene", EditorScene.New(library, "Scenes/main.scene.json"));
        Set("_fsTileSize", 72f);
        Set("_fsColumns", 1);
        Set("_fsViewLoaded", true);
        Set("_showFileSystem", true);
        Set("_fsShowPreview", true);
        Get<HashSet<string>>("_fsTreeOpen").Add("");

        // A context of its own, current from here on (CreateContext alone keeps the previous one current)
        ImGui.SetCurrentContext(ImGui.CreateContext());
        var io = ImGui.GetIO();
        io.IniFilename = null; // no imgui.ini in the test's folder
        io.DisplaySize = new float2(1000, 700);
        io.DeltaTime = 1f / 60;
        io.BackendFlags |= ImGuiBackendFlags.RendererHasTextures; // nothing uploads the font atlas; that's fine here
        io.ConfigErrorRecoveryEnableAssert = false; // a test that throws mid-frame mustn't abort the test run
        io.Fonts.AddFontDefault();
        _log = (level, _, message) => { lock (Logged) Logged.Add($"{level}: {message}"); };
        Log.Written += _log;
    }

    public void Dispose()
    {
        Log.Written -= _log;
        // A test that threw inside a frame left windows and IDs pushed: ending the frame recovers them
        // (error recovery is on, its asserts off), then the context can go
        if (_midFrame)
            ImGui.EndFrame();
        ImGui.DestroyContext();
    }

    // --- The editor's fields and methods ------------------------------------------------------------

    public T Get<T>(string field) => (T)App.GetField(field, All)!.GetValue(Editor)!;

    public void Set(string field, object? value) => App.GetField(field, All)!.SetValue(Editor, value);

    /// <summary>Calls one of the editor's methods by name; its own exception comes out, not a reflection wrapper.</summary>
    public object? Call(string method, params object?[] args)
    {
        // The one with that name taking these arguments (optional ones left out get their defaults)
        var m = App.GetMethods(All).Single(m => m.Name == method && m.GetParameters() is var ps
            && ps.Length >= args.Length && ps.Skip(args.Length).All(p => p.IsOptional));
        args = [.. args, .. m.GetParameters().Skip(args.Length).Select(_ => Type.Missing)];
        try
        {
            return m.Invoke(m.IsStatic ? null : Editor, args);
        }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

    /// <summary>A value of one of the editor's private enums, such as ("FsInput", "Rename").</summary>
    public static object Enum(string type, string value) => System.Enum.Parse(App.GetNestedType(type, All)!, value);

    /// <summary>The content view's items for the current folder, search and filter, as the dock lists them.</summary>
    public IList Entries() => (IList)Call("Visible", Get<string>("_fsDir"), true)!;

    public static string PathOf(object entry) => (string)entry.GetType().GetProperty("Path")!.GetValue(entry)!;

    public string File(string name) => Path.Combine(Root, name);

    public void Search(string text) => Get<ImInputString>("_fsSearch").SetValue(text.AsSpan());

    public HashSet<string> Selected => Get<HashSet<string>>("_fsSelected");

    /// <summary>Runs what the dock left in _afterTree, as the editor does after drawing: under Safely.</summary>
    public void RunAfterTree()
    {
        if (Get<Action?>("_afterTree") is { } after)
        {
            Set("_afterTree", null);
            Call("Safely", after);
        }
    }

    // --- Frames and input ---------------------------------------------------------------------------

    /// <summary>One ImGui frame with a full-size window (no title bar) at the top left, drawing <paramref name="draw"/>.</summary>
    public void Frame(Action? draw = null)
    {
        ImString.SharedStorage.Reset();
        ImGui.NewFrame();
        _midFrame = true;
        ImGui.SetNextWindowPos(float2.Zero);
        ImGui.SetNextWindowSize(new float2(1000, 700));
        ImGui.Begin("FileSystem", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoSavedSettings);
        draw?.Invoke();
        ImGui.End();
        ImGui.Render();
        _midFrame = false;
    }

    public void Frames(int count, Action? draw = null)
    {
        for (var i = 0; i < count; i++)
            Frame(draw);
    }

    /// <summary>A key press: down for one frame, then up for one.</summary>
    public void Press(ImGuiKey key, Action? draw = null)
    {
        ImGui.GetIO().AddKeyEvent(key, true);
        Frame(draw);
        ImGui.GetIO().AddKeyEvent(key, false);
        Frame(draw);
    }

    /// <summary>Holds a modifier (ImGuiKey.Mod_Ctrl, Mod_Shift) down or lets it go; io.KeyCtrl and so on follow on the next frame.</summary>
    public void Hold(ImGuiKey modifier, bool down)
    {
        ImGui.GetIO().AddKeyEvent(modifier, down);
        Frame();
    }

    /// <summary>A left click at a point: move there, down for a frame, up for a frame (ImGui buttons fire on the release).</summary>
    public void Click(float x, float y, Action draw)
    {
        var io = ImGui.GetIO();
        io.AddMousePosEvent(x, y);
        Frame(draw);
        io.AddMouseButtonEvent(0, true);
        Frame(draw);
        io.AddMouseButtonEvent(0, false);
        Frame(draw);
    }

    /// <summary>
    /// Answers the dock's name box (new item or rename) the way a person does: the box opens and selects
    /// its text, the text is typed over it, then Enter; then what the dock queued for after drawing runs.
    /// </summary>
    public void TypeInNameBox(string text)
    {
        void Box() => Call("FsInputBox", Entries());
        Frames(3, Box); // opens, then focuses the field with its text selected
        ImGui.GetIO().AddInputCharactersUTF8(text);
        Frames(2, Box);
        Press(ImGuiKey.Enter, Box);
        RunAfterTree();
        Call("FsRefresh");
    }

    /// <summary>Waits for the thumbnail decodes the dock started (they run on worker threads).</summary>
    public void WaitForDecodes()
    {
        foreach (var img in Get<IDictionary>("_images").Values)
            if (img.GetType().GetField("Decoding")!.GetValue(img) is Task t)
                ((IAsyncResult)t).AsyncWaitHandle.WaitOne(5000);
    }
}
