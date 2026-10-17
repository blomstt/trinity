using Brutal.ImGuiApi;
using Brutal.Numerics;
using Trinity.Scenes;

namespace Trinity.Editor;

/// <summary>
/// The Scene dock: the scene's nodes as a tree. Each node shows an icon for what it is
/// and, on the right, buttons for its script, its source scene (for an instance), its lock and its
/// visibility. Click selects (Ctrl+click adds), drag a node onto another to put it under it (with
/// Shift: just before it), onto empty space to move it to the top; drop a model or scene from the
/// FileSystem dock onto a node to add it there. Nodes from inside an instance are dimmed: they come
/// from the source scene, so they can be edited (as overrides) but not moved, deleted or given children.
/// </summary>
sealed partial class EditorApp
{
    const string NodePayload = "trinity-node";
    static readonly float4 InheritedColor = ImGuiHost.Color(0.55f, 0.58f, 0.62f);
    static readonly float4 InstanceColor = ImGuiHost.Color(0.62f, 0.78f, 0.95f);

    /// <summary>What the + button adds: a label, the node's name and its component.</summary>
    static readonly (string Label, string Name, string? Component, Icon Icon)[] NodeTypes =
    [
        ("Node", "Node", null, Icon.Node),
        ("Mesh", "Mesh", "MeshRenderer", Icon.Mesh),
        ("Camera", "Camera", "Camera", Icon.Camera),
        ("Directional light", "Sun", "DirectionalLight", Icon.Light),
        ("Scripted node", "Node", "Script", Icon.Script),
    ];

    EntityId _dragged;
    Action? _afterTree; // changes the tree can't take while it's being walked
    bool _renameRequested;
    bool _revealSelection; // unfold the tree down to the selection (picked in the viewport, say) and scroll to it
    EntityId _renaming;
    readonly ImInputString _renameEdit = new(128);
    readonly ImInputString _nodeFilter = new(128);
    HashSet<EntityId>? _filtered; // nodes shown while filtering: matches and their ancestors

    void Hierarchy()
    {
        ImGui.Begin(HierarchyPanel, ref _showHierarchy);
        var w = _scene.World;

        if (Icons.Button("add", Icon.Plus, Icons.FileColor, "Add a child node (Ctrl+Shift+N)"))
            ImGui.OpenPopup("add-node");
        if (ImGui.BeginPopup("add-node"))
        {
            foreach (var (label, name, component, icon) in NodeTypes)
            {
                Icons.Inline(icon, Icons.NodeColor);
                ImGui.SameLine();
                if (ImGui.Selectable(label))
                    _afterTree = () => _scene.CreateNode(name, _scene.Primary, component);
            }
            ImGui.EndPopup();
        }
        ImGui.SameLine(0, 2);
        if (Icons.Button("instance", Icon.Link, Icons.FileColor, "Instance a scene as a child"))
            ImGui.OpenPopup("instance-scene");
        if (ImGui.BeginPopup("instance-scene"))
        {
            ScanFiles();
            if (_sceneFiles.Count(f => f != _scene.Path) == 0)
                ImGui.TextDisabled("No other scenes");
            foreach (var f in _sceneFiles)
                if (f != _scene.Path)
                {
                    Icons.Inline(Icon.Instance, Icons.NodeColor);
                    ImGui.SameLine();
                    if (ImGui.Selectable(f))
                        _afterTree = () => InstanceScene(f, NewNodePosition(), _scene.Primary);
                }
            ImGui.EndPopup();
        }
        ImGui.SameLine(0, 6);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##filter", "Filter nodes", _nodeFilter) || _filtered != null)
            _filtered = _nodeFilter.IsEmpty ? null : Filtered(_nodeFilter.ToString());

        ImGui.BeginChild("tree");
        if (w.Count == 0)
            ImGui.TextDisabled("Empty scene. Add a node with +, or drop a model or scene here from FileSystem.");
        else if (_filtered is { Count: 0 })
            ImGui.TextDisabled("No nodes match");
        foreach (var root in w.Roots)
            TreeItem(root);
        _revealSelection = false;

        // The space below the tree: dropping there moves a node to the top level; clicking clears the selection
        var space = ImGui.GetContentRegionAvail();
        ImGui.InvisibleButton("##below", new float2(Math.Max(space.X, 1), Math.Max(space.Y, 24)));
        if (ImGui.IsItemClicked())
            _scene.Select(EntityId.None);
        if (ImGui.BeginDragDropTarget())
        {
            if (!ImGui.AcceptDragDropPayload(NodePayload).IsNull())
            {
                var dragged = _dragged;
                _afterTree = () => Report(_scene.Reparent(dragged, EntityId.None));
            }
            AcceptAssetDrop(EntityId.None);
            ImGui.EndDragDropTarget();
        }
        if (ImGui.BeginPopupContextItem("below"))
        {
            foreach (var (label, name, component, _) in NodeTypes)
                if (ImGui.MenuItem($"New {label.ToLowerInvariant()}"))
                    _afterTree = () => _scene.CreateNode(name, EntityId.None, component);
            ImGui.EndPopup();
        }
        ImGui.EndChild();

        RenamePopup();
        ImGui.End();
    }

