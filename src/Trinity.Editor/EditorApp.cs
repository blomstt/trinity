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
sealed class EditorApp : IDisposable
{
    const string ViewportPanel = "Viewport", HierarchyPanel = "Hierarchy", InspectorPanel = "Inspector";
    const string AssetsPanel = "Assets", LogPanel = "Log", StatsPanel = "Stats", EnvironmentPanel = "Environment";

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

    Node? _selected;
    ImTextureRef? _viewportTexture;
    int _viewportWidth = 1280, _viewportHeight = 720;
    bool _layoutBuilt;
    bool _showHierarchy = true, _showInspector = true, _showAssets = true, _showLog = true, _showStats = true, _showEnvironment = true;
    ImGuiID _inspectorDock;
    int _dragAxis = -1;
    readonly ImInputString _nameEdit = new(128);
    readonly ImInputString _saveAsEdit = new(128);
    Node? _nameEditFor;
    List<string> _assetFiles = [];
    double _assetsScanned = -10;

    public EditorApp(EditorProject project)
    {
        _project = project;
        _window = new Window($"Trinity Editor - {project.Name}", 1600, 950, appId: "trinity-editor");
        _gpu = new GraphicsDevice(_window, vsync: true);
        _shaders = new ShaderLibrary(_gpu);
        var config = Path.Combine(
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
            "trinity-editor"
        );
        _ui = new ImGuiHost(_window, _gpu, config);
        _scripts = new ScriptPanel(project, _ui.Mono);
        _runner.Output += _scripts.Checker.Feed;
        _scripts.Saved += () =>
        {
            if (_runner.Running && !GameRunner.HotReload)
                _runner.Restart(_project);
        };
        // BRUTAL License 2.2: the logo before the editor's main function
        if (Environment.GetEnvironmentVariable("TRINITY_SKIP_SPLASH") != "1")
            Splash.Show(_window, _gpu, _shaders);
        _view = new SceneView(_gpu, _shaders, project);
        _view.AssetReady += Window.Wake;

        var main = Path.Combine(project.ScenesPath, "main.scene.json");
        _scene = File.Exists(main) ? EditorScene.Load(main) : new EditorScene { Path = main };
        UseSceneCamera();
        Log.Info("editor", $"project {project.Name} at {project.Root}, scene {project.Relative(_scene.Path!)}");
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

    public void Select(string name)
    {
        _selected = _scene.Objects.FirstOrDefault(o => o.Name == name);
        if (_selected == null)
            Log.Error("editor", $"no object called {name} in the scene");
    }

    /// <summary>For checking the editor without a person: save the window to a PNG after a while, then quit.</summary>
    public (string Path, double After)? Screenshot;

    public void Run()
    {
        using var capture = Screenshot != null
            ? new GpuBuffer(_gpu, (long)_gpu.Extent.Width * _gpu.Extent.Height * 4 * 4, Brutal.VulkanApi.VkBufferUsageFlags.TransferDstBit, readback: true)
            : null;
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
            var changed = _scene.Objects.Any(o => o.Playing) || t - lastActivity < 0.5 || _logChanged || _view.Loading || Screenshot != null;
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
            _view.Render(cmd, _scene, _selected, dt, now);
            _ui.Render(cmd);
            var shoot = Screenshot is { } shot && now >= shot.After;
            _gpu.EndFrame(cmd, shoot ? capture : null, endRendering: false);
            if (shoot)
            {
                SaveScreenshot(capture!, Screenshot!.Value.Path);
                break;
            }

            _window.Title = $"Trinity Editor - {_project.Name} - {Path.GetFileName(_scene.Path)}{(_scene.Dirty ? " *" : "")}";
        }

        Log.Written -= WakeOnLog;
        _scripts.KeepUnsaved();
        if (_scene.Dirty && _scene.Path != null)
        {
            var backup = _scene.Path.Replace(".scene.json", ".autosave.scene.json");
            _scene.Save(backup);
            Log.Warn("editor", $"unsaved changes kept in {_project.Relative(backup)}");
        }
    }

