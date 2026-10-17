using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Trinity;

/// <summary>
/// Player settings in one JSON file per game: <c>$XDG_CONFIG_HOME/&lt;app&gt;/settings.json</c>
/// (normally <c>~/.config/&lt;app&gt;/</c>). Each system keeps its own named section, read and written
/// as a plain C# type. Unknown sections are kept, so older and newer builds can share a file.
/// Types are described by source-generated JSON metadata, which NativeAOT needs:
/// <c>settings.Get("audio", MyJson.Default.AudioSettings)</c>, where <c>MyJson</c> is a
/// <see cref="JsonSerializerContext"/> like <see cref="TrinityJson"/>.
/// </summary>
public sealed class Settings
{
    readonly JsonObject _root;
    public string FilePath { get; }

    Settings(string path, JsonObject root)
    {
        FilePath = path;
        _root = root;
    }

    public static Settings Load(string appName)
    {
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        var path = Path.Combine(config, appName, "settings.json");
        JsonObject root = [];
        if (File.Exists(path))
        {
            try
            {
                root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? [];
            }
            catch (JsonException e)
            {
                Log.Warn("settings", $"{path} is not valid JSON, using defaults ({e.Message})");
                File.Copy(path, path + ".broken", overwrite: true);
            }
        }
        return new Settings(path, root);
    }

    /// <summary>The section's values, or a new <typeparamref name="T"/> (its defaults) if it's missing.</summary>
    public T Get<T>(string section, JsonTypeInfo<T> type) where T : new()
    {
        if (_root[section] is not { } node)
            return new T();
        try
        {
            return node.Deserialize(type) ?? new T();
        }
        catch (JsonException e)
        {
            Log.Warn("settings", $"section '{section}' is unreadable, using defaults ({e.Message})");
            return new T();
        }
    }

    public void Set<T>(string section, T value, JsonTypeInfo<T> type) => _root[section] = JsonSerializer.SerializeToNode(value, type);

    /// <summary>Writes the file atomically, so a crash mid-save can't corrupt it.</summary>
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, _root.ToJsonString(TrinityJson.Default.Options));
        File.Move(temp, FilePath, overwrite: true);
    }
}

/// <summary>JSON metadata for the engine's own settings sections.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, IncludeFields = true)]
[JsonSerializable(typeof(DisplaySettings))]
[JsonSerializable(typeof(Dictionary<string, string[]>))]
public sealed partial class TrinityJson : JsonSerializerContext;

/// <summary>The "display" settings section, applied by the engine at start.</summary>
public sealed class DisplaySettings
{
    public int Width = 1600;
    public int Height = 900;
    public bool Fullscreen;
    public bool VSync = true;
}
