using System.Text.Json;
using Trinity.Scenes;

namespace Trinity.Editor;

/// <summary>
/// File operations the FileSystem dock makes on a project: moving and renaming with every scene's
/// references updated, finding what refers to a file, and deleting to the desktop's trash (so a delete
/// can be undone from the file manager).
/// </summary>
static class ProjectFiles
{
    /// <summary>Folders and files in <paramref name="folder"/> the dock and these operations leave alone: hidden ones, build output, and the editor's own working files.</summary>
    public static bool Ignored(string folder, string name) =>
        name.StartsWith('.') || IsBuildOutput(folder, name)
        || name.EndsWith(".autosave.scene.json", StringComparison.Ordinal)
        || name.EndsWith(".unsaved", StringComparison.Ordinal) || name.EndsWith(".tmp", StringComparison.Ordinal);

    /// <summary>A .NET project's bin or obj folder: only beside a .csproj, so an asset folder that happens to be called "bin" is kept.</summary>
    public static bool IsBuildOutput(string folder, string name) =>
        name is "bin" or "obj" && Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.csproj").Any();

    /// <summary>Every scene file in the project, project-relative.</summary>
    public static IEnumerable<string> Scenes(string root) =>
        Walk(root, root).Where(p => p.EndsWith(".scene.json", StringComparison.Ordinal));

    static IEnumerable<string> Walk(string root, string dir)
    {
        foreach (var f in Directory.EnumerateFiles(dir))
            if (!Ignored(dir, Path.GetFileName(f)))
                yield return SceneLibrary.Normalize(Path.GetRelativePath(root, f));
        foreach (var d in Directory.EnumerateDirectories(dir))
            if (!Ignored(dir, Path.GetFileName(d)))
                foreach (var f in Walk(root, d))
                    yield return f;
    }

    /// <summary><paramref name="path"/> after moving <paramref name="from"/> (a file, or a folder and everything in it) to <paramref name="to"/>, or null if it's not affected.</summary>
    public static string? Moved(string path, string from, string to) =>
        path == from ? to
        : path.StartsWith(from + "/", StringComparison.Ordinal) ? to + path[from.Length..]
        : null;

    /// <summary>The scenes that refer to a file, or to anything in a folder: as a model, another asset, or an instanced scene.</summary>
    public static List<string> ReferencesTo(SceneLibrary library, string path)
    {
        var found = new List<string>();
        foreach (var scene in Scenes(library.Root))
            if (Rewrite(library, scene, path, path, write: false))
                found.Add(scene);
        return found;
    }

    /// <summary>
    /// Moves or renames a file or folder, then points every scene's references at the new place.
    /// Returns the scenes changed. Scripts can name assets too; those are listed, not edited.
    /// </summary>
    public static (List<string> Scenes, List<string> Scripts) Move(SceneLibrary library, string from, string to)
    {
        var source = library.Absolute(from);
        var target = library.Absolute(to);
        if (File.Exists(target) || Directory.Exists(target))
            throw new IOException($"{to} already exists");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (Directory.Exists(source))
            Directory.Move(source, target);
        else
            File.Move(source, target);

        var scenes = new List<string>();
        foreach (var scene in Scenes(library.Root))
            if (Rewrite(library, scene, from, to, write: true))
                scenes.Add(scene);
        var scripts = Walk(library.Root, library.Root)
            .Where(p => p.EndsWith(".cs", StringComparison.Ordinal) && File.ReadAllText(library.Absolute(p)).Contains(from, StringComparison.Ordinal))
            .ToList();
        return (scenes, scripts);
    }

    /// <summary>Rewrites one scene's references from <paramref name="from"/> to <paramref name="to"/>; true if it has any.</summary>
    static bool Rewrite(SceneLibrary library, string scene, string from, string to, bool write)
    {
        string json;
        try
        {
            json = File.ReadAllText(library.Absolute(scene));
        }
        catch (IOException)
        {
            return false;
        }
        if (!json.Contains(from, StringComparison.Ordinal))
            return false; // quick: most scenes don't mention it at all
        var (doc, _) = SceneLibrary.Parse(json, scene);
        if (doc == null)
            return false;
        var changed = false;
        foreach (var node in doc.Nodes)
        {
            if (node.Instance != null && Moved(SceneLibrary.Normalize(node.Instance), from, to) is { } instance)
            {
                node.Instance = instance;
                changed = true;
            }
            foreach (var (component, props) in node.Components ?? [])
                changed |= RewriteAssets(props, component + ".", from, to);
            if (node.Overrides != null)
                foreach (var props in node.Overrides.Values)
                    changed |= RewriteAssets(props, "", from, to);
        }
        if (changed && write)
            library.Save(scene, doc);
        return changed;
    }

