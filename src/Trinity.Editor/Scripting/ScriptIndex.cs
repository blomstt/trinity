using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Trinity.Editor.Scripting;

/// <summary>A public field of a script, as the Inspector shows it.</summary>
sealed record ScriptField(string Name, string Type, object Default);

sealed record ScriptInfo(string Name, string File, int Line, IReadOnlyList<ScriptField> Fields);

/// <summary>
/// The scripts in a project, read from source: every <c>class X : Script</c> and its public fields of the
/// types the Inspector edits, with their initial values. Reading source (not the compiled game) keeps the
/// editor independent of the game's build, so it stays a NativeAOT program and knows about a field as soon
/// as it's typed.
/// </summary>
sealed partial class ScriptIndex
{
    public static readonly string[] FieldTypes = ["float", "double", "int", "bool", "string", "Vector3", "Node"];

    readonly string _root;
    Dictionary<string, ScriptInfo> _scripts = [];
    readonly Dictionary<string, DateTime> _scanned = [];
    double _lastScan = -10;

    public ScriptIndex(string root) => _root = root;

    public IEnumerable<ScriptInfo> All => _scripts.Values.OrderBy(s => s.Name);

    public ScriptInfo? Find(string? name) => name != null && _scripts.TryGetValue(name, out var s) ? s : null;

    /// <summary>The project's C# files, outside bin/ and obj/.</summary>
    public IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(_root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("/bin/") && !f.Contains("/obj/"));

    /// <summary>Rescans changed files, at most once a second unless forced.</summary>
    public void Refresh(double now, bool force = false)
    {
        if (!force && now - _lastScan < 1)
            return;
        _lastScan = now;
        var files = SourceFiles().ToList();
        var changed = force || files.Count != _scanned.Count || files.Any(f => !_scanned.TryGetValue(f, out var t) || t != File.GetLastWriteTimeUtc(f));
        if (!changed)
            return;
        _scanned.Clear();
        var scripts = new Dictionary<string, ScriptInfo>();
        foreach (var file in files)
        {
            _scanned[file] = File.GetLastWriteTimeUtc(file);
            foreach (var s in Scan(file, File.ReadAllLines(file)))
                scripts[s.Name] = s;
        }
        _scripts = scripts;
    }

    [GeneratedRegex(@"\bclass\s+(\w+)\s*:\s*(?:Trinity\.Scenes\.)?Script\b")]
    private static partial Regex ScriptClass();

    [GeneratedRegex(@"^\s*public\s+([\w.]+)\s+(\w+)\s*(?:=(.*))?$")]
    private static partial Regex PublicField();

    static IEnumerable<ScriptInfo> Scan(string file, string[] lines)
    {
        var code = CSharpLexer.Code(lines);
        for (var l = 0; l < code.Length; l++)
        {
            var m = ScriptClass().Match(code[l]);
            if (!m.Success)
                continue;
            yield return new ScriptInfo(m.Groups[1].Value, file, l, Fields(lines, code, l, m.Index + m.Length));
        }
    }

    /// <summary>Statements directly in the class body that declare a public field.</summary>
    static List<ScriptField> Fields(string[] lines, string[] code, int line, int col)
    {
        var fields = new List<ScriptField>();
        var depth = 0;
        var started = false;
        var statement = new System.Text.StringBuilder();
        var original = new System.Text.StringBuilder();
        for (var l = line; l < code.Length; l++)
        {
            for (var c = l == line ? col : 0; c < code[l].Length; c++)
            {
                var ch = code[l][c];
                if (ch == '{')
                {
                    depth++;
                    if (depth == 1)
                        started = true;
                    statement.Clear();
                    original.Clear();
                    continue;
                }
                if (ch == '}')
                {
                    depth--;
                    if (started && depth == 0)
                        return fields;
                    statement.Clear();
                    original.Clear();
                    continue;
                }
                if (depth != 1)
                    continue;
                if (ch == ';')
                {
                    if (PublicField().Match(statement.ToString()) is { Success: true } f)
                    {
                        var type = f.Groups[1].Value.Replace("System.Numerics.", "");
                        var init = f.Groups[3].Success ? original.ToString()[(original.ToString().IndexOf('=') + 1)..].Trim() : "";
                        if (FieldTypes.Contains(type))
                            fields.Add(new ScriptField(f.Groups[2].Value, type, Parse(type, init)));
                    }
                    statement.Clear();
                    original.Clear();
                    continue;
                }
                statement.Append(ch);
                original.Append(lines[l][c]);
            }
            statement.Append(' ');
            original.Append(' ');
        }
        return fields;
    }

    [GeneratedRegex(@"^new\s*(?:Vector3)?\s*\(([^,]+),([^,]+),([^)]+)\)$")]
    private static partial Regex NewVector3();

    /// <summary>A field's initial value from its initialiser, when it's a plain literal; otherwise the type's default.</summary>
    static object Parse(string type, string init)
    {
        static float F(string s) =>
            float.TryParse(s.Trim().TrimEnd('f', 'F', 'd', 'D', 'm', 'M'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        switch (type)
        {
            case "float":
                return F(init);
            case "double":
                return (double)F(init);
            case "int":
                return int.TryParse(init, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : 0;
            case "bool":
                return init == "true";
            case "string":
                return init.Length >= 2 && init[0] == '"' && init[^1] == '"' ? init[1..^1] : "";
            case "Node":
                return ""; // no node until one is picked
            default:
                if (NewVector3().Match(init) is { Success: true } v)
                    return new Vector3(F(v.Groups[1].Value), F(v.Groups[2].Value), F(v.Groups[3].Value));
                return init switch
                {
                    "Vector3.One" => Vector3.One,
                    "Vector3.UnitX" => Vector3.UnitX,
                    "Vector3.UnitY" => Vector3.UnitY,
                    "Vector3.UnitZ" => Vector3.UnitZ,
                    _ => Vector3.Zero,
                };
        }
    }

    /// <summary>A new script file, ready to fill in.</summary>
    public static string Template(string name) =>
        $$"""
        using System.Numerics;
        using Trinity;
        using Trinity.Scenes;

        public sealed class {{name}} : Script
        {
            // Public fields show in the Inspector
            public float Speed = 1;

            public override void Ready()
            {
            }

            public override void Update(in FrameTime time)
            {
            }
        }

        """;
}
