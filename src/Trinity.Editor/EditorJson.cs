using System.Text.Json.Serialization;

namespace Trinity.Editor;

/// <summary>
/// JSON metadata for the editor's own files (scenes use Trinity's), generated at build time so the
/// editor runs under NativeAOT without reflection.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(EditorProject))]
[JsonSerializable(typeof(Dictionary<string, string[]>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<ProjectEntry>))]
sealed partial class EditorJson : JsonSerializerContext;
