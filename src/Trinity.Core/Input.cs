using Brutal.GlfwApi;
using Brutal.Numerics;

namespace Trinity;

/// <summary>
/// Keyboard, mouse, gamepad and text input for one window, sampled once per frame.
///
/// Games read named actions rather than raw devices: <see cref="Bind"/> sets an action's default
/// bindings, then <see cref="Down"/>, <see cref="Pressed"/>, <see cref="Value"/> and <see cref="Axis"/>
/// read it whatever is bound. Players' rebinds are saved to and loaded from <see cref="Settings"/>.
/// The first connected gamepad is used, with SDL-style mappings built into GLFW.
/// </summary>
public sealed class Input
{
    public const float DeadZone = 0.2f;

    readonly HashSet<GlfwKey> _keys = [], _keysPressed = [], _keysReleased = [];
    readonly HashSet<GlfwMouseButton> _buttons = [], _buttonsPressed = [], _buttonsReleased = [];
    readonly HashSet<GlfwKey> _pressedFrame = [], _releasedFrame = [];
    readonly HashSet<GlfwMouseButton> _buttonsPressedFrame = [], _buttonsReleasedFrame = [];
    // What actions see: held at this frame (a tap pressed and released between two frames counts as held
    // for the frame it happened in) and at the last one, so a tap gives Pressed, then Released next frame
    HashSet<GlfwKey> _keysNow = [], _keysLast = [];
    HashSet<GlfwMouseButton> _buttonsNow = [], _buttonsLast = [];
    readonly bool _pollGamepads = true;
    readonly List<uint> _text = [], _textFrame = [];
    readonly Dictionary<string, Binding[]> _defaults = [];
    readonly Dictionary<string, Binding[]> _actions = [];
    double2 _mouse, _lastMouse, _scroll;

    // Gamepad state this frame and last frame
    readonly bool[] _pad = new bool[Binding.PadButtons], _padLast = new bool[Binding.PadButtons];
    readonly float[] _axes = new float[Binding.PadAxes], _axesLast = new float[Binding.PadAxes];

    public Input(Window window)
    {
        var w = window.Native;
        _mouse = _lastMouse = w.CursorPos;
        w.OnKey += (_, key, _, action, _) =>
        {
            if (action is GlfwKeyAction.Press or GlfwKeyAction.Release)
                Key(key, action == GlfwKeyAction.Press);
        };
        w.OnMouseButton += (_, button, action, _) => Button(button, action == GlfwButtonAction.Press);
        w.OnScroll += (_, offset) => _scroll += offset;
        w.OnCursorPos += (_, pos) => _mouse = pos;
        w.OnChar += (_, codepoint) => _text.Add(codepoint);
        // Keys released while another window has focus never reach this one: let go of everything
        // when focus goes, or a key held during alt-tab would stay down
        w.OnFocus += (_, focus) =>
        {
            if (focus == GlfwFocusEvent.LostFocus)
                ReleaseAll();
        };
    }

    /// <summary>Releases every held key and mouse button (as if each was let go), for when the window loses focus.</summary>
    internal void ReleaseAll()
    {
        foreach (var key in _keys.ToArray())
            Key(key, false);
        foreach (var button in _buttons.ToArray())
            Button(button, false);
    }

    /// <summary>Input with no window, fed by <see cref="Key"/> and <see cref="Button"/> (for tests).</summary>
    internal Input() => _pollGamepads = false;

    internal void Key(GlfwKey key, bool down)
    {
        if (down && _keys.Add(key))
            _keysPressed.Add(key);
        else if (!down && _keys.Remove(key))
            _keysReleased.Add(key);
    }

    internal void Button(GlfwMouseButton button, bool down)
    {
        if (down && _buttons.Add(button))
            _buttonsPressed.Add(button);
        else if (!down && _buttons.Remove(button))
            _buttonsReleased.Add(button);
    }

    /// <summary>Mouse position in window pixels.</summary>
    public double2 Mouse => _mouse;

    /// <summary>Mouse movement since the previous frame.</summary>
    public double2 MouseDelta { get; private set; }

    /// <summary>Wheel movement this frame (Y is the usual wheel).</summary>
    public double2 Scroll { get; private set; }

    /// <summary>Characters typed this frame, for text fields.</summary>
    public IReadOnlyList<uint> Text => _textFrame;

    /// <summary>Name of the gamepad in use, or null if none is connected.</summary>
    public string? Gamepad { get; private set; }

    /// <summary>True on a frame where any key, mouse button or gamepad button went down.</summary>
    public bool AnyPressed => _pressedFrame.Count > 0 || _buttonsPressedFrame.Count > 0 || PadPressedAny();

