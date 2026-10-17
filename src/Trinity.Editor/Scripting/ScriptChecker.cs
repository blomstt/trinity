using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Trinity.Editor.Scripting;

/// <summary>
/// Finds errors in the project's C#: builds the game with <c>dotnet build</c> after a script is saved
/// (Debug, the same build Play uses, so it also warms that up), and while the game runs reads the
/// compiler output of <c>dotnet watch</c>/<c>run</c> and the script errors the game reports. Only problems in the
/// project's own files are kept.
/// </summary>
sealed partial class ScriptChecker
{
    readonly EditorProject _project;
    readonly Lock _gate = new();
    List<Diagnostic> _build = [], _runtime = [];
    Process? _process;
    bool _again;

    public string Status { get; private set; } = "";
    public bool Busy => _process != null;

    /// <summary>Set while the game runs (under dotnet watch or run), which then does the building.</summary>
    public bool Watching;

    readonly ScriptIndex? _index;

    /// <param name="index">To place runtime errors that come without a line (code applied by hot reload) at their script.</param>
    public ScriptChecker(EditorProject project, ScriptIndex? index = null)
    {
        _project = project;
        _index = index;
    }

    public List<Diagnostic> All
    {
        get
        {
            lock (_gate)
                return [.. _build, .. _runtime];
        }
    }

    public List<Diagnostic> For(string file) => All.Where(d => d.File == file).ToList();

    public void Check()
    {
        if (Watching || !File.Exists(_project.GamePath))
            return;
        lock (_gate)
        {
            if (_process != null)
            {
                _again = true;
                return;
            }
            _runtime.Clear();
        }
        var found = new List<Diagnostic>();
        var start = new ProcessStartInfo("dotnet")
        {
            ArgumentList = { "build", _project.GamePath, "-c", "Debug", "-nologo", "-v", "q", "-clp:NoSummary" },
            WorkingDirectory = _project.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        Status = "checking...";
        var process = Process.Start(start);
        if (process == null)
            return;
        void Read(string? line)
        {
            if (Parse(line) is { } d)
                lock (found)
                    found.Add(d);
        }
        process.OutputDataReceived += (_, e) => Read(e.Data);
        process.ErrorDataReceived += (_, e) => Read(e.Data);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            process.WaitForExit(); // all output read
            List<Diagnostic> result;
            lock (found)
                result = found.Distinct().ToList();
            bool again;
            lock (_gate)
            {
                _build = result;
                _process = null;
                again = _again;
                _again = false;
            }
            var errors = result.Count(d => d.Error);
            Status = errors > 0 ? $"{errors} error{(errors == 1 ? "" : "s")}" : result.Count > 0 ? $"{result.Count} warning{(result.Count == 1 ? "" : "s")}" : "no errors";
            Log.Info("scripts", $"checked {_project.Name}: {Status}");
            process.Dispose();
            if (again)
                Check();
        };
        lock (_gate)
            _process = process;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    /// <summary>A line of output from the running game (and dotnet watch).</summary>
    public void Feed(string line)
    {
        if (line.Contains("succeeded", StringComparison.OrdinalIgnoreCase) && (line.Contains("Hot reload", StringComparison.OrdinalIgnoreCase) || line.Contains("Build")))
        {
            lock (_gate)
            {
                _build.Clear();
                _runtime.Clear();
            }
            Status = "no errors";
            return;
        }
        if (Parse(line) is { } d)
        {
            lock (_gate)
                if (!_build.Contains(d))
                    _build.Add(d);
            Status = "errors";
            return;
        }
        // Script errors from Trinity.Scenes: "... at Spin.Update(...) in /path/Spin.cs:line 12"
        if (RuntimeError().Match(line) is { Success: true } r && Inside(r.Groups["file"].Value))
        {
            var message = line[..Math.Max(0, line.IndexOf(" at ", StringComparison.Ordinal))].Trim();
            if (message.IndexOf("script: ", StringComparison.Ordinal) is var prefix and >= 0)
                message = message[(prefix + "script: ".Length)..]; // drop the log line's time and level
            var runtime = new Diagnostic(r.Groups["file"].Value, int.Parse(r.Groups["line"].Value) - 1, 0, true, "runtime", message);
            AddRuntime(runtime);
        }
        // Without a line: "Spinner (Spin.Update): ..." points at the script class
        else if (RuntimeErrorNoLine().Match(line) is { Success: true } n && _index?.Find(n.Groups["type"].Value) is { } script)
        {
            var message = line[(line.IndexOf("script: ", StringComparison.Ordinal) + "script: ".Length)..];
            var cut = message.IndexOf(" at ", StringComparison.Ordinal);
            AddRuntime(new Diagnostic(script.File, script.Line, 0, true, "runtime", cut > 0 ? message[..cut] : message));
        }
    }

    void AddRuntime(Diagnostic d)
    {
        lock (_gate)
            if (!_runtime.Any(x => x.File == d.File && x.Line == d.Line))
                _runtime.Add(d);
    }

    public void ClearRuntime()
    {
        lock (_gate)
            _runtime.Clear();
    }

    bool Inside(string file) => file.StartsWith(_project.Root + "/", StringComparison.Ordinal);

    [GeneratedRegex(@"(?<file>/[^\s(]+\.cs)\((?<line>\d+),(?<col>\d+)\): (?<sev>error|warning) (?<code>\w+): (?<msg>.*?)(?: \[[^\]]*\])?$")]
    private static partial Regex CompilerMessage();

    [GeneratedRegex(@" in (?<file>/\S+\.cs):line (?<line>\d+)")]
    private static partial Regex RuntimeError();

    [GeneratedRegex(@" error script: .+? \((?<type>\w+)\.\w+\): ")]
    private static partial Regex RuntimeErrorNoLine();

    Diagnostic? Parse(string? line)
    {
        if (line == null || CompilerMessage().Match(line) is not { Success: true } m || !Inside(m.Groups["file"].Value))
            return null;
        return new Diagnostic(
            m.Groups["file"].Value,
            int.Parse(m.Groups["line"].Value) - 1,
            int.Parse(m.Groups["col"].Value) - 1,
            m.Groups["sev"].Value == "error",
            m.Groups["code"].Value,
            m.Groups["msg"].Value
        );
    }
}
