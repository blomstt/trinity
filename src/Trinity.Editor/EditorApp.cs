using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Trinity.Graphics;
using Trinity.Editor.Scripting;
using Trinity.Scenes;

namespace Trinity.Editor;

/// <summary>Trinity Editor: one window with a viewport and docked panels, editing one project's scenes.</summary>
sealed partial class EditorApp : IDisposable
{
    const string ViewportPanel = "Viewport", HierarchyPanel = "Scene", InspectorPanel = "Inspector";
    const string LogPanel = "Log", StatsPanel = "Stats", EnvironmentPanel = "Environment";

    readonly Window _window;
    readonly GraphicsDevice _gpu;
    readonly ShaderLibrary _shaders;
    readonly ImGuiHost _ui;
    readonly SceneView _view;
    readonly GameRunner _runner = new();
    readonly ScriptPanel _scripts;
    ImGuiID _viewportDock;
    readonly Dictionary<string, ImInputString> _stringFields = [];
    readonly LogBuffer _log = new();
    readonly EditorProject _project;
    EditorScene _scene;
    SceneLibrary _library;
    ImTextureRef? _viewportTexture;
    int _viewportWidth = 1280, _viewportHeight = 720;
    bool _layoutBuilt;
    bool _showHierarchy = true, _showInspector = true, _showLog = true, _showStats = true, _showEnvironment = true;
    ImGuiID _inspectorDock;
    int _dragAxis = -1;
    readonly ImInputString _saveAsEdit = new(128);
    List<string> _assetFiles = [], _sceneFiles = [];
    double _assetsScanned = -10;

    readonly Settings _settings;

    /// <summary>Set when the editor closed to go back to the Project Manager rather than to quit.</summary>
    public bool BackToProjects { get; private set; }

    public EditorApp(EditorProject project, Settings settings, bool splash = true)
    {
        _project = project;
        _settings = settings;
        _window = new Window($"Trinity Editor - {project.Name}", 1600, 950, appId: "trinity-editor");
        _gpu = new GraphicsDevice(_window, vsync: true);
        _shaders = new ShaderLibrary(_gpu);
        var config = Path.Combine(
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
            "trinity-editor"
        );
        _ui = new ImGuiHost(_window, _gpu, config, "editor-layout.ini");
        _scripts = new ScriptPanel(project, _ui.Mono);
        _runner.Output += _scripts.Checker.Feed;
        _scripts.Saved += () =>
        {
            if (_runner.Running && !GameRunner.HotReload)
                _runner.Restart(_project);
        };
        // BRUTAL License 2.2: the logo before the editor's main function
        if (splash && Environment.GetEnvironmentVariable("TRINITY_SKIP_SPLASH") != "1")
            Splash.Show(_window, _gpu, _shaders);
        _view = new SceneView(_gpu, _shaders, project);
        _view.AssetReady += Window.Wake;

        _library = new SceneLibrary(project.Root);
        LoadFavorites();
        var main = SceneLibrary.Normalize(Path.Combine(project.Scenes, "main.scene.json"));
        _scene = File.Exists(project.Absolute(main)) ? LoadScene(main) : EditorScene.New(_library, main);
        UseSceneCamera();
        Log.Info("editor", $"project {project.Name} at {project.Root}, scene {_scene.Path}");
    }

    /// <summary>Opens a script in the Script panel: a path relative to the project, optionally with :line.</summary>
    public void OpenScript(string spec)
    {
        var colon = spec.LastIndexOf(':');
        var line = colon > 0 && int.TryParse(spec[(colon + 1)..], out var l) ? l - 1 : -1;
        var file = _project.Absolute(line >= 0 ? spec[..colon] : spec);
        if (File.Exists(file))
            _pendingScript = (file, line);
        else
            Log.Error("editor", $"no script at {file}");
    }

    (string File, int Line)? _pendingScript;

    /// <summary>Selects a node by name.</summary>
    public void Select(string name)
    {
        var e = _scene.World.All().FirstOrDefault(n => _scene.World.Name(n) == name);
        if (e.IsNone)
            Log.Error("editor", $"no node called {name} in the scene");
        _scene.Select(e);
        _focusInspector = e.IsNone ? 0 : 3; // a few frames: on the first, docking isn't set up yet
        _revealSelection = true;
    }

    int _focusInspector;

