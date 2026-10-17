using System.Diagnostics;
using Brutal.GlfwApi;
using Brutal.VulkanApi;
using Trinity.Graphics;

namespace Trinity;

public sealed record EngineOptions
{
    public string Title { get; init; } = "Trinity";

    /// <summary>Folder name for the settings file (~/.config/&lt;AppName&gt;). Defaults to the title.</summary>
    public string? AppName { get; init; }

    /// <summary>Override the player's display settings (for tests and benchmarks); null uses them.</summary>
    public int? Width { get; init; }
    public int? Height { get; init; }
    public bool? VSync { get; init; }

    /// <summary>Steps per second for <see cref="Game.FixedUpdate"/>.</summary>
    public int FixedRate { get; init; } = 60;

    /// <summary>
    /// Show the BRUTAL logo before the game loads. The BRUTAL License requires it in anything shipped;
    /// turn it off only for development (or set TRINITY_SKIP_SPLASH=1).
    /// </summary>
    public bool ShowSplash { get; init; } = true;

    /// <summary>Quit after this many seconds (for tests and benchmarks).</summary>
    public double? QuitAfter { get; init; }

    /// <summary>Print the status line to the console as well as the title bar.</summary>
    public bool PrintStatus { get; init; }

    /// <summary>
    /// Record a video here instead of running interactively: renders off screen at
    /// <see cref="RecordWidth"/> x <see cref="RecordHeight"/>, steps time by exactly 1/<see cref="RecordFps"/>,
    /// and stops after <see cref="RecordSeconds"/>.
    /// </summary>
    public string? RecordPath { get; init; }
    public double RecordSeconds { get; init; } = 10;
    public int RecordFps { get; init; } = 60;
    public int RecordWidth { get; init; } = 1920;
    public int RecordHeight { get; init; } = 1080;

    /// <summary>Start the recording with the BRUTAL logo, as the game itself does.</summary>
    public bool RecordSplash { get; init; }
}

/// <summary>Owns the window, input, GPU, jobs, shaders and settings, and runs a <see cref="Game"/>.</summary>
public sealed class Engine : IDisposable
{
    public readonly EngineOptions Options;
    public readonly Settings Settings;
    public readonly Window Window;
    public readonly Input Input;
    public readonly GraphicsDevice Gpu;
    public readonly Jobs Jobs;
    public readonly ShaderLibrary Shaders;

    public bool Recording => Options.RecordPath != null;
    public double Fps { get; private set; }
    public double FrameMs { get; private set; }

    Engine(EngineOptions options)
    {
        Options = options;
        var appName = options.AppName ?? Slug(options.Title);
        Settings = Settings.Load(appName);
        var display = Settings.Get("display", TrinityJson.Default.DisplaySettings);

        Window = new Window(options.Title, options.Width ?? display.Width, options.Height ?? display.Height, appName);
        if (display.Fullscreen && !Recording && options.Width == null)
            Window.Fullscreen = true;
        Input = new Input(Window);
        Input.LoadBindings(Settings);
        Input.Bind("engine.fullscreen", GlfwKey.F11);

        Gpu = new GraphicsDevice(
            Window,
            options.VSync ?? display.VSync,
            Recording ? new VkExtent2D { Width = options.RecordWidth, Height = options.RecordHeight } : null
        );
        Jobs = new Jobs();
        Shaders = new ShaderLibrary(Gpu);
        Log.Info("engine", $"{Gpu.GpuName}, {Jobs.ThreadCount} job threads, settings in {Settings.FilePath}");
    }