    /// <summary>Called by the engine after polling events: makes this frame's input visible to the game.</summary>
    public void BeginFrame()
    {
        Swap(_keysPressed, _pressedFrame);
        Swap(_keysReleased, _releasedFrame);
        Swap(_buttonsPressed, _buttonsPressedFrame);
        Swap(_buttonsReleased, _buttonsReleasedFrame);
        (_keysLast, _keysNow) = (_keysNow, _keysLast);
        _keysNow.Clear();
        _keysNow.UnionWith(_keys);
        _keysNow.UnionWith(_pressedFrame);
        (_buttonsLast, _buttonsNow) = (_buttonsNow, _buttonsLast);
        _buttonsNow.Clear();
        _buttonsNow.UnionWith(_buttons);
        _buttonsNow.UnionWith(_buttonsPressedFrame);
        _textFrame.Clear();
        _textFrame.AddRange(_text);
        _text.Clear();
        Scroll = _scroll;
        _scroll = default;
        MouseDelta = new double2(_mouse.X - _lastMouse.X, _mouse.Y - _lastMouse.Y);
        _lastMouse = _mouse;
        PollGamepad();
    }

    static void Swap<T>(HashSet<T> incoming, HashSet<T> frame)
    {
        frame.Clear();
        frame.UnionWith(incoming);
        incoming.Clear();
    }

    unsafe void PollGamepad()
    {
        Array.Copy(_pad, _padLast, _pad.Length);
        Array.Copy(_axes, _axesLast, _axes.Length);
        if (!_pollGamepads)
            return;
        for (var id = 0; id < 16; id++)
        {
            if (!Glfw.JoystickIsGamepad(id) || !Glfw.GetGamepadState(id, out var state))
                continue;
            Gamepad = Glfw.GetGamepadName(id) ?? "Gamepad";
            for (var b = 0; b < _pad.Length; b++)
                _pad[b] = state.Buttons[b] != 0;
            for (var a = 0; a < _axes.Length; a++)
                _axes[a] = state.Axes[a];
            return;
        }
        Gamepad = null;
        Array.Clear(_pad);
        Array.Clear(_axes);
        // Triggers rest at -1 in GLFW
        _axes[(int)GlfwGamepadAxis.LeftTrigger] = _axes[(int)GlfwGamepadAxis.RightTrigger] = -1;
    }

    bool PadPressedAny()
    {
        for (var b = 0; b < _pad.Length; b++)
            if (_pad[b] && !_padLast[b])
                return true;
        return false;
    }

    // --- Raw devices --------------------------------------------------------------------------------

    public bool Down(GlfwKey key) => _keys.Contains(key);

    public bool Pressed(GlfwKey key) => _pressedFrame.Contains(key);

    public bool Released(GlfwKey key) => _releasedFrame.Contains(key);

    public bool Down(GlfwMouseButton button) => _buttons.Contains(button);

    public bool Pressed(GlfwMouseButton button) => _buttonsPressedFrame.Contains(button);

    public bool Released(GlfwMouseButton button) => _buttonsReleasedFrame.Contains(button);

    /// <summary>A stick as a vector in -1..1 with the dead zone removed (Y down, as GLFW reports it).</summary>
    public float2 Stick(GlfwGamepadAxis x, GlfwGamepadAxis y)
    {
        var v = new float2(_axes[(int)x], _axes[(int)y]);
        var length = MathF.Sqrt(v.X * v.X + v.Y * v.Y);
        if (length < DeadZone)
            return default;
        var scaled = MathF.Min(1, (length - DeadZone) / (1 - DeadZone));
        return new float2(v.X / length * scaled, v.Y / length * scaled);
    }

    // --- Actions ------------------------------------------------------------------------------------

    /// <summary>
    /// Sets an action's default bindings. A player's saved rebind (from <see cref="LoadBindings"/>)
    /// overrides these.
    /// </summary>
    public void Bind(string action, params Binding[] bindings)
    {
        _defaults[action] = bindings;
        if (!_overridden.Contains(action))
            _actions[action] = bindings;
    }

    readonly HashSet<string> _overridden = [];

    /// <summary>Replaces what an action is bound to, for the in-game controls menu.</summary>
    public void Rebind(string action, params Binding[] bindings)
    {
        _actions[action] = bindings;
        _overridden.Add(action);
    }

    /// <summary>Puts an action back to the game's default bindings.</summary>
    public void ResetBinding(string action)
    {
        _overridden.Remove(action);
        if (_defaults.TryGetValue(action, out var d))
            _actions[action] = d;
    }

    public IReadOnlyList<Binding> Bindings(string action) => Lookup(action);

    public IEnumerable<string> Actions => _actions.Keys;

    /// <summary>How strongly the action is held, 0..1 (sticks and triggers give values in between).</summary>
    public float Value(string action)
    {
        var v = 0f;
        foreach (var b in Lookup(action))
            v = MathF.Max(v, Current(b));
        return v;
    }

    public bool Down(string action) => Value(action) >= 0.5f;

    public bool Pressed(string action)
    {
        foreach (var b in Lookup(action))
            if (Current(b) >= 0.5f && Previous(b) < 0.5f)
                return true;
        return false;
    }

    public bool Released(string action)
    {
        foreach (var b in Lookup(action))
            if (Current(b) < 0.5f && Previous(b) >= 0.5f)
                return true;
        return false;
    }

    /// <summary>-1..1 from a pair of opposing actions, such as "left" and "right".</summary>
    public float Axis(string negative, string positive) => Value(positive) - Value(negative);