    /// <summary>For checking the editor without a person: save the window to a PNG after a while, then quit.</summary>
    public (string Path, double After)? Screenshot;

    public void Run()
    {
        var clock = Stopwatch.StartNew();
        var last = 0.0;
        double lastActivity = 0, lastFrame = 0;
        _window.Activity += () => lastActivity = clock.Elapsed.TotalSeconds;
        Log.Written += WakeOnLog;
        while (!_window.ShouldClose)
        {
            // Draw only when something changed: input in the last half second, playing animations, a
            // new log line, or a once-a-second refresh for the stats. Otherwise don't draw at all:
            // on Wayland each presented frame wakes the event wait again, so waiting isn't enough.
            Window.PollEvents();
            var t = clock.Elapsed.TotalSeconds;
            var changed = Animating() || t - lastActivity < 0.5 || _logChanged || _view.Loading || Screenshot != null;
            if (!changed && t - lastFrame < 1.0)
            {
                Window.WaitEvents(1.0 - (t - lastFrame));
                continue;
            }
            _logChanged = false;
            lastFrame = t;
            var now = clock.Elapsed.TotalSeconds;
            var dt = (float)Math.Min(now - last, 0.1);
            last = now;

            if (!_gpu.BeginFrame(out var cmd))
                continue;

            // The viewport is resized before the UI refers to it this frame
            if (_view.Resize(_viewportWidth, _viewportHeight))
            {
                if (_viewportTexture is { } old)
                    _ui.RemoveTexture(old);
                _viewportTexture = _ui.AddTexture(_view.Sampler, _view.Output!.View);
            }

            _ui.NewFrame();
            DrawUi();
            _view.Render(cmd, _scene, dt, now);
            _ui.Render(cmd);
            var shoot = Screenshot is { } shot && now >= shot.After;
            // Sized now, not at start: the window manager may have resized the window since
            using var capture = shoot ? Screenshots.Buffer(_gpu) : null;
            _gpu.EndFrame(cmd, capture, endRendering: false);
            if (shoot)
            {
                Screenshots.Save(_gpu, capture!, Screenshot!.Value.Path);
                break;
            }

            _window.Title = $"Trinity Editor - {_project.Name} - {Path.GetFileName(_scene.Path)}{(_scene.Dirty ? " *" : "")}";
            if (_scene.Dirty && now - _autosaved > 120)
                Autosave(now);
        }

        Log.Written -= WakeOnLog;
        _scripts.KeepUnsaved();
        if (_scene.Dirty)
            Log.Warn("editor", $"unsaved changes kept in {Autosave(0)}");
    }

    double _autosaved;

    /// <summary>Writes unsaved changes beside the scene (not over it), every couple of minutes and on quit.</summary>
    string Autosave(double now)
    {
        _autosaved = now;
        var backup = _scene.Path.Replace(".scene.json", ".autosave.scene.json");
        try
        {
            _library.Save(backup, _scene.ToDocument());
        }
        catch (Exception e)
        {
            Log.Error("editor", $"autosave failed: {e.Message}");
        }
        return backup;
    }

    bool Animating()
    {
        foreach (ref var a in _scene.World.Store<Animator>().Components)
            if (!a.Paused)
                return true;
        return false;
    }

    volatile bool _logChanged;

    void WakeOnLog(string level, string category, string message)
    {
        _logChanged = true;
        Window.Wake();
    }

    // --- Layout and menus ---------------------------------------------------------------------------

    void DrawUi()
    {
        var dock = ImGui.DockSpaceOverViewport(default, ImGui.GetMainViewport());
        if (!_layoutBuilt)
        {
            _layoutBuilt = true;
            if (_ui.FirstRun)
                BuildDefaultLayout(dock);
        }

        _scripts.Checker.Watching = _runner.Running;
        MenuBar();
        Shortcuts();
        Viewport();
        if (_pendingScript is { } pending)
        {
            _scripts.Open(pending.File, pending.Line);
            _pendingScript = null;
        }
        _scripts.Draw(ImGui.GetTime(), _viewportDock);
        if (_showHierarchy) Hierarchy();
        if (_showInspector) Inspector();
        if (_showFileSystem) FileSystem();
        if (_showHistory) History();
        if (_showLog) LogWindow();
        if (_showStats) Stats();
        if (_showEnvironment) EnvironmentWindow();
        // Changes panels asked for while they were drawing (which can replace the nodes they show)
        if (_afterTree is { } after)
        {
            _afterTree = null;
            after();
        }
        // Edits from one drag or one text entry are one undo step: it ends when nothing is being edited
        if (!ImGui.IsAnyItemActive() && _dragAxis < 0)
            _scene.CloseEdit();
    }

