using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Trinity.Editor.Scripting;
using Trinity.Scenes;

namespace Trinity.Editor;

/// <summary>
/// The Inspector panel: the selected node's properties and components, drawn from
/// <see cref="SceneSchema"/>. Every change goes through <see cref="EditorScene.SetProperty"/>, so it
/// can be undone and, on a node from an instanced scene, becomes an override (shown highlighted, with
/// a Reset button that goes back to the source's value).
/// </summary>
sealed partial class EditorApp
{
    static readonly float4 Warning = ImGuiHost.Color(0.95f, 0.72f, 0.33f);
    static readonly float4 OverrideColor = ImGuiHost.Color(0.95f, 0.78f, 0.45f);

    readonly Dictionary<string, ImInputString> _textEdits = [];
    string? _activeText;

    void Inspector()
    {
        if (_focusInspector > 0)
        {
            _focusInspector--;
            ImGui.SetNextWindowFocus();
        }
        ImGui.Begin(InspectorPanel, ref _showInspector);
        _inspectorDock = ImGui.GetWindowDockID();
        var w = _scene.World;
        var e = _scene.Primary;
        if (!w.IsAlive(e))
        {
            ImGui.TextDisabled("Nothing selected");
            ImGui.End();
            return;
        }
        if (_scene.Selection.Count > 1)
            ImGui.TextDisabled($"{_scene.Selection.Count} nodes selected; showing {w.Name(e)}");

        var inherited = _scene.IsInherited(e);
        ImGui.PushID(e.Index);
        Property(e, "Name", PropertyKind.Text, "Name");
        Property(e, "Hidden", PropertyKind.Bool, "Hidden");
        if (inherited)
        {
            var root = w.Get<Inherited>(e).InstanceRoot;
            ImGui.TextColored(InheritedColor, $"From {w.Name(root)} ({w.Get<SceneInstance>(root).Source})");
            ImGui.TextDisabled("Changes here are saved as overrides on the instance");
        }
        if (w.Has<SceneInstance>(e))
            InstanceSection(e, inherited);

        ImGui.SeparatorText("Transform");
        Property(e, "Position", PropertyKind.Vector3, "Position");
        Property(e, "Rotation", PropertyKind.Vector3, "Rotation", speed: 0.5f);
        Property(e, "Scale", PropertyKind.Vector3, "Scale", speed: 0.01f);

        string? remove = null;
        foreach (var def in SceneSchema.Components)
        {
            if (!def.Has(w, e))
                continue;
            ImGui.PushID(def.Name);
            var open = ImGui.CollapsingHeader(Words(def.Name), ImGuiTreeNodeFlags.DefaultOpen);
            if (!inherited && ImGui.BeginPopupContextItem())
            {
                if (ImGui.MenuItem($"Remove {def.Name}"))
                    remove = def.Name;
                ImGui.EndPopup();
            }
            if (open)
            {
                if (def.Name == "Script")
                    ScriptSection(e);
                else
                    foreach (var p in def.Properties)
                        ComponentProperty(e, def, p);
                if (!inherited && ImGui.SmallButton($"Remove {Words(def.Name)}"))
                    remove = def.Name;
            }
            ImGui.PopID();
        }
        if (remove != null)
            Report(_scene.RemoveComponent(e, remove));

        ImGui.Separator();
        ImGui.BeginDisabled(inherited);
        if (ImGui.Button("Add component"))
            ImGui.OpenPopup("add-component");
        ImGui.EndDisabled();
        if (inherited && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Add components to this node in its source scene");
        if (ImGui.BeginPopup("add-component"))
        {
            foreach (var def in SceneSchema.Components)
                if (!def.Has(w, e) && ImGui.MenuItem(Words(def.Name)))
                    Report(_scene.AddComponent(e, def.Name));
            ImGui.EndPopup();
        }

        ModelInfo(e);
        ImGui.PopID();
        ImGui.End();
        if (_pendingInspector is { } pending)
        {
            _pendingInspector = null;
            pending();
        }
    }

    void InstanceSection(EntityId e, bool inherited)
    {
        var w = _scene.World;
        var source = w.Get<SceneInstance>(e).Source;
        ImGui.SeparatorText("Instance");
        ImGui.TextColored(InstanceColor, source);
        if (!File.Exists(_project.Absolute(source)))
            ImGui.TextColored(Warning, "Source scene missing: the instance is empty");
        // These run after the panel: they replace nodes the inspector may be showing
        if (ImGui.SmallButton("Open source"))
            _pendingInspector = () => OpenScene(source);
        ImGui.SameLine();
        if (ImGui.SmallButton("Reload"))
            _pendingInspector = () => _scene.ReloadInstancesOf(source);
        var count = _scene.OverrideCount(e);
        if (!inherited && count > 0)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"Revert {count} override{(count == 1 ? "" : "s")}"))
                _pendingInspector = () => _scene.RevertInstance(e);
        }
    }

    Action? _pendingInspector;

    void ComponentProperty(EntityId e, ComponentDef def, PropertyDef p)
    {
        var path = $"{def.Name}.{p.Name}";
        var w = _scene.World;
        // Pickers for what a free-form field can't show well
        if (path == "MeshRenderer.Mesh")
        {
            ScanFiles();
            AssetCombo(e, path, Words(p.Name), _assetFiles.Where(IsModel));
            return;
        }
        if (path == "Animator.Clip" && w.Has<MeshRenderer>(e) && _view.Model(w.Get<MeshRenderer>(e).Mesh) is { Model.IsRigged: true } model)
        {
            var current = w.Get<Animator>(e).Clip ?? "";
            Overridden(e, path, () =>
            {
                if (ImGui.BeginCombo(Words(p.Name), current.Length == 0 ? "(first)" : current))
                {
                    foreach (var name in model.ClipNames)
                        if (ImGui.Selectable(name, name == current))
                        {
                            Report(_scene.SetProperty(e, path, SceneSchema.Text(name)));
                            w.Get<Animator>(e).Time = 0;
                        }
                    ImGui.EndCombo();
                }
            });
            ImGui.SameLine();
            if (ImGui.SmallButton("Restart"))
                w.Get<Animator>(e).Time = 0;
            return;
        }
        Property(e, path, p.Kind, Words(p.Name));
    }

    /// <summary>One property's widget, by kind. Drags merge into one undo step (see DrawUi).</summary>
    void Property(EntityId e, string path, PropertyKind kind, string label, float speed = 0.05f)
    {
        if (SceneSchema.Get(_scene.World, e, path) is not { } value)
            return;
        Overridden(e, path, () =>
        {
            switch (kind)
            {
                case PropertyKind.Float:
                    var f = value.GetSingle();
                    if (ImGui.DragFloat(label, ref f, speed))
                        Report(_scene.SetProperty(e, path, SceneSchema.Float(f), merge: true));
                    break;
                case PropertyKind.Int:
                    var i = value.GetInt32();
                    if (ImGui.DragInt(label, ref i))
                        Report(_scene.SetProperty(e, path, SceneSchema.Int(i), merge: true));
                    break;
                case PropertyKind.Bool:
                    var b = value.GetBoolean();
                    if (ImGui.Checkbox(label, ref b))
                        Report(_scene.SetProperty(e, path, SceneSchema.Bool(b)));
                    break;
                case PropertyKind.Vector3:
                    var v = SceneSchema.ReadVector(value);
                    var f3 = new float3(v.X, v.Y, v.Z);
                    if (ImGui.DragFloat3(label, ref f3, speed))
                        Report(_scene.SetProperty(e, path, SceneSchema.Vector(new Vector3(f3.X, f3.Y, f3.Z)), merge: true));
                    break;
                case PropertyKind.Text:
                    TextProperty(e, path, label, value.GetString() ?? "");
                    break;
                case PropertyKind.Asset:
                    ScanFiles();
                    AssetCombo(e, path, label, _assetFiles);
                    break;
                case PropertyKind.Entity:
                    EntityCombo(e, path, label, SceneSchema.ReadEntity(value));
                    break;
            }
        });
    }

    /// <summary>Draws a property; if it's an override, highlighted and with a Reset button.</summary>
    void Overridden(EntityId e, string path, Action draw)
    {
        var overridden = _scene.IsOverridden(e, path);
        if (overridden)
            ImGui.PushStyleColor(ImGuiCol.Text, OverrideColor);
        draw();
        if (!overridden)
            return;
        ImGui.PopStyleColor();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Overridden on this instance");
        ImGui.SameLine();
        if (ImGui.SmallButton($"Reset##{path}"))
            _pendingInspector = () => _scene.ResetOverride(e, path);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Back to the source scene's value");
    }

    /// <summary>A text field, committed (one undo step) when editing ends.</summary>
    void TextProperty(EntityId e, string path, string label, string current)
    {
        var key = $"{e.Index}:{e.Generation}/{path}";
        if (!_textEdits.TryGetValue(key, out var edit))
            _textEdits[key] = edit = new ImInputString(256);
        if (_activeText != key)
            edit.SetValue(current.AsSpan());
        ImGui.InputText(label, edit);
        if (ImGui.IsItemActive())
            _activeText = key;
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            var text = edit.ToString();
            if (text != current && !(path == "Name" && text.Length == 0))
                Report(_scene.SetProperty(e, path, SceneSchema.Text(text)));
        }
        if (!ImGui.IsItemActive() && _activeText == key)
            _activeText = null;
    }

    void AssetCombo(EntityId e, string path, string label, IEnumerable<string> files)
    {
        var current = SceneSchema.Get(_scene.World, e, path)?.GetString() ?? "";
        Overridden(e, path, () =>
        {
            if (!ImGui.BeginCombo(label, current.Length == 0 ? "None" : current))
                return;
            if (ImGui.Selectable("None", current.Length == 0))
                Report(_scene.SetProperty(e, path, SceneSchema.Text("")));
            foreach (var f in files)
                if (ImGui.Selectable(f, f == current))
                    Report(_scene.SetProperty(e, path, SceneSchema.Text(f)));
            ImGui.EndCombo();
        });
        if (current.Length > 0 && !File.Exists(_project.Absolute(current)))
            ImGui.TextColored(Warning, $"Missing: {current}");
    }

    void EntityCombo(EntityId e, string path, string label, EntityId current)
    {
        var w = _scene.World;
        if (!ImGui.BeginCombo(label, w.IsAlive(current) ? w.Name(current) : "None"))
            return;
        if (ImGui.Selectable("None", !w.IsAlive(current)))
            Report(_scene.SetProperty(e, path, SceneSchema.Json("null")));
        foreach (var n in w.All())
        {
            ImGui.PushID(n.Index);
            if (ImGui.Selectable(w.Name(n), n == current))
                Report(_scene.SetProperty(e, path, SceneSchema.EntityRef(n)));
            ImGui.PopID();
        }
        ImGui.EndCombo();
    }

    void ModelInfo(EntityId e)
    {
        var w = _scene.World;
        if (!w.Has<MeshRenderer>(e) || _view.Model(w.Get<MeshRenderer>(e).Mesh) is not { } model)
            return;
        ImGui.SeparatorText("Model");
        ImGui.TextDisabled($"{model.Triangles:N0} triangles, {model.Model.Vertices.Length:N0} vertices");
        if (model.Model.IsRigged)
        {
            ImGui.TextDisabled($"{model.Model.JointCount} bones, far version {model.Model.Lod1.IndexCount / 3:N0} triangles");
            ImGui.TextDisabled($"Clips: {string.Join(", ", model.ClipNames)}");
        }
    }

    // --- Scripts --------------------------------------------------------------------------------------

    void ScriptSection(EntityId e)
    {
        var w = _scene.World;
        var current = w.Get<ScriptComponent>(e).Class ?? "";
        Overridden(e, "Script.Class", () =>
        {
            if (!ImGui.BeginCombo("Class", current.Length == 0 ? "None" : current))
                return;
            foreach (var s in _scripts.Index.All)
                if (ImGui.Selectable(s.Name, s.Name == current))
                    Report(_scene.SetScript(e, s.Name));
            ImGui.Separator();
            if (ImGui.Selectable("New script..."))
                _scripts.NewScript(ScriptPanel.Clean(w.Name(e)), name => Report(_scene.SetScript(e, name)));
            ImGui.EndCombo();
        });
        if (current.Length == 0)
        {
            ImGui.TextDisabled("Pick a script class to give this node behaviour");
            return;
        }
        if (_scripts.Index.Find(current) is not { } info)
        {
            ImGui.TextColored(Warning, $"No script class '{current}' in the project");
            return;
        }
        if (ImGui.SmallButton("Open script"))
            _scripts.Open(info.File, info.Line);
        foreach (var f in info.Fields)
            ScriptField(e, f);
        // Values saved for fields the script no longer has
        foreach (var name in w.Get<ScriptComponent>(e).Fields?.Keys.Where(k => info.Fields.All(f => f.Name != k)).ToList() ?? [])
        {
            ImGui.TextColored(Warning, $"{name}: not in {current} any more");
            ImGui.SameLine();
            if (ImGui.SmallButton($"Remove##{name}"))
                _pendingInspector = () => _scene.ClearScriptField(e, name);
        }
    }

    /// <summary>One script field: the value saved in the scene, or the script's own default until it's changed.</summary>
    void ScriptField(EntityId e, ScriptField f)
    {
        var path = "Script." + SceneSchema.FieldsPrefix + f.Name;
        var saved = SceneSchema.Get(_scene.World, e, path);
        var label = Words(f.Name);
        ImGui.PushID(f.Name);
        Overridden(e, path, () =>
        {
            JsonElement? value = null;
            switch (f.Type)
            {
                case "float" or "double":
                    var x = saved is { ValueKind: JsonValueKind.Number } n ? n.GetSingle() : Convert.ToSingle(f.Default, CultureInfo.InvariantCulture);
                    if (ImGui.DragFloat(label, ref x, 0.05f))
                        value = SceneSchema.Float(x);
                    break;
                case "int":
                    var i = saved is { ValueKind: JsonValueKind.Number } k && k.TryGetInt32(out var ki) ? ki : (int)f.Default;
                    if (ImGui.DragInt(label, ref i))
                        value = SceneSchema.Int(i);
                    break;
                case "bool":
                    var b = saved is { ValueKind: JsonValueKind.True or JsonValueKind.False } t ? t.GetBoolean() : (bool)f.Default;
                    if (ImGui.Checkbox(label, ref b))
                        value = SceneSchema.Bool(b);
                    break;
                case "string":
                    TextProperty(e, path, label, saved is { ValueKind: JsonValueKind.String } st ? st.GetString() ?? "" : (string)f.Default);
                    break;
                case "Vector3":
                    var d = (Vector3)f.Default;
                    var vec = saved is { ValueKind: JsonValueKind.Array } a && a.GetArrayLength() == 3
                        ? new float3(a[0].GetSingle(), a[1].GetSingle(), a[2].GetSingle())
                        : new float3(d.X, d.Y, d.Z);
                    if (ImGui.DragFloat3(label, ref vec, 0.05f))
                        value = SceneSchema.Vector(new Vector3(vec.X, vec.Y, vec.Z));
                    break;
                case "Node":
                    EntityCombo(e, path, label, saved is { } s ? SceneSchema.ReadEntity(s) : EntityId.None);
                    break;
                default:
                    ImGui.TextDisabled($"{label}: {f.Type} (not editable here)");
                    break;
            }
            if (value is { } v)
                Report(_scene.SetProperty(e, path, v, merge: true));
        });
        // Own nodes: a saved value can go back to the script's default (overrides have their own Reset)
        if (saved != null && !_scene.IsInherited(e))
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Default"))
                _pendingInspector = () => _scene.ClearScriptField(e, f.Name);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Back to the script's default");
        }
        ImGui.PopID();
    }
}
