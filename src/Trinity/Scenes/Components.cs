using System.Text.Json;

namespace Trinity.Scenes;

// Trinity's built-in components. Plain structs stored contiguously per type (ComponentStore); they hold
// data and references (asset paths, entity ids), never the shared resources themselves. Defaults are
// the zero values, so `default` and `new()` agree.

/// <summary>Draws a model. <see cref="Mesh"/> is the model's project-relative path, its stable resource id; every renderer naming the same path shares one GPU mesh.</summary>
public struct MeshRenderer
{
    public string Mesh;
    /// <summary>Not drawn into shadow maps.</summary>
    public bool NoShadows;

    public MeshRenderer(string mesh) => Mesh = mesh;
}

/// <summary>Plays one of the model's clips. <see cref="Time"/> is runtime state (where the clip is), not saved.</summary>
public struct Animator
{
    public string Clip;
    /// <summary>Playback rate; 1 is normal speed. Stored as the offset from 1, so the default is normal speed.</summary>
    public float SpeedOffset;
    public bool Paused;
    /// <summary>Play the clip once and hold its last frame, instead of looping.</summary>
    public bool Once;
    public float Time;

    public Animator(string clip) => Clip = clip;

    public float Speed
    {
        readonly get => 1 + SpeedOffset;
        set => SpeedOffset = value - 1;
    }
}

/// <summary>A camera looking down the node's -Z axis. The first one marked <see cref="Current"/> is what a scene game renders from.</summary>
public struct Camera3D
{
    /// <summary>Vertical field of view in degrees (0: 60).</summary>
    public float FieldOfView;
    /// <summary>Near and far planes in metres (0: 0.1 and 2000).</summary>
    public float Near, Far;
    public bool Current;

    public readonly float FieldOfViewOrDefault => FieldOfView > 0 ? FieldOfView : 60;
    public readonly float NearOrDefault => Near > 0 ? Near : 0.1f;
    public readonly float FarOrDefault => Far > 0 ? Far : 2000;
}

/// <summary>The sun: light travelling along the node's -Z axis. The scene's environment supplies the sky; this sets where the sun is and how strong.</summary>
public struct DirectionalLight
{
    /// <summary>Sunlight at the top of the atmosphere (0: the environment's).</summary>
    public float Illuminance;
}

/// <summary>A C# script on the node: its class and the field values set in the editor. <see cref="Instance"/> is the running script.</summary>
public struct ScriptComponent
{
    public string Class;
    public Dictionary<string, JsonElement>? Fields;
    public Script? Instance;

    public ScriptComponent(string className) => Class = className;
}

/// <summary>
/// Marks a node as an instance of another scene (a prefab): its children came from <see cref="Source"/>,
/// and <see cref="Overrides"/> holds the properties changed on this instance, by source node path
/// ("12", or "12/3" through nested instances) and property name.
/// </summary>
public struct SceneInstance
{
    public string Source;
    public Dictionary<string, Dictionary<string, JsonElement>> Overrides;
}

/// <summary>Marks a node that came from an instanced scene: which instance root it belongs to, and its path in the source.</summary>
public struct Inherited
{
    public EntityId InstanceRoot;
    public string SourcePath;
}