    /// <summary>Scene above FileSystem and History on the left, Inspector right, Log below, viewport in the middle.</summary>
    static void BuildDefaultLayout(ImGuiID dock)
    {
        ImGui.Internal.DockBuilderSplitNode(dock, ImGuiDir.Left, 0.19f, out var left, out var rest);
        ImGui.Internal.DockBuilderSplitNode(rest, ImGuiDir.Right, 0.25f, out var right, out var middle);
        ImGui.Internal.DockBuilderSplitNode(middle, ImGuiDir.Down, 0.24f, out var bottom, out var centre);
        ImGui.Internal.DockBuilderSplitNode(right, ImGuiDir.Down, 0.30f, out var rightBottom, out var rightTop);
        ImGui.Internal.DockBuilderSplitNode(left, ImGuiDir.Down, 0.5f, out var leftBottom, out var leftTop);
        ImGui.Internal.DockBuilderDockWindow(HierarchyPanel, leftTop);
        ImGui.Internal.DockBuilderDockWindow(HistoryPanel, leftBottom);
        ImGui.Internal.DockBuilderDockWindow(FileSystemPanel, leftBottom);
        ImGui.Internal.DockBuilderDockWindow(EnvironmentPanel, rightTop);
        ImGui.Internal.DockBuilderDockWindow(InspectorPanel, rightTop);
        ImGui.Internal.DockBuilderDockWindow(StatsPanel, rightBottom);
        ImGui.Internal.DockBuilderDockWindow(LogPanel, bottom);
        ImGui.Internal.DockBuilderDockWindow(ScriptPanel.Title, centre);
        ImGui.Internal.DockBuilderDockWindow(ViewportPanel, centre);
        ImGui.Internal.DockBuilderFinish(dock);
    }