    static string Slug(string title) =>
        string.Concat(title.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-')).Trim('-');

    /// <summary>Runs <paramref name="game"/> until its window closes (or the recording or time limit ends).</summary>
    public static void Run(Game game, EngineOptions options)
    {
        using var engine = new Engine(options);
        game.Engine = engine;
        using var recorder = engine.Recording ? new FrameRecorder(engine.Gpu, options.RecordPath!, options.RecordFps) : null;
        try
        {
            var splash = engine.Recording
                ? options.RecordSplash
                : options.ShowSplash && Environment.GetEnvironmentVariable("TRINITY_SKIP_SPLASH") != "1";
            if (splash)
                Splash.Show(engine.Window, engine.Gpu, engine.Shaders, recorder, options.RecordFps);
            if (engine.Window.ShouldClose)
                return;
            if (engine.Input.Gamepad is { } pad)
                Log.Info("input", $"gamepad: {pad}");
            game.Load();
            engine.Loop(game, recorder);
            engine.SaveSettings();
        }
        finally
        {
            engine.Gpu.WaitIdle();
            game.Dispose();
        }
    }

    void Loop(Game game, FrameRecorder? recorder)
    {
        var recordFrames = (int)(Options.RecordSeconds * Options.RecordFps) + (recorder?.Frames ?? 0);
        var fixedStep = 1.0 / Options.FixedRate;
        var clock = Stopwatch.StartNew();
        var last = 0.0;
        double gameTime = 0, fixedTime = 0, accumulator = 0, statusTimer = 0, msSum = 0;
        long frame = 0, fixedFrame = 0;
        var statusFrames = 0;

        while (!Window.ShouldClose)
        {
            Window.PollEvents();
            Input.BeginFrame();
            var now = clock.Elapsed.TotalSeconds;
            var real = (float)(now - last);
            last = now;
            if (Options.QuitAfter is { } q && now >= q)
                break;
            if (recorder != null && recorder.Frames >= recordFrames)
                break;
            if (Input.Pressed("engine.fullscreen") && !Recording)
                Window.Fullscreen = !Window.Fullscreen;

            if (!Gpu.BeginFrame(out var cmd))
                continue;

            var delta = recorder != null ? 1.0 / Options.RecordFps : Math.Min(real, 0.1);
            gameTime += delta;

            // Fixed steps to catch up with real time, at most 5 a frame so a stall can't snowball
            accumulator += delta;
            var steps = 0;
            while (accumulator >= fixedStep && steps < 5)
            {
                fixedTime += fixedStep;
                game.FixedUpdate(new FrameTime((float)fixedStep, fixedTime, fixedFrame++, real));
                accumulator -= fixedStep;
                steps++;
            }
            if (steps == 5)
                accumulator = Math.Min(accumulator, fixedStep);

            var time = new FrameTime((float)delta, gameTime, frame++, real, (float)(accumulator / fixedStep));
            game.Update(time);
            game.RenderFrame(cmd, time);
            Gpu.EndFrame(cmd, recorder?.Buffer);
            recorder?.Capture();

            statusFrames++;
            msSum += real * 1000;
            statusTimer += real;
            if (statusTimer >= 0.5)
            {
                Fps = statusFrames / statusTimer;
                FrameMs = msSum / statusFrames;
                var status = game.Status;
                var line = $"{Options.Title} | {Fps:F0} fps, {FrameMs:F2} ms"
                    + (status.Length > 0 ? " | " + status : "")
                    + (recorder != null ? $" | recording {recorder.Frames}/{recordFrames}" : "");
                Window.Title = line;
                if (Options.PrintStatus)
                    Console.WriteLine(line);
                statusTimer = msSum = 0;
                statusFrames = 0;
            }
        }
    }

    /// <summary>Remembers the window and controls for next time. Not after recordings or overridden sizes.</summary>
    void SaveSettings()
    {
        if (Recording || Options.Width != null || Options.VSync != null)
            return;
        var display = Settings.Get("display", TrinityJson.Default.DisplaySettings);
        display.Fullscreen = Window.Fullscreen;
        if (!Window.Fullscreen)
            (display.Width, display.Height) = (Window.Size.X, Window.Size.Y);
        display.VSync = Gpu.VSync;
        Settings.Set("display", display, TrinityJson.Default.DisplaySettings);
        Input.SaveBindings(Settings);
        Settings.Save();
    }

    public void Dispose()
    {
        Gpu.WaitIdle();
        Gpu.Dispose();
        Window.Dispose();
    }
}
