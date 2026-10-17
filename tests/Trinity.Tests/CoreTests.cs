using Brutal.GlfwApi;
using System.Text.Json.Serialization;
using Xunit;

namespace Trinity.Tests;

public class BindingTests
{
    public static TheoryData<Binding, string> Cases => new()
    {
        { GlfwKey.W, "key:W" },
        { GlfwMouseButton.Number1, "mouse:Number1" },
        { GlfwGamepadButton.A, "pad:A" },
        { Binding.Axis(GlfwGamepadAxis.LeftX), "axis:LeftX+" },
        { Binding.Axis(GlfwGamepadAxis.LeftY, positive: false), "axis:LeftY-" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Writes_and_reads_back(Binding binding, string text)
    {
        Assert.Equal(text, binding.ToString());
        Assert.Equal(binding, Binding.TryParse(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("key")]
    [InlineData("key:NotAKey")]
    [InlineData("wheel:Up")]
    [InlineData("axis:")]
    [InlineData("pad:99")] // numbers parse as enums, and undefined ones would index past the gamepad state
    [InlineData("pad:3")]
    [InlineData("axis:9+")]
    [InlineData("axis:-1+")]
    [InlineData("key:65")]
    [InlineData("axis:LeftX*")]
    public void Rejects_nonsense(string text) => Assert.Null(Binding.TryParse(text));
}

public class InputTests
{
    static Input WithAttack()
    {
        var input = new Input();
        input.Bind("attack", GlfwMouseButton.Number1, GlfwKey.Space);
        input.BeginFrame();
        return input;
    }

    [Fact]
    public void A_tap_within_one_frame_is_pressed_then_released()
    {
        var input = WithAttack();
        input.Button(GlfwMouseButton.Number1, true); // a touchpad tap: both in one poll
        input.Button(GlfwMouseButton.Number1, false);
        input.BeginFrame();
        Assert.True(input.Pressed("attack"));
        Assert.True(input.Down("attack"));
        Assert.False(input.Released("attack"));
        input.BeginFrame();
        Assert.False(input.Pressed("attack"));
        Assert.False(input.Down("attack"));
        Assert.True(input.Released("attack"));
        input.BeginFrame();
        Assert.False(input.Released("attack"));
    }

    [Fact]
    public void A_held_key_is_pressed_once_down_while_held_and_released_once()
    {
        var input = WithAttack();
        input.Key(GlfwKey.Space, true);
        input.BeginFrame();
        Assert.True(input.Pressed("attack"));
        input.BeginFrame();
        Assert.False(input.Pressed("attack"));
        Assert.True(input.Down("attack"));
        input.Key(GlfwKey.Space, false);
        input.BeginFrame();
        Assert.True(input.Released("attack"));
        Assert.False(input.Down("attack"));
    }
}

public class SettingsTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "trinity-tests-" + Guid.NewGuid().ToString("N"));
    readonly string? _oldConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

    public SettingsTests() => Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _dir);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _oldConfig);
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Missing_section_gives_defaults()
    {
        var display = Settings.Load("game").Get("display", TrinityJson.Default.DisplaySettings);
        Assert.Equal(1600, display.Width);
        Assert.True(display.VSync);
    }

    [Fact]
    public void Saves_and_loads_sections()
    {
        var s = Settings.Load("game");
        s.Set("display", new DisplaySettings { Width = 2560, Height = 1440, Fullscreen = true }, TrinityJson.Default.DisplaySettings);
        s.Set("audio", new Dictionary<string, float> { ["master"] = 0.5f }, TestJson.Default.DictionaryStringSingle);
        s.Save();

        var again = Settings.Load("game");
        var display = again.Get("display", TrinityJson.Default.DisplaySettings);
        Assert.Equal(2560, display.Width);
        Assert.True(display.Fullscreen);
        Assert.Equal(0.5f, again.Get("audio", TestJson.Default.DictionaryStringSingle)["master"]);
    }

    [Fact]
    public void Broken_file_is_kept_aside_and_defaults_used()
    {
        var path = Path.Combine(_dir, "game", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");

        var display = Settings.Load("game").Get("display", TrinityJson.Default.DisplaySettings);
        Assert.Equal(1600, display.Width);
        Assert.True(File.Exists(path + ".broken"));
    }
}

public class JobsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(63)]
    [InlineData(1000)]
    [InlineData(100_003)]
    public void For_covers_every_index_once(int count)
    {
        var jobs = new Jobs(threads: 4);
        var hits = new int[count];
        jobs.For(count, (start, end, _) =>
        {
            for (var i = start; i < end; i++)
                Interlocked.Increment(ref hits[i]);
        });
        Assert.All(hits, h => Assert.Equal(1, h));
    }

    [Fact]
    public void Chunks_are_contiguous_and_in_order()
    {
        var jobs = new Jobs(threads: 4);
        var ranges = new (int Start, int End)[jobs.ChunkCount];
        jobs.For(10_000, (start, end, chunk) => ranges[chunk] = (start, end));
        var next = 0;
        foreach (var (start, end) in ranges.Where(r => r.End > r.Start))
        {
            Assert.Equal(next, start);
            next = end;
        }
        Assert.Equal(10_000, next);
    }
}

[JsonSerializable(typeof(Dictionary<string, float>))]
partial class TestJson : JsonSerializerContext;