    /// <summary>Nodes whose names contain the filter text, and their ancestors so the tree can reach them.</summary>
    HashSet<EntityId> Filtered(string text)
    {
        var w = _scene.World;
        var shown = new HashSet<EntityId>();
        foreach (var e in w.All())
            if (w.Name(e).Contains(text, StringComparison.OrdinalIgnoreCase))
                for (var a = e; !a.IsNone && shown.Add(a); a = w.Parent(a)) { }
        return shown;
    }

    /// <summary>What a node is, for its icon.</summary>
    static Icon NodeIcon(World w, EntityId e) =>
        w.Has<Camera3D>(e) ? Icon.Camera
        : w.Has<DirectionalLight>(e) ? Icon.Light
        : w.Has<MeshRenderer>(e) ? (w.Has<Animator>(e) ? Icon.Character : Icon.Mesh)
        : w.Has<SceneInstance>(e) ? Icon.Instance
        : Icon.Node;

    void TreeItem(EntityId e)
    {
        var w = _scene.World;
        if (_filtered != null && !_filtered.Contains(e))
            return;
        var inherited = _scene.IsInherited(e);
        var instance = w.Has<SceneInstance>(e);
        var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.AllowOverlap | ImGuiTreeNodeFlags.FramePadding;
        if (w.ChildCount(e) == 0)
            flags |= ImGuiTreeNodeFlags.Leaf;
        else if (!instance)
            flags |= ImGuiTreeNodeFlags.DefaultOpen; // instances start folded: their insides are the source's
        if (_scene.Selection.Contains(e))
            flags |= ImGuiTreeNodeFlags.Selected;

        ImGui.PushID(e.Index);
        if (_filtered != null || _revealSelection && e != _scene.Primary && w.IsAlive(_scene.Primary) && w.IsSelfOrAncestor(e, _scene.Primary))
            ImGui.SetNextItemOpen(true);
        var hiddenAbove = w.HiddenInHierarchy(e);
        var textColor = inherited ? InheritedColor : instance ? InstanceColor : ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
        if (hiddenAbove)
            textColor.W *= 0.5f;
        ImGui.PushStyleColor(ImGuiCol.Text, textColor);
        // Room for the icon before the name (it's drawn over the gap); a long name is shortened so it
        // stops before the buttons at the right of the row
        var buttons = RowButtonCount(e);
        var labelStart = ImGui.GetCursorScreenPos().X + ImGui.GetTreeNodeToLabelSpacing() + ImGui.GetTextLineHeight() + 6;
        var labelEnd = ImGui.GetWindowPos().X + ImGui.GetWindowWidth() - ImGui.GetStyle().ScrollbarSize - 8 - buttons * ImGui.GetFrameHeight();
        var open = ImGui.TreeNodeEx("        " + Fit(w.Name(e), labelEnd - labelStart) + "##node", flags);
        ImGui.PopStyleColor();
        var min = ImGui.GetItemRectMin();
        var height = ImGui.GetItemRectMax().Y - min.Y;
        var iconSize = ImGui.GetTextLineHeight();
        var iconColor = inherited ? InheritedColor : Icons.NodeColor;
        if (hiddenAbove)
            iconColor.W *= 0.5f;
        Icons.Draw(ImGui.GetWindowDrawList(), NodeIcon(w, e),
            new float2(min.X + ImGui.GetTreeNodeToLabelSpacing() + 2, min.Y + (height - iconSize) / 2), iconSize, iconColor);

        if (_revealSelection && e == _scene.Primary)
            ImGui.SetScrollHereY();
        if (ImGui.IsItemClicked() && !ImGui.IsItemToggledOpen())
            _scene.Select(e, add: ImGui.GetIO().KeyCtrl);
        if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            _view.Frame(w, e);
        if (ImGui.IsItemHovered() && inherited)
            ImGui.SetTooltip($"From {w.Name(w.Get<Inherited>(e).InstanceRoot)}'s source scene: edits become overrides");

        if (!inherited && ImGui.BeginDragDropSource())
        {
            _dragged = e;
            ImGui.SetDragDropPayload(NodePayload, default, 0);
            ImGui.Text(w.Name(e));
            ImGui.EndDragDropSource();
        }
        if (ImGui.BeginDragDropTarget())
        {
            if (!ImGui.AcceptDragDropPayload(NodePayload).IsNull())
            {
                var dragged = _dragged;
                if (ImGui.GetIO().KeyShift)
                    _afterTree = () => Report(_scene.Reparent(dragged, w.Parent(e), BeforeIndex(dragged, e)));
                else
                    _afterTree = () => Report(_scene.Reparent(dragged, e));
            }
            AcceptAssetDrop(e);
            ImGui.EndDragDropTarget();
        }

        if (ImGui.BeginPopupContextItem())
        {
            if (!_scene.Selection.Contains(e))
                _scene.Select(e);
            NodeMenu(e, inherited, instance);
            ImGui.EndPopup();
        }

        RowButtons(e, min.Y, height);

        if (open)
        {
            foreach (var c in w.Children(e))
                TreeItem(c);
            ImGui.TreePop();
        }
        ImGui.PopID();
    }

