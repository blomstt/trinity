using System.Text.Json;
using System.Text.Json.Nodes;

namespace Trinity;

/// <summary>
/// Player settings in one JSON file per game: <c>$XDG_CONFIG_HOME/&lt;app&gt;/settings.json</c>
/// (normally <c>~/.config/&lt;app&gt;/</c>). Each system keeps its own named section, read and written
/// as a plain C# type. Unknown sections are kept, so older and newer builds can share a file.
/// </summary>
public sealed class Settings
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };

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
    public T Get<T>(string section) where T : new()
    {
        if (_root[section] is not { } node)
            return new T();
        try
        {
            return node.Deserialize<T>(Json) ?? new T();
        }
        catch (JsonException e)
        {
            Log.Warn("settings", $"section '{section}' is unreadable, using defaults ({e.Message})");
            return new T();
        }
    }

    public void Set<T>(string section, T value) => _root[section] = JsonSerializer.SerializeToNode(value, Json);

    /// <summary>Writes the file atomically, so a crash mid-save can't corrupt it.</summary>
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, _root.ToJsonString(Json));
        File.Move(temp, FilePath, overwrite: true);
    }
}

/// <summary>The "display" settings section, applied by the engine at start.</summary>
public sealed class DisplaySettings
{
    public int Width = 1600;
    public int Height = 900;
    public bool Fullscreen;
    public bool VSync = true;
}
