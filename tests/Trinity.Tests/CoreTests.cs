using Brutal.GlfwApi;
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
    public void Rejects_nonsense(string text) => Assert.Null(Binding.TryParse(text));
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
        var display = Settings.Load("game").Get<DisplaySettings>("display");
        Assert.Equal(1600, display.Width);
        Assert.True(display.VSync);
    }

    [Fact]
    public void Saves_and_loads_sections()
    {
        var s = Settings.Load("game");
        s.Set("display", new DisplaySettings { Width = 2560, Height = 1440, Fullscreen = true });
        s.Set("audio", new Dictionary<string, float> { ["master"] = 0.5f });
        s.Save();

        var again = Settings.Load("game");
        var display = again.Get<DisplaySettings>("display");
        Assert.Equal(2560, display.Width);
        Assert.True(display.Fullscreen);
        Assert.Equal(0.5f, again.Get<Dictionary<string, float>>("audio")["master"]);
    }

    [Fact]
    public void Broken_file_is_kept_aside_and_defaults_used()
    {
        var path = Path.Combine(_dir, "game", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");

        var display = Settings.Load("game").Get<DisplaySettings>("display");
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