    /// <summary>Asset-path properties (by the schema) that name the moved file.</summary>
    static bool RewriteAssets(Dictionary<string, JsonElement> props, string prefix, string from, string to)
    {
        var changed = false;
        foreach (var key in props.Keys.ToList())
        {
            if (!IsAsset(prefix + key) || props[key].ValueKind != JsonValueKind.String)
                continue;
            if (Moved(props[key].GetString() ?? "", from, to) is { } moved)
            {
                props[key] = SceneSchema.Text(moved);
                changed = true;
            }
        }
        return changed;
    }

    static bool IsAsset(string path)
    {
        var (component, property) = SceneSchema.Split(path);
        return component != null && SceneSchema.Component(component)?.Properties.FirstOrDefault(p => p.Name == property)?.Kind == PropertyKind.Asset;
    }

    /// <summary>Points the open scene's own asset references at the moved file (the same rewrite, in memory). True if any changed.</summary>
    public static bool Rewrite(World world, string from, string to)
    {
        var changed = false;
        foreach (var e in world.All())
        {
            foreach (var def in SceneSchema.Components)
            {
                if (!def.Has(world, e))
                    continue;
                foreach (var p in def.Properties)
                    if (p.Kind == PropertyKind.Asset && p.Get(world, e) is { ValueKind: JsonValueKind.String } v && Moved(v.GetString() ?? "", from, to) is { } moved)
                    {
                        p.Set(world, e, SceneSchema.Text(moved));
                        changed = true;
                    }
            }
            if (world.Has<SceneInstance>(e) && Moved(world.Get<SceneInstance>(e).Source, from, to) is { } source)
            {
                world.Get<SceneInstance>(e).Source = source;
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>
    /// Moves a file or folder to the desktop trash (the freedesktop.org layout every Linux file manager
    /// uses), so it can be restored. Returns where it went.
    /// </summary>
    public static string Trash(string fullPath)
    {
        var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x
            ? x
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        var mounts = File.Exists("/proc/self/mounts") ? File.ReadAllText("/proc/self/mounts") : "";
        return Trash(fullPath, data, mounts, UserId());
    }

    /// <summary>
    /// <see cref="Trash(string)"/> with what it reads from the system passed in (for tests): the user's data
    /// folder, the mount table and the user id.
    /// </summary>
    internal static string Trash(string fullPath, string dataHome, string mounts, string uid)
    {
        var (trash, pathInInfo) = TrashFor(Path.GetFullPath(fullPath), dataHome, mounts, uid);
        var files = Path.Combine(trash, "files");
        var info = Path.Combine(trash, "info");
        Directory.CreateDirectory(files);
        Directory.CreateDirectory(info);
        var name = Path.GetFileName(fullPath.TrimEnd('/'));
        var unique = name;
        for (var i = 2; File.Exists(Path.Combine(files, unique)) || Directory.Exists(Path.Combine(files, unique)) || File.Exists(Path.Combine(info, unique + ".trashinfo")); i++)
            unique = $"{Path.GetFileNameWithoutExtension(name)}.{i}{Path.GetExtension(name)}";
        // The info file first, as the spec asks, so a crash between the two leaves nothing unaccounted for
        File.WriteAllText(Path.Combine(info, unique + ".trashinfo"),
            $"[Trash Info]\nPath={Uri.EscapeDataString(pathInInfo).Replace("%2F", "/")}\nDeletionDate={DateTime.Now:yyyy-MM-ddTHH:mm:ss}\n");
        var target = Path.Combine(files, unique);
        try
        {
            if (Directory.Exists(fullPath))
                Directory.Move(fullPath, target);
            else
                File.Move(fullPath, target);
        }
        catch (Exception e)
        {
            File.Delete(Path.Combine(info, unique + ".trashinfo"));
            throw new IOException($"couldn't move it into {trash}: {e.Message}", e);
        }
        return target;
    }

    /// <summary>
    /// Which trash a file goes to, as the freedesktop.org spec says: the user's own (in their data folder)
    /// for files on the same drive, otherwise that drive's own, $topdir/.Trash-$uid, since a move can't
    /// cross drives. Returns the trash folder and the path its .trashinfo records (relative to the drive's
    /// top for a drive's own trash).
    /// </summary>
    internal static (string Trash, string PathInInfo) TrashFor(string fullPath, string dataHome, string mounts, string uid)
    {
        var drive = MountPointOf(fullPath, mounts);
        if (drive == MountPointOf(Path.GetFullPath(dataHome), mounts))
            return (Path.Combine(dataHome, "Trash"), fullPath);
        return (Path.Combine(drive, ".Trash-" + uid), Path.GetRelativePath(drive, fullPath));
    }

    /// <summary>The mount point a path is on: the longest one in the mount table (/proc/self/mounts) that contains it.</summary>
    internal static string MountPointOf(string path, string mounts)
    {
        var best = "/";
        foreach (var line in mounts.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(' ');
            if (fields.Length < 2)
                continue;
            // Spaces and other awkward characters are written as octal escapes (\040)
            var point = System.Text.RegularExpressions.Regex.Replace(fields[1], @"\\([0-7]{3})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 8)).ToString());
            var inside = point == "/" || path == point || path.StartsWith(point.TrimEnd('/') + "/", StringComparison.Ordinal);
            if (inside && point.Length > best.Length)
                best = point;
        }
        return best;
    }

    /// <summary>The user's id, for a drive's own trash folder (from /proc, so no native call is needed).</summary>
    static string UserId()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/self/status"))
                if (line.StartsWith("Uid:", StringComparison.Ordinal))
                    return line.Split('\t', ' ', StringSplitOptions.RemoveEmptyEntries)[1];
        }
        catch (IOException) { }
        return Environment.UserName;
    }

    /// <summary>
    /// The name a renamed file keeps its kind with: typing "boulder" for "rock.glb" gives "boulder.glb",
    /// and a scene keeps ".scene.json". A name typed with an extension of its own is used as typed.
    /// </summary>
    public static string KeepExtension(string original, string typed)
    {
        if (original.EndsWith(".scene.json", StringComparison.Ordinal))
            return typed.EndsWith(".scene.json", StringComparison.Ordinal) ? typed : typed.TrimEnd('.') + ".scene.json";
        var ext = Path.GetExtension(original);
        return ext.Length == 0 || Path.GetExtension(typed).Length > 0 ? typed : typed + ext;
    }

    /// <summary>A path in <paramref name="folder"/> that isn't taken: "rock.glb", then "rock_1.glb" and so on.</summary>
    public static string FreeName(string root, string folder, string name)
    {
        var ext = name.EndsWith(".scene.json", StringComparison.Ordinal) ? ".scene.json" : Path.GetExtension(name);
        var stem = name[..^ext.Length];
        var candidate = name;
        string Rel(string n) => folder.Length == 0 ? n : folder + "/" + n;
        for (var i = 1; File.Exists(Path.Combine(root, Rel(candidate))) || Directory.Exists(Path.Combine(root, Rel(candidate))); i++)
            candidate = $"{stem}_{i}{ext}";
        return Rel(candidate);
    }

    /// <summary>
    /// Creates what a name asks for in <paramref name="folder"/>: a folder for a name
    /// ending in "/", an empty scene for ".scene.json", otherwise an empty file. Names may hold folders
    /// ("models/rock/" makes both). Returns the project-relative path.
    /// </summary>
    public static string Create(string root, string folder, string name)
    {
        if (name.Contains('\\') || name.StartsWith('/') || name.Split('/').Any(part => part is "." or ".."))
            throw new IOException($"{name} isn't a name this can create");
        var path = (folder.Length == 0 ? "" : folder + "/") + name.TrimEnd('/');
        var full = Path.Combine(root, path);
        if (File.Exists(full) || Directory.Exists(full))
            throw new IOException($"{path} already exists");
        if (name.EndsWith('/'))
            Directory.CreateDirectory(full);
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            if (name.EndsWith(".scene.json", StringComparison.Ordinal))
                new SceneLibrary(root).Save(path, new SceneDocument());
            else
                File.WriteAllText(full, "");
        }
        return path;
    }

    /// <summary>Copies a file, or a folder and everything in it (leaving out build output).</summary>
    public static void Copy(string from, string to)
    {
        if (File.Exists(from))
        {
            File.Copy(from, to);
            return;
        }
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from))
            File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.EnumerateDirectories(from))
            if (!IsBuildOutput(from, Path.GetFileName(d)))
                Copy(d, Path.Combine(to, Path.GetFileName(d)));
    }
}