    int RowButtonCount(EntityId e)
    {
        var w = _scene.World;
        return 1 + (w.Locked(e) ? 1 : 0) + (w.Has<SceneInstance>(e) ? 1 : 0)
            + (w.Has<ScriptComponent>(e) && w.Get<ScriptComponent>(e).Class is { Length: > 0 } ? 1 : 0);
    }

    /// <summary>Text cut to fit a width, ending in "..." when cut.</summary>
    static string Fit(string text, float width)
    {
        if (ImGui.CalcTextSize(text).X <= width)
            return text;
        for (var n = text.Length - 1; n > 0; n--)
            if (ImGui.CalcTextSize(text[..n] + "...").X <= width)
                return text[..n] + "...";
        return "...";
    }

    /// <summary>The buttons at the right of a row: script, source scene, lock, visibility.</summary>
    void RowButtons(EntityId e, float rowTop, float rowHeight)
    {
        var w = _scene.World;
        var size = rowHeight;
        var right = ImGui.GetWindowPos().X + ImGui.GetWindowWidth() - ImGui.GetStyle().ScrollbarSize - 4;
        var x = right;
        float2 Next() => new(x -= size, rowTop);

        var hidden = w.Hidden(e);
        ImGui.SetCursorScreenPos(Next());
        if (Icons.Button("eye", hidden ? Icon.EyeClosed : Icon.Eye, hidden ? Icons.Dim : Icons.FileColor, hidden ? "Hidden: click to show" : "Visible: click to hide", size))
            _afterTree = () => Report(_scene.SetProperty(e, "Hidden", SceneSchema.Bool(!hidden)));
        if (w.Locked(e))
        {
            ImGui.SetCursorScreenPos(Next());
            if (Icons.Button("lock", Icon.Lock, Icons.FileColor, "Locked: can't be picked or moved in the viewport. Click to unlock", size))
                _afterTree = () => Report(_scene.SetProperty(e, "Locked", SceneSchema.Bool(false)));
        }
        if (w.Has<SceneInstance>(e))
        {
            var source = w.Get<SceneInstance>(e).Source;
            ImGui.SetCursorScreenPos(Next());
            if (Icons.Button("source", Icon.Instance, InstanceColor, $"Instance of {source}: click to open it", size))
                _afterTree = () => OpenScene(source);
        }
        if (w.Has<ScriptComponent>(e) && w.Get<ScriptComponent>(e).Class is { Length: > 0 } cls)
        {
            ImGui.SetCursorScreenPos(Next());
            if (Icons.Button("script", Icon.Script, Icons.FileColor, $"Script {cls}: click to open it", size) && _scripts.Index.Find(cls) is { } info)
                _scripts.Open(info.File, info.Line);
        }
    }

