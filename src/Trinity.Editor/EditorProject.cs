using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;


namespace Trinity.Editor;

/// <summary>
/// A game the editor works on, described by a <c>trinity.json</c> at the game's root:
/// <code>{ "name": "My game", "game": "src/MyGame/MyGame.csproj", "assets": "Assets", "scenes": "Scenes", "scripts": "Scripts" }</code>
/// Paths are relative to the file.
/// </summary>
sealed class EditorProject
{
    // set, not init: the generated JSON code would null an init property missing from the file
    public string Name { get; set; } = "Untitled";
    public string Game { get; set; } = "";
    public string Assets { get; set; } = "Assets";
    public string Scenes { get; set; } = "Scenes";
    public string Scripts { get; set; } = "Scripts";
    /// <summary>Labels shown and filtered on in the Project Manager.</summary>
    public string[] Tags { get; set; } = [];
    /// <summary>The Trinity version the project was made with.</summary>
    public string? Engine { get; set; }

    [JsonIgnore] public string Root { get; private set; } = "";
    [JsonIgnore] public string AssetsPath => Path.Combine(Root, Assets);
    [JsonIgnore] public string ScenesPath => Path.Combine(Root, Scenes);
    [JsonIgnore] public string ScriptsPath => Path.Combine(Root, Scripts);
    [JsonIgnore] public string GamePath => Path.Combine(Root, Game);

    public static EditorProject Load(string pathOrFolder)
    {
        var file = Directory.Exists(pathOrFolder) ? Path.Combine(pathOrFolder, "trinity.json") : pathOrFolder;
        if (!File.Exists(file))
            throw new FileNotFoundException($"No trinity.json at {pathOrFolder}");
        var project = JsonSerializer.Deserialize(File.ReadAllText(file), EditorJson.Default.EditorProject) ?? new EditorProject();
        project.Root = Path.GetDirectoryName(Path.GetFullPath(file))!;
        return project;
    }

    /// <summary>A path relative to the project root, for saving in scenes.</summary>
    public string Relative(string path) => Path.GetRelativePath(Root, path);

    public string Absolute(string relative) => Path.GetFullPath(Path.Combine(Root, relative));

    public static string FileIn(string folder) => Path.Combine(folder, "trinity.json");

    /// <summary>Changes fields of a project's trinity.json in place, keeping everything else in it as it was.</summary>
    public static void Edit(string folder, Action<JsonObject> change)
    {
        var file = FileIn(folder);
        var json = JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? new JsonObject();
        change(json);
        var temp = file + ".tmp";
        File.WriteAllText(temp, json.ToJsonString(EditorJson.Default.Options));
        File.Move(temp, file, overwrite: true);
    }
}