    volatile bool _logChanged;

    void WakeOnLog(string level, string category, string message)
    {
        _logChanged = true;
        Window.Wake();
    }

    void SaveScreenshot(GpuBuffer pixels, string path)
    {
        _gpu.WaitIdle();
        var e = _gpu.Extent;
        var raw = path + ".bgra";
        File.WriteAllBytes(raw, pixels.Bytes[..(e.Width * e.Height * 4)].ToArray());
        var format = _gpu.ColorFormat is Brutal.VulkanApi.VkFormat.B8G8R8A8SRGB or Brutal.VulkanApi.VkFormat.B8G8R8A8UNorm ? "bgra" : "rgba";
        Process.Start("magick", ["-size", $"{e.Width}x{e.Height}", "-depth", "8", $"{format}:{raw}", "-alpha", "off", path])!.WaitForExit();
        File.Delete(raw);
        Log.Info("editor", $"screenshot saved to {path}");
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
        if (_showAssets) Assets();
        if (_showLog) LogWindow();
        if (_showStats) Stats();
        if (_showEnvironment) EnvironmentWindow();
    }

    /// <summary>Hierarchy left, inspector and stats right, assets and log below, viewport in the middle.</summary>
    static void BuildDefaultLayout(ImGuiID dock)
    {
        ImGui.Internal.DockBuilderSplitNode(dock, ImGuiDir.Left, 0.17f, out var left, out var rest);
        ImGui.Internal.DockBuilderSplitNode(rest, ImGuiDir.Right, 0.24f, out var right, out var middle);
        ImGui.Internal.DockBuilderSplitNode(middle, ImGuiDir.Down, 0.28f, out var bottom, out var centre);
        ImGui.Internal.DockBuilderSplitNode(right, ImGuiDir.Down, 0.30f, out var rightBottom, out var rightTop);
        ImGui.Internal.DockBuilderDockWindow(HierarchyPanel, left);
        ImGui.Internal.DockBuilderDockWindow(EnvironmentPanel, rightTop);
        ImGui.Internal.DockBuilderDockWindow(InspectorPanel, rightTop);
        ImGui.Internal.DockBuilderDockWindow(StatsPanel, rightBottom);
        ImGui.Internal.DockBuilderDockWindow(AssetsPanel, bottom);
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
                var scenes = Directory.Exists(_project.ScenesPath)
                    ? Directory.GetFiles(_project.ScenesPath, "*.scene.json").Order().ToArray()
                    : [];
                if (scenes.Length == 0)
                    ImGui.TextDisabled("No scenes yet");
                foreach (var s in scenes)
                    if (ImGui.MenuItem(Path.GetFileName(s), default, s == _scene.Path))
                        OpenScene(s);
                ImGui.EndMenu();
            }
            if (ImGui.MenuItem("Save scene", "Ctrl+S")) SaveScene();
            if (ImGui.MenuItem("Save scene as...")) openSaveAs = true;
            ImGui.Separator();
            if (ImGui.MenuItem("Quit", "Ctrl+Q")) _window.ShouldClose = true;
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Edit"))
        {
            if (ImGui.MenuItem("Duplicate", "Ctrl+D", false, _selected != null)) Duplicate();
            if (ImGui.MenuItem("Delete", "Del", false, _selected != null)) Delete();
            if (ImGui.MenuItem("Frame selected", "F", false, _selected != null)) _view.Frame(_selected!);
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("View"))
        {
            ImGui.MenuItem(HierarchyPanel, default, ref _showHierarchy);
            ImGui.MenuItem(InspectorPanel, default, ref _showInspector);
            ImGui.MenuItem(AssetsPanel, default, ref _showAssets);
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
            _saveAsEdit.SetValue(Path.GetFileName(_scene.Path ?? "main.scene.json").Replace(".scene.json", "").AsSpan());
            ImGui.OpenPopup("Save scene as");
        }
        if (ImGui.BeginPopup("Save scene as"))
        {
            ImGui.Text("Scene name");
            var enter = ImGui.InputText("##name", _saveAsEdit, ImGuiInputTextFlags.EnterReturnsTrue);
            if ((ImGui.Button("Save") || enter) && !_saveAsEdit.IsEmpty)
            {
                SaveScene(Path.Combine(_project.ScenesPath, _saveAsEdit.ToString() + ".scene.json"));
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
        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.N)) NewScene();
        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.Q)) _window.ShouldClose = true;
        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.D) && _selected != null) Duplicate();
        if (ImGui.IsKeyPressed(ImGuiKey.Delete) && _selected != null) Delete();
    }

    /// <summary>Saves the scene and scripts, then runs the game, as Godot does.</summary>
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
            if (ImGui.IsKeyPressed(ImGuiKey.F) && _selected != null && !io.WantTextInput)
                _view.Frame(_selected);
        }
        ImGui.End();
        ImGui.PopStyleVar();
    }

    /// <summary>Hovering, grabbing and dragging the selection's move arrows; a click elsewhere picks.</summary>
    void MoveGizmo(Vector2 mouse, bool hovered)
    {
        var io = ImGui.GetIO();
        if (_dragAxis >= 0)
        {
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left) || _selected == null)
            {
                _dragAxis = -1;
                return;
            }
            var (p0, p1) = GizmoAxisOnScreen(_selected, _dragAxis);
            if (p0 is { } a && p1 is { } b)
            {
                var s = b - a;
                var t = Vector2.Dot(new Vector2(io.MouseDelta.X, io.MouseDelta.Y), s) / MathF.Max(s.LengthSquared(), 1);
                ReadOnlySpan<Vector3> axes = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
                _selected.Position += axes[_dragAxis] * _view.GizmoLength(_selected) * t;
                _scene.Dirty = true;
            }
            return;
        }
        _view.GizmoAxis = hovered && _selected != null ? HoveredAxis(_selected, mouse) : -1;
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            if (_view.GizmoAxis >= 0)
                _dragAxis = _view.GizmoAxis;
            else
                _selected = _view.Pick(_scene, _view.Ray(mouse));
        }
    }

    (Vector2?, Vector2?) GizmoAxisOnScreen(Node o, int axis)
    {
        ReadOnlySpan<Vector3> axes = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
        return (_view.ToScreen(o.Position), _view.ToScreen(o.Position + axes[axis] * _view.GizmoLength(o)));
    }

    int HoveredAxis(Node o, Vector2 mouse)
    {
        var best = -1;
        var bestDistance = 8f; // pixels
        for (var a = 0; a < 3; a++)
        {
            if (GizmoAxisOnScreen(o, a) is not ({ } p0, { } p1))
                continue;
            var s = p1 - p0;
            var t = Math.Clamp(Vector2.Dot(mouse - p0, s) / MathF.Max(s.LengthSquared(), 1), 0, 1);
            var d = Vector2.Distance(mouse, p0 + s * t);
            if (d < bestDistance)
                (best, bestDistance) = (a, d);
        }
        return best;
    }

    void EnvironmentWindow()
    {
        ImGui.SetNextWindowDockID(_inspectorDock, ImGuiCond.FirstUseEver);
        if (!ImGui.Begin(EnvironmentPanel, ref _showEnvironment))
        {
            ImGui.End();
            return;
        }
        var e = _scene.File.Environment;
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
        if (changed)
            _scene.Dirty = true;
        ImGui.End();
    }

    void Hierarchy()
    {
        ImGui.Begin(HierarchyPanel, ref _showHierarchy);
        if (_scene.Objects.Count == 0)
            ImGui.TextDisabled("Empty scene. Double-click a model in Assets to place it.");
        for (var i = 0; i < _scene.Objects.Count; i++)
        {
            var o = _scene.Objects[i];
            ImGui.PushID(i);
            if (ImGui.Selectable(o.Name, o == _selected))
                _selected = o;
            if (!string.IsNullOrEmpty(o.Script))
            {
                ImGui.SameLine();
                ImGui.TextDisabled(o.Script);
            }
            if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                _view.Frame(o);
            if (ImGui.BeginPopupContextItem())
            {
                _selected = o;
                if (ImGui.MenuItem("Frame", "F")) _view.Frame(o);
                if (ImGui.MenuItem("Duplicate", "Ctrl+D")) Duplicate();
                if (ImGui.MenuItem("Delete", "Del")) Delete();
                ImGui.EndPopup();
            }
            ImGui.PopID();
        }
        ImGui.End();
    }

    void Inspector()
    {
        ImGui.Begin(InspectorPanel, ref _showInspector);
        _inspectorDock = ImGui.GetWindowDockID();
        if (_selected is not { } o)
        {
            ImGui.TextDisabled("Nothing selected");
            ImGui.End();
            return;
        }

        if (_nameEditFor != o)
        {
            _nameEdit.SetValue(o.Name.AsSpan());
            _nameEditFor = o;
        }
        if (ImGui.InputText("Name", _nameEdit))
        {
            o.Name = _nameEdit.ToString();
            _scene.Dirty = true;
        }
        ImGui.TextDisabled(o.Model);

        ImGui.SeparatorText("Transform");
        var position = new float3(o.Position.X, o.Position.Y, o.Position.Z);
        if (ImGui.DragFloat3("Position", ref position, 0.05f))
        {
            o.Position = new Vector3(position.X, position.Y, position.Z);
            _scene.Dirty = true;
        }
        var rotation = new float3(o.Rotation.X, o.Rotation.Y, o.Rotation.Z);
        if (ImGui.DragFloat3("Rotation", ref rotation, 0.5f))
        {
            o.Rotation = new Vector3(rotation.X, rotation.Y, rotation.Z);
            _scene.Dirty = true;
        }
        var scale = o.Scale;
        if (ImGui.DragFloat("Scale", ref scale, 0.01f, 0.01f, 1000f))
        {
            o.Scale = scale;
            _scene.Dirty = true;
        }

        ScriptSection(o);

        if (_view.Model(o.Model) is { } model)
        {
            ImGui.SeparatorText("Animation");
            if (!model.Model.IsRigged)
            {
                ImGui.TextDisabled("Static model");
            }
            else
            {
                var current = Array.IndexOf(model.ClipNames, o.Clip) is var c and >= 0 ? model.ClipNames[c] : model.ClipNames[0];
                if (ImGui.BeginCombo("Clip", current))
                {
                    foreach (var name in model.ClipNames)
                        if (ImGui.Selectable(name, name == current))
                        {
                            o.Clip = name;
                            o.Time = 0;
                            _scene.Dirty = true;
                        }
                    ImGui.EndCombo();
                }
                var playing = o.Playing;
                if (ImGui.Checkbox("Playing", ref playing))
                {
                    o.Playing = playing;
                    _scene.Dirty = true;
                }
                ImGui.SameLine();
                if (ImGui.Button("Restart"))
                    o.Time = 0;
                var speed = o.Speed;
                if (ImGui.DragFloat("Speed", ref speed, 0.01f, 0f, 10f))
                {
                    o.Speed = speed;
                    _scene.Dirty = true;
                }
                var clip = model.Model.Clips[Math.Max(0, Array.IndexOf(model.ClipNames, current))];
                ImGui.TextDisabled($"{clip.Duration:F2} s, {clip.FrameCount} baked frames");
            }

            ImGui.SeparatorText("Model");
            ImGui.TextDisabled($"{model.Triangles:N0} triangles, {model.Model.Vertices.Length:N0} vertices");
            if (model.Model.IsRigged)
                ImGui.TextDisabled($"{model.Model.JointCount} bones, far version {model.Model.Lod1.IndexCount / 3:N0} triangles");
        }
        ImGui.End();
    }

    // --- Scripts in the Inspector ---------------------------------------------------------------------

    static readonly float4 Warning = ImGuiHost.Color(0.95f, 0.72f, 0.33f);

    void ScriptSection(Node o)
    {
        ImGui.SeparatorText("Script");
        var current = o.Script ?? "";
        if (ImGui.BeginCombo("Script", current.Length == 0 ? "None" : current))
        {
            if (ImGui.Selectable("None", current.Length == 0))
                SetScript(o, null);
            foreach (var s in _scripts.Index.All)
                if (ImGui.Selectable(s.Name, s.Name == current))
                    SetScript(o, s.Name);
            ImGui.Separator();
            if (ImGui.Selectable("New script..."))
                _scripts.NewScript(ScriptPanel.Clean(o.Name), name => SetScript(o, name));
            ImGui.EndCombo();
        }
        if (current.Length == 0)
        {
            ImGui.TextDisabled("Attach a script to give this object behaviour");
            return;
        }
        if (_scripts.Index.Find(current) is not { } info)
        {
            ImGui.TextColored(Warning, $"No script class '{current}' in the project");
            return;
        }
        if (ImGui.Button("Open script"))
            _scripts.Open(info.File, info.Line);
        foreach (var f in info.Fields)
            ScriptField(o, f);
        // Values saved for fields the script no longer has
        foreach (var name in o.Fields?.Keys.Where(k => info.Fields.All(f => f.Name != k)).ToList() ?? [])
        {
            ImGui.TextColored(Warning, $"{name}: not in {current} any more");
            ImGui.SameLine();
            if (ImGui.SmallButton($"Remove##{name}"))
            {
                o.Fields!.Remove(name);
                _scene.Dirty = true;
            }
        }
    }

    void SetScript(Node o, string? name)
    {
        if (o.Script == name)
            return;
        o.Script = name;
        o.Fields = null;
        _scene.Dirty = true;
    }

    /// <summary>One script field: the value saved in the scene, or the script's own default until it's changed.</summary>
    void ScriptField(Node o, ScriptField f)
    {
        var saved = o.Fields != null && o.Fields.TryGetValue(f.Name, out var v) ? v : (JsonElement?)null;
        var label = Words(f.Name);
        var inv = CultureInfo.InvariantCulture;
        string? json = null;
        ImGui.PushID(f.Name);
        switch (f.Type)
        {
            case "float" or "double":
                var x = saved is { ValueKind: JsonValueKind.Number } n ? n.GetSingle() : Convert.ToSingle(f.Default, inv);
                if (ImGui.DragFloat(label, ref x, 0.05f))
                    json = x.ToString("R", inv);
                break;
            case "int":
                var i = saved is { ValueKind: JsonValueKind.Number } k && k.TryGetInt32(out var ki) ? ki : (int)f.Default;
                if (ImGui.DragInt(label, ref i))
                    json = i.ToString(inv);
                break;
            case "bool":
                var b = saved is { ValueKind: JsonValueKind.True or JsonValueKind.False } t ? t.GetBoolean() : (bool)f.Default;
                if (ImGui.Checkbox(label, ref b))
                    json = b ? "true" : "false";
                break;
            case "string":
                var key = $"{o.GetHashCode()}/{f.Name}";
                var text = saved is { ValueKind: JsonValueKind.String } st ? st.GetString() ?? "" : (string)f.Default;
                if (!_stringFields.TryGetValue(key, out var edit))
                {
                    edit = new ImInputString(256);
                    edit.SetValue(text.AsSpan());
                    _stringFields[key] = edit;
                }
                if (ImGui.InputText(label, edit))
                    json = "\"" + JsonEncodedText.Encode(edit.ToString()) + "\"";
                break;
            case "Vector3":
                var d = (Vector3)f.Default;
                var vec = saved is { ValueKind: JsonValueKind.Array } a && a.GetArrayLength() == 3
                    ? new float3(a[0].GetSingle(), a[1].GetSingle(), a[2].GetSingle())
                    : new float3(d.X, d.Y, d.Z);
                if (ImGui.DragFloat3(label, ref vec, 0.05f))
                    json = $"[{vec.X.ToString("R", inv)}, {vec.Y.ToString("R", inv)}, {vec.Z.ToString("R", inv)}]";
                break;
        }
        if (json != null)
        {
            o.Fields ??= [];
            o.Fields[f.Name] = JsonDocument.Parse(json).RootElement.Clone();
            _scene.Dirty = true;
        }
        if (saved != null)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Reset"))
            {
                o.Fields!.Remove(f.Name);
                _stringFields.Remove($"{o.GetHashCode()}/{f.Name}");
                _scene.Dirty = true;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Back to the script's default");
        }
        ImGui.PopID();
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

    void Assets()
    {
        ImGui.Begin(AssetsPanel, ref _showAssets);
        if (ImGui.Button("Refresh") || ImGui.GetTime() - _assetsScanned > 5)
        {
            _assetsScanned = ImGui.GetTime();
            _assetFiles = Directory.Exists(_project.AssetsPath)
                ? Directory.EnumerateFiles(_project.AssetsPath, "*", SearchOption.AllDirectories).Select(_project.Relative).Order().ToList()
                : [];
        }
        ImGui.SameLine();
        ImGui.TextDisabled($"{_project.Relative(_project.AssetsPath)}/  -  double-click a model to place it");
        ImGui.Separator();
        ImGui.BeginChild("files");
        foreach (var file in _assetFiles)
        {
            var isModel = file.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase);
            if (!isModel)
            {
                ImGui.TextDisabled(file);
                continue;
            }
            ImGui.Selectable(file);
            if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                Place(file);
        }
        ImGui.EndChild();
        ImGui.End();
    }

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
        ImGui.Text($"{_scene.Objects.Count} objects, {_view.TrianglesDrawn:N0} triangles");
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

    void Place(string modelPath)
    {
        var name = Path.GetFileNameWithoutExtension(modelPath);
        var count = _scene.Objects.Count(o => o.Name.StartsWith(name));
        var model = _view.Model(modelPath);
        var o = new Node
        {
            Name = count == 0 ? name : $"{name} {count + 1}",
            Model = modelPath,
            Position = new Vector3(_view.Camera.Target.X, 0, _view.Camera.Target.Z),
            Clip = model?.ClipNames.FirstOrDefault(c => c == "idle") ?? model?.ClipNames.FirstOrDefault() ?? "",
        };
        _scene.Objects.Add(o);
        _scene.Dirty = true;
        _selected = o;
        Log.Info("editor", $"placed {o.Name}");
    }

    void Duplicate()
    {
        if (_selected is not { } s)
            return;
        var copy = s.Clone();
        copy.Name = s.Name + " copy";
        copy.Position = s.Position + new Vector3(1, 0, 0);
        _scene.Objects.Add(copy);
        _scene.Dirty = true;
        _selected = copy;
    }

    void Delete()
    {
        if (_selected is not { } s)
            return;
        _scene.Objects.Remove(s);
        _scene.Dirty = true;
        _selected = null;
    }

    /// <summary>Starts the viewport where the scene's camera is, if it has one.</summary>
    void UseSceneCamera()
    {
        if (_scene.File.Camera is { } c)
            _view.Camera = new OrbitCamera
            {
                Target = c.Target,
                Yaw = float.DegreesToRadians(c.Yaw),
                Pitch = float.DegreesToRadians(c.Pitch),
                Distance = c.Distance,
            };
    }

    void NewScene()
    {
        _scene = new EditorScene { Path = Path.Combine(_project.ScenesPath, "untitled.scene.json") };
        _selected = null;
    }

    public void OpenScene(string path)
    {
        _scene = EditorScene.Load(path);
        _selected = null;
        UseSceneCamera();
        Log.Info("editor", $"opened {_project.Relative(path)}");
    }

    void SaveScene(string? path = null)
    {
        path ??= _scene.Path ?? Path.Combine(_project.ScenesPath, "main.scene.json");
        _scene.Save(path);
        Log.Info("editor", $"saved {_project.Relative(path)}");
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