    /// <summary>The sibling index that puts <paramref name="node"/> just before <paramref name="target"/>, allowing for it leaving its place first.</summary>
    int BeforeIndex(EntityId node, EntityId target)
    {
        var w = _scene.World;
        var index = w.SiblingIndex(target);
        if (w.Parent(node) == w.Parent(target) && w.SiblingIndex(node) < index)
            index--;
        return index;
    }

    void NodeMenu(EntityId e, bool inherited, bool instance)
    {
        var w = _scene.World;
        if (ImGui.BeginMenu("Add child node"))
        {
            foreach (var (label, name, component, _) in NodeTypes)
                if (ImGui.MenuItem(label))
                    _afterTree = () => _scene.CreateNode(name, e, component);
            ImGui.EndMenu();
        }
        if (ImGui.MenuItem("Rename", "F2")) _renameRequested = true;
        if (ImGui.MenuItem("Duplicate", "Ctrl+D", false, !inherited)) _afterTree = () => _scene.Duplicate(_scene.Selection);
        if (ImGui.MenuItem("Delete", "Del", false, !inherited)) _afterTree = () => Report(_scene.Delete(_scene.Selection));
        if (ImGui.MenuItem("Move to top level", default, false, !inherited && !w.Parent(e).IsNone)) _afterTree = () => Report(_scene.Reparent(e, EntityId.None));
        ImGui.Separator();
        var locked = w.Locked(e);
        if (ImGui.MenuItem(locked ? "Unlock" : "Lock")) _afterTree = () => Report(_scene.SetProperty(e, "Locked", SceneSchema.Bool(!locked)));
        var hidden = w.Hidden(e);
        if (ImGui.MenuItem(hidden ? "Show" : "Hide")) _afterTree = () => Report(_scene.SetProperty(e, "Hidden", SceneSchema.Bool(!hidden)));
        if (ImGui.MenuItem("Frame", "F")) _view.Frame(w, e);
        if (ImGui.MenuItem("Copy node path")) ImGui.SetClipboardText(NodePath(e));
        if (instance)
        {
            var source = w.Get<SceneInstance>(e).Source;
            ImGui.Separator();
            if (ImGui.MenuItem($"Open {Path.GetFileName(source)}")) _afterTree = () => OpenScene(source);
            if (ImGui.MenuItem("Revert overrides", default, false, !inherited && _scene.OverrideCount(e) > 0)) _afterTree = () => _scene.RevertInstance(e);
            if (ImGui.MenuItem("Reload from source")) _afterTree = () => _scene.ReloadInstancesOf(source);
        }
    }

    /// <summary>A node's path of names from the top, as scripts find it with <c>Scene.Find</c>.</summary>
    string NodePath(EntityId e)
    {
        var w = _scene.World;
        var parts = new List<string>();
        for (var a = e; !a.IsNone; a = w.Parent(a))
            parts.Add(w.Name(a));
        parts.Reverse();
        return string.Join('/', parts);
    }

    void RenamePopup()
    {
        if (_renameRequested && !_scene.Primary.IsNone)
        {
            _renameRequested = false;
            _renaming = _scene.Primary;
            _renameEdit.SetValue(_scene.World.Name(_renaming).AsSpan());
            ImGui.OpenPopup("rename");
        }
        if (!ImGui.BeginPopup("rename"))
            return;
        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();
        if (ImGui.InputText("Name", _renameEdit, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll))
        {
            if (_scene.World.IsAlive(_renaming) && !_renameEdit.IsEmpty)
                Report(_scene.SetProperty(_renaming, "Name", SceneSchema.Text(_renameEdit.ToString())));
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }
}
