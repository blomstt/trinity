using System.Text.Json.Serialization;

namespace Trinity.Editor;

/// <summary>The editor's last project, kept in its settings.</summary>
sealed class RecentProjects
{
    public string? Last { get; set; }
}

/// <summary>
/// JSON metadata for the editor's own files (scenes use Trinity's), generated at build time so the
/// editor runs under NativeAOT without reflection.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(EditorProject))]
[JsonSerializable(typeof(RecentProjects))]
sealed partial class EditorJson : JsonSerializerContext;
