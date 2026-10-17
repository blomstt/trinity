using Trinity.Scenes;

namespace Trinity.Editor;

/// <summary>The scene being edited: Trinity's scene file, plus where it's saved and whether it changed.</summary>
sealed class EditorScene
{
    public SceneFile File = new();
    public List<Node> Objects => File.Objects;
    public string? Path;
    public bool Dirty;

    public static EditorScene Load(string path) => new() { File = SceneFile.Load(path), Path = path };

    public void Save(string path)
    {
        File.Save(path);
        Path = path;
        Dirty = false;
    }
}
