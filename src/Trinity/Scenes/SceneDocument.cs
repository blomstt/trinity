using System.Numerics;
using System.Text.Json;

namespace Trinity.Scenes;

/// <summary>
/// A scene file (<c>*.scene.json</c>), format 2: its nodes in hierarchy order (parents before children,
/// siblings in order), the environment, and where the camera starts. Version 1 files (a flat
/// <c>Objects</c> list) are migrated on load. A scene used inside another is a prefab: the same file.
/// </summary>
public sealed class SceneDocument
{
    public const int CurrentFormat = 2;

    public int Format { get; set; } = CurrentFormat;

    /// <summary>The next persistent id to give a new node, so ids stay unique within the scene forever.</summary>
    public long NextId { get; set; } = 1;

    public List<NodeRecord> Nodes { get; set; } = [];
    public SceneEnvironment? Environment { get; set; }
    public SceneCamera? Camera { get; set; }
}

/// <summary>One saved node: identity, place in the hierarchy, transform, components, or an instance of another scene.</summary>
public sealed class NodeRecord
{
    /// <summary>Persistent id, unique within this scene file (not the runtime <see cref="EntityId"/>).</summary>
    public long Id { get; set; }

    /// <summary>The parent's <see cref="Id"/>; 0 for a root node.</summary>
    public long Parent { get; set; }

    public string Name { get; set; } = "Node";
    public Vector3 Position { get; set; }
    public Vector3 Rotation { get; set; }
    public Vector3 Scale { get; set; } = Vector3.One;
    public bool Hidden { get; set; }

    /// <summary>Component name to property values, as <see cref="SceneSchema"/> names them.</summary>
    public Dictionary<string, Dictionary<string, JsonElement>>? Components { get; set; }

    /// <summary>A scene this node instances (its contents appear as this node's children).</summary>
    public string? Instance { get; set; }

    /// <summary>For an instance: source node path ("7", "3/7" through nested instances) to overridden property paths and values.</summary>
    public Dictionary<string, Dictionary<string, JsonElement>>? Overrides { get; set; }
}