    Binding[] Lookup(string action) =>
        _actions.TryGetValue(action, out var b) ? b : throw new KeyNotFoundException($"No input action named '{action}'. Bind it first.");

    float Current(Binding b) => b.Kind switch
    {
        BindingKind.Key => _keysNow.Contains((GlfwKey)b.Code) ? 1 : 0,
        BindingKind.Mouse => _buttonsNow.Contains((GlfwMouseButton)b.Code) ? 1 : 0,
        BindingKind.Pad => _pad[b.Code] ? 1 : 0,
        _ => AxisValue(_axes, b),
    };

    float Previous(Binding b) => b.Kind switch
    {
        BindingKind.Key => _keysLast.Contains((GlfwKey)b.Code) ? 1 : 0,
        BindingKind.Mouse => _buttonsLast.Contains((GlfwMouseButton)b.Code) ? 1 : 0,
        BindingKind.Pad => _padLast[b.Code] ? 1 : 0,
        _ => AxisValue(_axesLast, b),
    };

    static float AxisValue(float[] axes, Binding b)
    {
        var raw = axes[b.Code];
        // Triggers run -1 (released) to 1 (pulled); sticks run -1 to 1 around 0
        var v = b.Code is (int)GlfwGamepadAxis.LeftTrigger or (int)GlfwGamepadAxis.RightTrigger ? (raw + 1) / 2 : raw * b.Sign;
        return v < DeadZone ? 0 : MathF.Min(1, (v - DeadZone) / (1 - DeadZone));
    }

    // --- Saving rebinds -----------------------------------------------------------------------------

    /// <summary>Applies the player's saved rebinds from the "bindings" settings section.</summary>
    public void LoadBindings(Settings settings)
    {
        foreach (var (action, texts) in settings.Get("bindings", TrinityJson.Default.DictionaryStringStringArray))
        {
            var parsed = texts.Select(Binding.TryParse).Where(b => b != null).Select(b => b!.Value).ToArray();
            if (parsed.Length > 0)
                Rebind(action, parsed);
        }
    }

    /// <summary>Stores rebound actions (only those that differ from the defaults) in the settings.</summary>
    public void SaveBindings(Settings settings) =>
        settings.Set(
            "bindings",
            _overridden.ToDictionary(a => a, a => _actions[a].Select(b => b.ToString()).ToArray()),
            TrinityJson.Default.DictionaryStringStringArray
        );
}

public enum BindingKind
{
    Key,
    Mouse,
    Pad,
    PadAxis,
}

/// <summary>
/// One key, mouse button, gamepad button or gamepad axis direction an action can be bound to.
/// Written as text for settings: <c>key:W</c>, <c>mouse:Number1</c>, <c>pad:A</c>, <c>axis:LeftX+</c>.
/// </summary>
public readonly record struct Binding(BindingKind Kind, int Code, float Sign = 1)
{
    public static implicit operator Binding(GlfwKey key) => new(BindingKind.Key, (int)key);

    public static implicit operator Binding(GlfwMouseButton button) => new(BindingKind.Mouse, (int)button);

    public static implicit operator Binding(GlfwGamepadButton button) => new(BindingKind.Pad, (int)button);

    /// <summary>A stick pushed one way (<paramref name="positive"/> = right or down), or a trigger.</summary>
    public static Binding Axis(GlfwGamepadAxis axis, bool positive = true) => new(BindingKind.PadAxis, (int)axis, positive ? 1 : -1);

    public override string ToString() => Kind switch
    {
        BindingKind.Key => $"key:{(GlfwKey)Code}",
        BindingKind.Mouse => $"mouse:{(GlfwMouseButton)Code}",
        BindingKind.Pad => $"pad:{(GlfwGamepadButton)Code}",
        _ => $"axis:{(GlfwGamepadAxis)Code}{(Sign < 0 ? "-" : "+")}",
    };

    public static Binding? TryParse(string text)
    {
        var colon = text.IndexOf(':');
        if (colon < 0)
            return null;
        var kind = text[..colon];
        var name = text[(colon + 1)..];
        // Names only: Enum.TryParse also takes numbers, and an undefined one ("pad:99") would index past
        // the gamepad state when the action is read
        return kind switch
        {
            "key" when Parse<GlfwKey>(name, int.MaxValue) is { } k => k,
            "mouse" when Parse<GlfwMouseButton>(name, int.MaxValue) is { } m => m,
            "pad" when Parse<GlfwGamepadButton>(name, PadButtons) is { } p => p,
            "axis" when name.Length > 1 && name[^1] is '+' or '-' && Parse<GlfwGamepadAxis>(name[..^1], PadAxes) is { } a => Axis(a, name[^1] == '+'),
            _ => null,
        };
    }

    internal const int PadButtons = 15, PadAxes = 6;

    static T? Parse<T>(string name, int count) where T : struct, Enum =>
        name.Length > 0 && !char.IsAsciiDigit(name[0]) && name[0] != '-' && Enum.TryParse<T>(name, out var v)
            && Enum.IsDefined(v) && Convert.ToInt32(v) is var code && code >= 0 && code < count
            ? v
            : null;
}
