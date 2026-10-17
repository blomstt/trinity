using System.Diagnostics;

namespace Trinity.Editor;

/// <summary>
/// Runs the project's game as its own process with <c>dotnet watch</c>, its output going to the editor's log.
/// Saving a script while it runs hot-reloads the change into the running game; a change hot reload can't
/// apply (a new class, say) restarts it. dotnet watch needs the ASP.NET Core runtime; without it the game
/// runs with <c>dotnet run</c> and the editor restarts it after a script is saved.
/// </summary>
sealed class GameRunner : IDisposable
{
    Process? _process;

    /// <summary>Whether dotnet watch can run here (it needs the ASP.NET Core shared runtime).</summary>
    public static readonly bool HotReload = FindAspNetRuntime();

    static bool FindAspNetRuntime()
    {
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (root == null)
        {
            var dotnet = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':')
                .Select(d => Path.Combine(d, "dotnet")).FirstOrDefault(File.Exists);
            if (dotnet == null)
                return false;
            var target = File.ResolveLinkTarget(dotnet, returnFinalTarget: true)?.FullName ?? dotnet;
            root = Path.GetDirectoryName(target)!;
        }
        return Directory.Exists(Path.Combine(root, "shared", "Microsoft.AspNetCore.App"));
    }

    public bool Running => _process is { HasExited: false };

    /// <summary>Every output line, for the script checker to find errors in.</summary>
    public event Action<string>? Output;

    public void Play(EditorProject project)
    {
        if (Running)
            return;
        if (!File.Exists(project.GamePath))
        {
            Log.Error("play", $"no game project at {project.GamePath} (set \"game\" in trinity.json)");
            return;
        }
        // Debug: hot reload needs it, and it shares the build the script checker makes
        string[] command = HotReload
            ? ["watch", "--non-interactive", "--project", project.GamePath, "run", "-c", "Debug"]
            : ["run", "--project", project.GamePath, "-c", "Debug"];
        var start = new ProcessStartInfo("dotnet", command)
        {
            WorkingDirectory = project.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // The editor already showed the BRUTAL logo; don't repeat it on every run and restart
        start.Environment["TRINITY_SKIP_SPLASH"] = "1";
        start.Environment["DOTNET_WATCH_SUPPRESS_EMOJIS"] = "1";
        if (HotReload)
            Log.Info("play", $"building and starting {project.Name}; saved scripts apply while it runs (hot reload)");
        else
            Log.Info("play", $"building and starting {project.Name}; saving a script restarts it (for hot reload instead, install the ASP.NET Core runtime: Arch package aspnet-runtime)");
        _process = Process.Start(start);
        if (_process == null)
        {
            Log.Error("play", "couldn't start dotnet");
            return;
        }
        _process.EnableRaisingEvents = true;
        _process.OutputDataReceived += (_, e) => Line(e.Data);
        _process.ErrorDataReceived += (_, e) => Line(e.Data);
        _process.Exited += (_, _) => Log.Info("play", $"{project.Name} stopped");
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    void Line(string? data)
    {
        if (data is not { Length: > 0 } line)
            return;
        line = line.Trim();
        if (line.StartsWith("dotnet watch", StringComparison.Ordinal))
            Log.Info("play", line["dotnet watch".Length..].Trim());
        else if (line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries) is [_, "error", var text])
            Log.Error("game", text); // Trinity's log format: "  12.345 error category: message"
        else if (line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries) is [_, "warn", var warning])
            Log.Warn("game", warning);
        else
            Log.Info("game", line);
        Output?.Invoke(line);
    }

    public void Stop()
    {
        if (!Running)
            return;
        // The whole tree: dotnet watch/run start the build and the game as children
        _process!.Kill(entireProcessTree: true);
        _process.WaitForExit(3000);
    }

    /// <summary>Applies saved scripts without hot reload: stops the game and runs it again.</summary>
    public void Restart(EditorProject project)
    {
        Log.Info("play", "restarting the game with the saved scripts");
        Stop();
        Play(project);
    }

    public void Dispose()
    {
        Stop();
        _process?.Dispose();
    }
}
