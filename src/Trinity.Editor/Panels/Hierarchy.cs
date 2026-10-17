using Brutal.ImGuiApi;
using Brutal.Numerics;
using Trinity.Scenes;

namespace Trinity.Editor;

/// <summary>
/// The Hierarchy panel: the scene's nodes as a tree. Click selects (Ctrl+click adds), drag a node onto
/// another to put it under it (with Shift: just before it), onto empty space to move it to the top.
/// Instances of other scenes show their source; the nodes inside them are dimmed: they come from the
/// source scene, so they can be edited (as overrides) but not moved, deleted or given children.
/// </summary>
sealed partial class EditorApp
{
    const string NodePayload = "trinity-node";
    static readonly float4 InheritedColor = ImGuiHost.Color(0.55f, 0.58f, 0.62f);
    static readonly float4 InstanceColor = ImGuiHost.Color(0.62f, 0.78f, 0.95f);

    EntityId _dragged;
    Action? _afterTree; // changes the tree can't take while it's being walked
    bool _renameRequested;
    bool _revealSelection; // unfold the tree down to the selection (picked in the viewport, say) and scroll to it
    EntityId _renaming;
    readonly ImInputString _renameEdit = new(128);

    void Hierarchy()
    {
        ImGui.Begin(HierarchyPanel, ref _showHierarchy);
        var w = _scene.World;
        if (ImGui.SmallButton("+ Node"))
            _scene.CreateNode("Node", _scene.Primary);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("New empty node under the selection (Ctrl+Shift+N)");
        ImGui.SameLine();
        if (ImGui.SmallButton("+ Instance"))
            ImGui.OpenPopup("instance-scene");
        if (ImGui.BeginPopup("instance-scene"))
        {
            ScanFiles();
            if (_sceneFiles.Count(f => f != _scene.Path) == 0)
                ImGui.TextDisabled("No other scenes");
            foreach (var f in _sceneFiles)
                if (f != _scene.Path && ImGui.MenuItem(f))
                    InstanceScene(f);
            ImGui.EndPopup();
        }
        ImGui.Separator();

        if (w.Count == 0)
            ImGui.TextDisabled("Empty scene. Double-click a model in Assets to place it.");
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
            ImGui.EndDragDropTarget();
        }
        if (ImGui.BeginPopupContextItem("below"))
        {
            if (ImGui.MenuItem("New node")) _afterTree = () => _scene.CreateNode("Node");
            ImGui.EndPopup();
        }

        RenamePopup();
        ImGui.End();
        if (_afterTree is { } after)
        {
            _afterTree = null;
            after();
        }
    }

    void TreeItem(EntityId e)
    {
        var w = _scene.World;
        var inherited = _scene.IsInherited(e);
        var instance = w.Has<SceneInstance>(e);
        var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth;
        if (w.ChildCount(e) == 0)
            flags |= ImGuiTreeNodeFlags.Leaf;
        else if (!instance)
            flags |= ImGuiTreeNodeFlags.DefaultOpen; // instances start folded: their insides are the source's
        if (_scene.Selection.Contains(e))
            flags |= ImGuiTreeNodeFlags.Selected;

        ImGui.PushID(e.Index);
        if (_revealSelection && e != _scene.Primary && w.IsAlive(_scene.Primary) && w.IsSelfOrAncestor(e, _scene.Primary))
            ImGui.SetNextItemOpen(true);
        var label = w.Name(e);
        if (instance)
            label += $"   ({Path.GetFileName(w.Get<SceneInstance>(e).Source).Replace(".scene.json", "")})";
        if (w.Has<ScriptComponent>(e) && w.Get<ScriptComponent>(e).Class is { Length: > 0 } cls)
            label += $"   [{cls}]";
        if (w.Hidden(e))
            label += "   (hidden)";
        var colored = inherited || instance;
        if (colored)
            ImGui.PushStyleColor(ImGuiCol.Text, inherited ? InheritedColor : InstanceColor);
        var open = ImGui.TreeNodeEx(label, flags);
        if (colored)
            ImGui.PopStyleColor();

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
            ImGui.EndDragDropTarget();
        }

        if (ImGui.BeginPopupContextItem())
        {
            if (!_scene.Selection.Contains(e))
                _scene.Select(e);
            NodeMenu(e, inherited, instance);
            ImGui.EndPopup();
        }

        if (open)
        {
            foreach (var c in w.Children(e))
                TreeItem(c);
            ImGui.TreePop();
        }
        ImGui.PopID();
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
        if (ImGui.MenuItem("New child node", default, false, !inherited)) _afterTree = () => _scene.CreateNode("Node", e);
        if (ImGui.MenuItem("Rename", "F2")) _renameRequested = true;
        if (ImGui.MenuItem("Duplicate", "Ctrl+D", false, !inherited)) _afterTree = () => _scene.Duplicate(_scene.Selection);
        if (ImGui.MenuItem("Delete", "Del", false, !inherited)) _afterTree = () => Report(_scene.Delete(_scene.Selection));
        if (ImGui.MenuItem("Move to top level", default, false, !inherited && !w.Parent(e).IsNone)) _afterTree = () => Report(_scene.Reparent(e, EntityId.None));
        ImGui.Separator();
        if (ImGui.MenuItem("Frame", "F")) _view.Frame(w, e);
        if (instance)
        {
            var source = w.Get<SceneInstance>(e).Source;
            ImGui.Separator();
            if (ImGui.MenuItem($"Open {Path.GetFileName(source)}")) _afterTree = () => OpenScene(source);
            if (ImGui.MenuItem("Revert overrides", default, false, !inherited && _scene.OverrideCount(e) > 0)) _afterTree = () => _scene.RevertInstance(e);
            if (ImGui.MenuItem("Reload from source")) _afterTree = () => _scene.ReloadInstancesOf(source);
        }
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