    void MenuBar()
    {
        var openSaveAs = false;
        var openAbout = false;
        if (!ImGui.BeginMainMenuBar())
            return;
        if (ImGui.BeginMenu("File"))
        {
            if (ImGui.MenuItem("New scene", "Ctrl+N")) NewScene();
            if (ImGui.BeginMenu("Open scene"))
            {
                ScanFiles();
                if (_sceneFiles.Count == 0)
                    ImGui.TextDisabled("No scenes yet");
                foreach (var s in _sceneFiles)
                    if (ImGui.MenuItem(s, default, s == _scene.Path))
                        OpenScene(_project.Absolute(s));
                ImGui.EndMenu();
            }
            if (ImGui.MenuItem("Save scene", "Ctrl+S")) SaveScene();
            if (ImGui.MenuItem("Save scene as...")) openSaveAs = true;
            ImGui.Separator();
            if (ImGui.MenuItem("Quit to Project List"))
            {
                BackToProjects = true;
                _window.ShouldClose = true;
            }
            if (ImGui.MenuItem("Quit", "Ctrl+Q")) _window.ShouldClose = true;
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Edit"))
        {
            var any = _scene.Selection.Count > 0;
            if (ImGui.MenuItem(_scene.UndoLabel is { } u ? $"Undo {u}" : "Undo", "Ctrl+Z", false, _scene.CanUndo)) _scene.Undo();
            if (ImGui.MenuItem(_scene.RedoLabel is { } r ? $"Redo {r}" : "Redo", "Ctrl+Y", false, _scene.CanRedo)) _scene.Redo();
            ImGui.Separator();
            if (ImGui.MenuItem("New node", "Ctrl+Shift+N")) _scene.CreateNode("Node", _scene.Primary);
            if (ImGui.MenuItem("Rename", "F2", false, !_scene.Primary.IsNone)) _renameRequested = true;
            if (ImGui.MenuItem("Duplicate", "Ctrl+D", false, any)) _scene.Duplicate(_scene.Selection);
            if (ImGui.MenuItem("Delete", "Del", false, any)) Report(_scene.Delete(_scene.Selection));
            if (ImGui.MenuItem("Frame selected", "F", false, !_scene.Primary.IsNone)) _view.Frame(_scene.World, _scene.Primary);
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("View"))
        {
            ImGui.MenuItem(HierarchyPanel, default, ref _showHierarchy);
            ImGui.MenuItem(InspectorPanel, default, ref _showInspector);
            ImGui.MenuItem(FileSystemPanel, default, ref _showFileSystem);
            ImGui.MenuItem(HistoryPanel, default, ref _showHistory);
            ImGui.MenuItem(LogPanel, default, ref _showLog);
            ImGui.MenuItem(StatsPanel, default, ref _showStats);
            ImGui.MenuItem(EnvironmentPanel, default, ref _showEnvironment);
            ImGui.Separator();
            if (ImGui.BeginMenu("Viewport shading"))
            {
                foreach (var (name, mode) in new[] { ("Lit", Trinity.Rendering.DebugView.None), ("Albedo", Trinity.Rendering.DebugView.Albedo), ("Normals", Trinity.Rendering.DebugView.Normals), ("Shadow cascades", Trinity.Rendering.DebugView.Cascades) })
                    if (ImGui.MenuItem(name, default, _view.DebugView == mode))
                        _view.DebugView = mode;
                ImGui.EndMenu();
            }
            ImGui.MenuItem(ScriptPanel.Title, default, ref _scripts.Show);
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Help"))
        {
            if (ImGui.MenuItem("About Trinity")) openAbout = true;
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Run"))
        {
            if (ImGui.MenuItem("Play", "F5", false, !_runner.Running)) Play();
            if (ImGui.MenuItem("Stop", "Shift+F5", false, _runner.Running)) _runner.Stop();
            ImGui.EndMenu();
        }

        // Play / Stop in the middle of the bar
        ImGui.SetCursorPosX(ImGui.GetWindowWidth() / 2 - 60);
        if (_runner.Running)
        {
            if (ImGui.Button("  Stop  ")) _runner.Stop();
            ImGui.SameLine();
            ImGui.TextColored(ImGuiHost.Color(0.88f, 0.66f, 0.36f), "running");
        }
        else if (ImGui.Button("  Play  "))
        {
            Play();
        }
        ImGui.EndMainMenuBar();

        if (openAbout)
            ImGui.OpenPopup("About Trinity");
        if (ImGui.BeginPopup("About Trinity"))
        {
            ImGui.Text($"Trinity Editor {typeof(EditorApp).Assembly.GetName().Version?.ToString(3) ?? ""}");
            ImGui.TextDisabled("Copyright 2026 blomst. PolyForm Noncommercial 1.0.0; commercial licences: github.com/blomstt/trinity");
            ImGui.Separator();
            ImGui.Text("Built with BRUTAL");
            foreach (var line in Credits.Required)
                ImGui.TextDisabled(line);
            ImGui.TextDisabled($"Third-party licences: {Path.Combine(AppContext.BaseDirectory, "licenses")}");
            ImGui.EndPopup();
        }
        if (openSaveAs)
        {
            _saveAsEdit.SetValue(Path.GetFileName(_scene.Path).Replace(".scene.json", "").AsSpan());
            ImGui.OpenPopup("Save scene as");
        }
        if (ImGui.BeginPopup("Save scene as"))
        {
            ImGui.Text("Scene name");
            var enter = ImGui.InputText("##name", _saveAsEdit, ImGuiInputTextFlags.EnterReturnsTrue);
            if ((ImGui.Button("Save") || enter) && !_saveAsEdit.IsEmpty)
            {
                SaveScene(SceneLibrary.Normalize(Path.Combine(_project.Scenes, _saveAsEdit.ToString() + ".scene.json")));
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
    }

    void Shortcuts()
    {
        var io = ImGui.GetIO();
        if (ImGui.IsKeyPressed(ImGuiKey.F5, false))
        {
            if (io.KeyShift) _runner.Stop();
            else Play();
        }
        if (io.WantTextInput || _scripts.HasFocus)
            return;
        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.S)) SaveScene();
        if (io.KeyCtrl && !io.KeyShift && ImGui.IsKeyPressed(ImGuiKey.N)) NewScene();
        if (io.KeyCtrl && io.KeyShift && ImGui.IsKeyPressed(ImGuiKey.N)) _scene.CreateNode("Node", _scene.Primary);
        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.Q)) _window.ShouldClose = true;
        if (io.KeyCtrl && !io.KeyShift && ImGui.IsKeyPressed(ImGuiKey.Z)) _scene.Undo();
        if (io.KeyCtrl && (ImGui.IsKeyPressed(ImGuiKey.Y) || io.KeyShift && ImGui.IsKeyPressed(ImGuiKey.Z))) _scene.Redo();
        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.D) && _scene.Selection.Count > 0) _scene.Duplicate(_scene.Selection);
        if (ImGui.IsKeyPressed(ImGuiKey.Delete) && _scene.Selection.Count > 0) Report(_scene.Delete(_scene.Selection));
        if (ImGui.IsKeyPressed(ImGuiKey.F2) && !_scene.Primary.IsNone) _renameRequested = true;
    }

    /// <summary>Saves the scene and scripts, then runs the game.</summary>
    void Play()
    {
        if (_runner.Running)
            return;
        if (_scene.Dirty)
            SaveScene();
        _scripts.SaveAll();
        _scripts.Checker.ClearRuntime();
        _runner.Play(_project);
    }

    // --- Panels --------------------------------------------------------------------------------------

    void Viewport()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new float2(0, 0));
        ImGui.Begin(ViewportPanel);
        _viewportDock = ImGui.GetWindowDockID();
        var size = ImGui.GetContentRegionAvail();
        _viewportWidth = Math.Max(8, (int)size.X);
        _viewportHeight = Math.Max(8, (int)size.Y);
        var origin = ImGui.GetCursorScreenPos();
        if (_viewportTexture is { } texture)
            ImGui.Image(texture, size);
        // Owns every mouse button over the image, so dragging there never moves the panel
        ImGui.SetCursorScreenPos(origin);
        ImGui.InvisibleButton("##viewport", size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);

        // Left click selects (or grabs the move gizmo); right drag orbits, middle drag (or Shift + right)
        // pans, the wheel zooms; F frames the selection
        var io = ImGui.GetIO();
        var mouse = new Vector2(io.MousePos.X - origin.X, io.MousePos.Y - origin.Y);
        // A model or scene dragged from FileSystem lands where the mouse points on the ground
        if (ImGui.BeginDragDropTarget())
        {
            AcceptAssetDrop(EntityId.None, GroundUnder(mouse));
            ImGui.EndDragDropTarget();
        }
        MoveGizmo(mouse, ImGui.IsItemHovered());
        if (ImGui.IsItemHovered())
        {
            var d = io.MouseDelta;
            ref var cam = ref _view.Camera;
            var pan = ImGui.IsMouseDown(ImGuiMouseButton.Middle) || (io.KeyShift && ImGui.IsMouseDown(ImGuiMouseButton.Right));
            if (pan)
            {
                var (forward, right) = cam.GroundAxes;
                var speed = cam.Distance * 0.0016f;
                cam.Target += (-right * d.X + forward * d.Y) * speed;
            }
            else if (ImGui.IsMouseDown(ImGuiMouseButton.Right))
            {
                cam.Yaw -= d.X * 0.008f;
                cam.Pitch = Math.Clamp(cam.Pitch + d.Y * 0.006f, -1.4f, 1.5f);
            }
            if (io.MouseWheel != 0)
                cam.Distance = Math.Clamp(cam.Distance * MathF.Pow(0.88f, io.MouseWheel), 0.3f, 1500f);
            if (ImGui.IsKeyPressed(ImGuiKey.F) && !_scene.Primary.IsNone && !io.WantTextInput)
                _view.Frame(_scene.World, _scene.Primary);
        }
        ImGui.End();
        ImGui.PopStyleVar();
    }

    /// <summary>
    /// Hovering, grabbing and dragging the selected node's move arrows (in world axes; the node's local
    /// position changes to match, under whatever parent it has). A drag is one undo step. A click
    /// elsewhere picks (with Ctrl, adds to or removes from the selection).
    /// </summary>
    void MoveGizmo(Vector2 mouse, bool hovered)
    {
        var io = ImGui.GetIO();
        var w = _scene.World;
        var selected = _scene.Primary;
        if (_dragAxis >= 0)
        {
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left) || !w.IsAlive(selected))
            {
                _dragAxis = -1;
                _scene.CloseEdit();
                return;
            }
            var at = w.WorldPosition(selected);
            var (p0, p1) = GizmoAxisOnScreen(at, _dragAxis);
            if (p0 is { } a && p1 is { } b)
            {
                var s = b - a;
                var t = Vector2.Dot(new Vector2(io.MouseDelta.X, io.MouseDelta.Y), s) / MathF.Max(s.LengthSquared(), 1);
                ReadOnlySpan<Vector3> axes = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
                var delta = axes[_dragAxis] * _view.GizmoLength(at) * t;
                // World movement into the parent's space
                var parent = w.Parent(selected);
                if (!parent.IsNone && Matrix4x4.Invert(w.WorldMatrix(parent), out var inv))
                    delta = Vector3.TransformNormal(delta, inv);
                if (delta != Vector3.Zero)
                    Report(_scene.SetProperty(selected, "Position", SceneSchema.Vector(w.Position(selected) + delta), merge: true));
            }
            return;
        }
        _view.GizmoAxis = hovered && w.IsAlive(selected) && !w.Locked(selected) ? HoveredAxis(w.WorldPosition(selected), mouse) : -1;
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            if (_view.GizmoAxis >= 0)
            {
                _dragAxis = _view.GizmoAxis;
                _scene.CloseEdit();
            }
            else
            {
                var picked = _view.Pick(w, _view.Ray(mouse));
                if (!picked.IsNone || !io.KeyCtrl)
                    _scene.Select(picked, add: io.KeyCtrl);
                _revealSelection = !picked.IsNone;
            }
        }
    }

    /// <summary>Where the ray through a viewport pixel meets the ground (y = 0), or the camera's target if it doesn't.</summary>
    Vector3 GroundUnder(Vector2 pixel)
    {
        var (origin, direction) = _view.Ray(pixel);
        if (direction.Y > -1e-4f)
            return NewNodePosition();
        var t = -origin.Y / direction.Y;
        return t is > 0 and < 5000 ? origin + direction * t : NewNodePosition();
    }

    (Vector2?, Vector2?) GizmoAxisOnScreen(Vector3 at, int axis)
    {
        ReadOnlySpan<Vector3> axes = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
        return (_view.ToScreen(at), _view.ToScreen(at + axes[axis] * _view.GizmoLength(at)));
    }

    int HoveredAxis(Vector3 at, Vector2 mouse)
    {
        var best = -1;
        var bestDistance = 8f; // pixels
        for (var a = 0; a < 3; a++)
        {
            if (GizmoAxisOnScreen(at, a) is not ({ } p0, { } p1))
                continue;
            var s = p1 - p0;
            var t = Math.Clamp(Vector2.Dot(mouse - p0, s) / MathF.Max(s.LengthSquared(), 1), 0, 1);
            var d = Vector2.Distance(mouse, p0 + s * t);
            if (d < bestDistance)
                (best, bestDistance) = (a, d);
        }
        return best;
    }

    static void Report(string? error)
    {
        if (error != null)
            Log.Warn("editor", error);
    }

    void EnvironmentWindow()
    {
        ImGui.SetNextWindowDockID(_inspectorDock, ImGuiCond.FirstUseEver);
        if (!ImGui.Begin(EnvironmentPanel, ref _showEnvironment))
        {
            ImGui.End();
            return;
        }
        var e = _scene.Environment;
        var before = _scene.EnvironmentJson();
        var changed = false;
        ImGui.SeparatorText("Sun");
        var azimuth = e.SunAzimuth;
        if (ImGui.DragFloat("Azimuth", ref azimuth, 0.5f, 0, 360, "%.1f°")) { e.SunAzimuth = azimuth; changed = true; }
        var elevation = e.SunElevation;
        if (ImGui.DragFloat("Elevation", ref elevation, 0.2f, -10, 90, "%.1f°")) { e.SunElevation = elevation; changed = true; }
        var illuminance = e.SunIlluminance;
        if (ImGui.DragFloat("Illuminance", ref illuminance, 0.05f, 0, 100)) { e.SunIlluminance = illuminance; changed = true; }
        ImGui.SeparatorText("Sky and fog");
        var haze = e.Haze;
        if (ImGui.DragFloat("Haze", ref haze, 0.02f, 0, 20)) { e.Haze = haze; changed = true; }
        var fog = e.FogDensity * 1000;
        if (ImGui.DragFloat("Fog density", ref fog, 0.05f, 0, 100, "%.2f / km")) { e.FogDensity = fog / 1000; changed = true; }
        var falloff = e.FogHeightFalloff;
        if (ImGui.DragFloat("Fog height falloff", ref falloff, 0.002f, 0, 1)) { e.FogHeightFalloff = falloff; changed = true; }
        ImGui.SeparatorText("Shadows");
        var shadows = e.Shadows;
        if (ImGui.Checkbox("Sun shadows", ref shadows)) { e.Shadows = shadows; changed = true; }
        var distance = e.ShadowDistance;
        if (ImGui.DragFloat("Distance", ref distance, 0.5f, 5, 1000, "%.0f m")) { e.ShadowDistance = distance; changed = true; }
        ImGui.SeparatorText("Camera");
        var exposure = e.Exposure;
        if (ImGui.DragFloat("Exposure", ref exposure, 0.02f, -10, 10, "%+.2f stops")) { e.Exposure = exposure; changed = true; }
        var aces = e.ToneMapper.Equals("ACES", StringComparison.OrdinalIgnoreCase);
        if (ImGui.BeginCombo("Tone mapper", aces ? "ACES" : "AgX"))
        {
            if (ImGui.Selectable("AgX", !aces)) { e.ToneMapper = "AgX"; changed = true; }
            if (ImGui.Selectable("ACES", aces)) { e.ToneMapper = "ACES"; changed = true; }
            ImGui.EndCombo();
        }
        // A drag is one undo step: edits merge until no widget is active (see DrawUi)
        if (changed)
            _scene.EnvironmentChanged(before, merge: true);
        ImGui.End();
    }

    /// <summary>"DegreesPerSecond" as "Degrees per second".</summary>
    static string Words(string name)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
                sb.Append(' ').Append(char.ToLowerInvariant(name[i]));
            else
                sb.Append(name[i]);
        }
        return sb.ToString();
    }

    void ScanFiles()
    {
        if (ImGui.GetTime() - _assetsScanned < 2)
            return;
        _assetsScanned = ImGui.GetTime();
        _assetFiles = Directory.Exists(_project.AssetsPath)
            ? Directory.EnumerateFiles(_project.AssetsPath, "*", SearchOption.AllDirectories).Select(f => SceneLibrary.Normalize(_project.Relative(f))).Order().ToList()
            : [];
        _sceneFiles = Directory.Exists(_project.ScenesPath)
            ? Directory.EnumerateFiles(_project.ScenesPath, "*.scene.json", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".autosave.scene.json", StringComparison.Ordinal))
                .Select(f => SceneLibrary.Normalize(_project.Relative(f))).Order().ToList()
            : [];
    }

    static bool IsModel(string file) =>
        file.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase);

    void LogWindow()
    {
        ImGui.Begin(LogPanel, ref _showLog);
        if (ImGui.Button("Clear"))
            _log.Clear();
        ImGui.Separator();
        ImGui.BeginChild("lines");
        var atBottom = ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 4;
        foreach (var (level, category, message) in _log.Snapshot())
        {
            var color = level switch
            {
                "error" => ImGuiHost.Color(0.93f, 0.40f, 0.36f),
                "warn" => ImGuiHost.Color(0.95f, 0.72f, 0.33f),
                _ => category == "game" ? ImGuiHost.Color(0.62f, 0.72f, 0.80f) : ImGuiHost.Color(0.75f, 0.77f, 0.80f),
            };
            ImGui.TextColored(color, $"{category}: {message}");
        }
        if (atBottom)
            ImGui.SetScrollHereY(1f);
        ImGui.EndChild();
        ImGui.End();
    }

    void Stats()
    {
        ImGui.Begin(StatsPanel, ref _showStats);
        var io = ImGui.GetIO();
        ImGui.Text($"{io.Framerate:F0} fps, {1000f / Math.Max(1f, io.Framerate):F2} ms");
        ImGui.TextDisabled(_gpu.GpuName);
        ImGui.Text($"{_scene.World.Count} nodes, {_view.TrianglesDrawn:N0} triangles");
        ImGui.Text($"Viewport {_viewportWidth} x {_viewportHeight}");
        var cam = _view.Camera;
        ImGui.TextDisabled($"Camera at {cam.Target.X:F1}, {cam.Target.Y:F1}, {cam.Target.Z:F1}, {cam.Distance:F1} m out");
        ImGui.Text(_runner.Running ? "Game: running" : "Game: stopped");
        var timer = _view.Renderer.Timer;
        if (timer.Last.Count > 0)
        {
            ImGui.SeparatorText($"GPU {timer.TotalMs:F2} ms");
            foreach (var (name, ms) in timer.Last)
                ImGui.TextDisabled($"{name,-20} {ms,6:F3} ms");
        }
        ImGui.End();
    }

    // --- Scene editing --------------------------------------------------------------------------------

    void Place(string modelPath, Vector3 position, EntityId parent)
    {
        var model = _view.Model(modelPath);
        var clip = model?.ClipNames.FirstOrDefault(c => c == "idle") ?? model?.ClipNames.FirstOrDefault();
        var e = _scene.Place(modelPath, position, clip, parent);
        Log.Info("editor", $"placed {_scene.World.Name(e)}");
    }

    void InstanceScene(string scenePath, Vector3 position, EntityId parent)
    {
        if (scenePath == _scene.Path)
        {
            Log.Warn("editor", "a scene can't contain itself");
            return;
        }
        var e = _scene.InstantiateScene(scenePath, position, out var problems, parent);
        foreach (var error in problems.Errors)
            Log.Error("scene", error);
        foreach (var warning in problems.Warnings)
            Log.Warn("scene", warning);
        Log.Info("editor", $"added {_scene.World.Name(e)}, an instance of {scenePath}");
    }

    /// <summary>Starts the viewport where the scene's camera is, if it has one.</summary>
    void UseSceneCamera()
    {
        if (_scene.Camera is { } c)
            _view.Camera = new OrbitCamera
            {
                Target = c.Target,
                Yaw = float.DegreesToRadians(c.Yaw),
                Pitch = float.DegreesToRadians(c.Pitch),
                Distance = c.Distance,
            };
    }

    EditorScene LoadScene(string relative)
    {
        var scene = EditorScene.Load(_library, relative, out var problems);
        foreach (var error in problems.Errors)
            Log.Error("scene", error);
        foreach (var warning in problems.Warnings)
            Log.Warn("scene", warning);
        return scene;
    }

    void NewScene()
    {
        KeepUnsaved();
        _scene = EditorScene.New(_library, SceneLibrary.Normalize(Path.Combine(_project.Scenes, "untitled.scene.json")));
    }

    /// <summary>Opens a scene file (an absolute path, or relative to the project).</summary>
    public void OpenScene(string path)
    {
        KeepUnsaved();
        // A fresh library, so scenes changed outside the editor are read again
        _library = new SceneLibrary(_project.Root);
        _scene = LoadScene(SceneLibrary.Normalize(_project.Relative(_project.Absolute(path))));
        UseSceneCamera();
        Log.Info("editor", $"opened {_scene.Path}");
    }

    void KeepUnsaved()
    {
        if (_scene.Dirty)
            Log.Warn("editor", $"unsaved changes to {_scene.Path} kept in {Autosave(ImGui.GetTime())}");
    }

    void SaveScene(string? path = null)
    {
        try
        {
            _scene.Save(path);
            _autosaved = ImGui.GetTime();
            Log.Info("editor", $"saved {_scene.Path}");
        }
        catch (Exception e)
        {
            Log.Error("editor", $"couldn't save {_scene.Path}: {e.Message}");
        }
    }

    public void Dispose()
    {
        _runner.Dispose();
        _gpu.WaitIdle();
        _view.Dispose();
        _ui.Dispose();
        _gpu.Dispose();
        _window.Dispose();
        _log.Dispose();
    }

    /// <summary>The last log lines, collected from any thread for the Log panel.</summary>
    sealed class LogBuffer : IDisposable
    {
        const int Max = 2000;
        readonly List<(string, string, string)> _lines = [];
        readonly Lock _gate = new();

        public LogBuffer() => Log.Written += Add;

        void Add(string level, string category, string message)
        {
            lock (_gate)
            {
                _lines.Add((level, category, message));
                if (_lines.Count > Max)
                    _lines.RemoveRange(0, _lines.Count - Max);
            }
        }

        public (string, string, string)[] Snapshot()
        {
            lock (_gate)
                return [.. _lines];
        }

        public void Clear()
        {
            lock (_gate)
                _lines.Clear();
        }

        public void Dispose() => Log.Written -= Add;
    }
}
