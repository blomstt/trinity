using System.Diagnostics;
using System.Numerics;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Trinity.Graphics;

namespace Trinity.Editor;

/// <summary>Trinity Editor: one window with a viewport and docked panels, editing one project's scenes.</summary>
sealed class EditorApp : IDisposable
{
    const string ViewportPanel = "Viewport", HierarchyPanel = "Hierarchy", InspectorPanel = "Inspector";
    const string AssetsPanel = "Assets", LogPanel = "Log", StatsPanel = "Stats";

    readonly Window _window;
    readonly GraphicsDevice _gpu;
    readonly ShaderLibrary _shaders;
    readonly ImGuiHost _ui;
    readonly SceneView _view;
    readonly GameRunner _runner = new();
    readonly LogBuffer _log = new();
    readonly EditorProject _project;
    EditorScene _scene;

    SceneObject? _selected;
    ImTextureRef? _viewportTexture;
    int _viewportWidth = 1280, _viewportHeight = 720;
    bool _layoutBuilt;
    bool _showHierarchy = true, _showInspector = true, _showAssets = true, _showLog = true, _showStats = true;
    readonly ImInputString _nameEdit = new(128);
    readonly ImInputString _saveAsEdit = new(128);
    SceneObject? _nameEditFor;
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
        // BRUTAL License 2.2: the logo before the editor's main function
        if (Environment.GetEnvironmentVariable("TRINITY_SKIP_SPLASH") != "1")
            Splash.Show(_window, _gpu, _shaders);
        _view = new SceneView(_gpu, _shaders, project);

        var main = Path.Combine(project.ScenesPath, "main.scene.json");
        _scene = File.Exists(main) ? EditorScene.Load(main) : new EditorScene { Path = main };
        Log.Info("editor", $"project {project.Name} at {project.Root}, scene {project.Relative(_scene.Path!)}");
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
            var changed = _scene.Objects.Any(o => o.Playing) || t - lastActivity < 0.5 || _logChanged || Screenshot != null;
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
                _viewportTexture = _ui.AddTexture(_view.Target!.Sampler, _view.Target.ColorView);
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

        MenuBar();
        Shortcuts();
        Viewport();
        if (_showHierarchy) Hierarchy();
        if (_showInspector) Inspector();
        if (_showAssets) Assets();
        if (_showLog) LogWindow();
        if (_showStats) Stats();
    }

    /// <summary>Hierarchy left, inspector and stats right, assets and log below, viewport in the middle.</summary>
    static void BuildDefaultLayout(ImGuiID dock)
    {
        ImGui.Internal.DockBuilderSplitNode(dock, ImGuiDir.Left, 0.17f, out var left, out var rest);
        ImGui.Internal.DockBuilderSplitNode(rest, ImGuiDir.Right, 0.24f, out var right, out var middle);
        ImGui.Internal.DockBuilderSplitNode(middle, ImGuiDir.Down, 0.28f, out var bottom, out var centre);
        ImGui.Internal.DockBuilderSplitNode(right, ImGuiDir.Down, 0.30f, out var rightBottom, out var rightTop);
        ImGui.Internal.DockBuilderDockWindow(HierarchyPanel, left);
        ImGui.Internal.DockBuilderDockWindow(InspectorPanel, rightTop);
        ImGui.Internal.DockBuilderDockWindow(StatsPanel, rightBottom);
        ImGui.Internal.DockBuilderDockWindow(AssetsPanel, bottom);
        ImGui.Internal.DockBuilderDockWindow(LogPanel, bottom);
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
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Help"))
        {
            if (ImGui.MenuItem("About Trinity")) openAbout = true;
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Run"))
        {
            if (ImGui.MenuItem("Play", "F5", false, !_runner.Running)) _runner.Play(_project);
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
            _runner.Play(_project);
        }
        ImGui.EndMainMenuBar();

        if (openAbout)
            ImGui.OpenPopup("About Trinity");
        if (ImGui.BeginPopup("About Trinity"))
        {
            ImGui.Text($"Trinity Editor {typeof(EditorApp).Assembly.GetName().Version?.ToString(3)}");
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
        if (io.WantTextInput)
            return;
        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.S)) SaveScene();
        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.N)) NewScene();
        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.Q)) _window.ShouldClose = true;
        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.D) && _selected != null) Duplicate();
        if (ImGui.IsKeyPressed(ImGuiKey.Delete) && _selected != null) Delete();
        if (ImGui.IsKeyPressed(ImGuiKey.F5))
        {
            if (io.KeyShift) _runner.Stop();
            else _runner.Play(_project);
        }
    }

    // --- Panels --------------------------------------------------------------------------------------

    void Viewport()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new float2(0, 0));
        ImGui.Begin(ViewportPanel);
        var size = ImGui.GetContentRegionAvail();
        _viewportWidth = Math.Max(8, (int)size.X);
        _viewportHeight = Math.Max(8, (int)size.Y);
        if (_viewportTexture is { } texture)
            ImGui.Image(texture, size);

        // Right drag orbits, middle drag (or Shift + right) pans, the wheel zooms; F frames the selection
        var io = ImGui.GetIO();
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
        ImGui.End();
    }

    // --- Scene editing --------------------------------------------------------------------------------

    void Place(string modelPath)
    {
        var name = Path.GetFileNameWithoutExtension(modelPath);
        var count = _scene.Objects.Count(o => o.Name.StartsWith(name));
        var model = _view.Model(modelPath);
        var o = new SceneObject
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
        var copy = new SceneObject
        {
            Name = s.Name + " copy",
            Model = s.Model,
            Position = s.Position + new Vector3(1, 0, 0),
            Rotation = s.Rotation,
            Scale = s.Scale,
            Clip = s.Clip,
            Speed = s.Speed,
            Playing = s.Playing,
        };
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

    void NewScene()
    {
        _scene = new EditorScene { Path = Path.Combine(_project.ScenesPath, "untitled.scene.json") };
        _selected = null;
    }

    void OpenScene(string path)
    {
        _scene = EditorScene.Load(path);
        _selected = null;
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
