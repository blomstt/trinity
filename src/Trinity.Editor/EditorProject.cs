using System.Text.Json;


namespace Trinity.Editor;

/// <summary>
/// A game the editor works on, described by a <c>trinity.json</c> at the game's root:
/// <code>{ "name": "Varendal", "game": "src/Varendal/Varendal.csproj", "assets": "Assets", "scenes": "Scenes", "scripts": "Scripts" }</code>
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

    public string Root { get; private set; } = "";
    public string AssetsPath => Path.Combine(Root, Assets);
    public string ScenesPath => Path.Combine(Root, Scenes);
    public string ScriptsPath => Path.Combine(Root, Scripts);
    public string GamePath => Path.Combine(Root, Game);

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
}
