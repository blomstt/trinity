namespace Trinity.Scenes;

/// <summary>
/// Notices when files change on disk: each tracked file's last-write time (or that it was missing)
/// is remembered, and <see cref="Changed"/> lists the files that differ now. Used to reload a model
/// that was re-exported, or one that failed to load and has since been fixed or added.
/// </summary>
public sealed class AssetStamps
{
    readonly Dictionary<string, DateTime> _stamps = [];

    static DateTime Stamp(string file) => File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;

    /// <summary>Remembers the file's state now (call when loading it).</summary>
    public void Track(string key, string file) => _stamps[key] = Stamp(file);

    public void Forget(string key) => _stamps.Remove(key);

    /// <summary>
    /// The tracked keys whose files changed (appeared, disappeared or were written) since tracked. They
    /// stay changed until the caller deals with them (<see cref="Forget"/> or <see cref="Track"/> again).
    /// </summary>
    public List<string> Changed(Func<string, string> fileOf)
    {
        var changed = new List<string>();
        foreach (var (key, stamp) in _stamps)
            if (Stamp(fileOf(key)) != stamp)
                changed.Add(key);
        return changed;
    }
}
