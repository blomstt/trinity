using Brutal.GlfwApi;
using Brutal.Numerics;

namespace Trinity;

/// <summary>The game window. Owns GLFW for the lifetime of the engine; there's one window.</summary>
public sealed class Window : IDisposable
{
    public readonly GlfwWindow Native;

    /// <param name="appId">
    /// Identifies the app to the desktop (Wayland app ID, X11 class): matches the .desktop file's
    /// StartupWMClass and lets window managers apply rules and icons.
    /// </param>
    public Window(string title, int width, int height, string? appId = null)
    {
        // GLFW otherwise loads GTK (libdecor) to draw title bars on Wayland. Only GNOME needs that;
        // Hyprland, Sway and KDE draw their own or none
        if (!(Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").Contains("GNOME", StringComparison.OrdinalIgnoreCase))
            Glfw.PInvoke.InitHint((int)GlfwInitHint.WaylandLibdecor, 0x00038002); // GLFW_WAYLAND_DISABLE_LIBDECOR
        if (!Glfw.Init())
            throw new Exception("GLFW failed to initialise");
        Glfw.WindowHint(GlfwWindowHint.ClientApi, 0); // Vulkan, no OpenGL context
        if (appId != null)
        {
            Glfw.WindowHint((GlfwWindowHint)0x00026001, appId); // GLFW_WAYLAND_APP_ID, not in BRUTAL's enum yet
            Glfw.WindowHint(GlfwWindowHint.X11ClassName, appId);
        }
        Native = Glfw.CreateWindow(new GlfwWindow.CreateInfo { Title = title, Size = new int2(width, height) });
    }

    public string Title
    {
        get => Native.Title;
        set => Native.Title = value;
    }

    public bool ShouldClose
    {
        get => Native.ShouldClose;
        set => Native.ShouldClose = value;
    }

    /// <summary>Window size in screen units while windowed.</summary>
    public int2 Size => Native.Size;

    int2 _windowedPos, _windowedSize;

    /// <summary>Fullscreen on the primary monitor at its current resolution, or back to the previous window.</summary>
    public bool Fullscreen
    {
        get => Native.IsFullscreen;
        set
        {
            if (value == Native.IsFullscreen)
                return;
            if (value)
            {
                _windowedPos = Native.Position;
                _windowedSize = Native.Size;
                var monitor = Glfw.PrimaryMonitor;
                var mode = monitor.VideoMode;
                Native.SetFullscreen(monitor, new int2(mode.Width, mode.Height), mode.RefreshRate);
            }
            else
            {
                Native.SetWindowed(_windowedPos, _windowedSize.X > 0 ? _windowedSize : new int2(1600, 900));
            }
        }
    }

    /// <summary>Drawable size in pixels (can differ from the window size on scaled displays).</summary>
    public int2 FramebufferSize => Native.FramebufferSize;

    public static void PollEvents() => Glfw.PollEvents();

    public static void WaitEvents() => Glfw.WaitEvents();

    /// <summary>Sleeps until input or another window event arrives, or the timeout passes.</summary>
    public static void WaitEvents(double timeoutSeconds) => Glfw.WaitEventsTimeout(timeoutSeconds);

    /// <summary>Wakes a thread blocked in <see cref="WaitEvents()"/>, from any thread.</summary>
    public static void Wake() => Glfw.PostEmptyEvent();

    /// <summary>Raised on any input or resize, for apps that only redraw when something happens.</summary>
    public event Action? Activity
    {
        add
        {
            _activity += value;
            if (_hooked)
                return;
            _hooked = true;
            Native.OnCursorPos += (_, _) => _activity?.Invoke();
            Native.OnMouseButton += (_, _, _, _) => _activity?.Invoke();
            Native.OnScroll += (_, _) => _activity?.Invoke();
            Native.OnKey += (_, _, _, _, _) => _activity?.Invoke();
            Native.OnChar += (_, _) => _activity?.Invoke();
            Native.OnFramebufferSize += (_, _) => _activity?.Invoke();
        }
        remove => _activity -= value;
    }

    Action? _activity;
    bool _hooked;

    public void Dispose()
    {
        Native.Dispose();
        Glfw.Terminate();
    }
}
