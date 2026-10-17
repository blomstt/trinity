using System.Diagnostics;

namespace Trinity.Editor;

/// <summary>Builds and runs the project's game as its own process, its output going to the editor's log.</summary>
sealed class GameRunner : IDisposable
{
    Process? _process;

    public bool Running => _process is { HasExited: false };

    public void Play(EditorProject project)
    {
        if (Running)
            return;
        if (!File.Exists(project.GamePath))
        {
            Log.Error("play", $"no game project at {project.GamePath} (set \"game\" in trinity.json)");
            return;
        }
        var start = new ProcessStartInfo("dotnet")
        {
            ArgumentList = { "run", "-c", "Release", "--project", project.GamePath },
            WorkingDirectory = project.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        Log.Info("play", $"building and starting {project.Name}");
        _process = Process.Start(start);
        if (_process == null)
        {
            Log.Error("play", "couldn't start dotnet");
            return;
        }
        _process.EnableRaisingEvents = true;
        _process.OutputDataReceived += (_, e) => { if (e.Data is { Length: > 0 } line) Log.Info("game", line.Trim()); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } line) Log.Info("game", line.Trim()); };
        _process.Exited += (_, _) => Log.Info("play", $"{project.Name} exited ({_process?.ExitCode})");
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    public void Stop()
    {
        if (!Running)
            return;
        // The whole tree: dotnet run starts the game as a child
        _process!.Kill(entireProcessTree: true);
        Log.Info("play", "stopped");
    }

    public void Dispose()
    {
        Stop();
        _process?.Dispose();
    }
}
