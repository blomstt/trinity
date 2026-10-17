using BattleSample;
using Trinity;

// Two armies of spearmen on Trinity. Controls: WASD pan, Q/E turn, mouse wheel or Z/X zoom, R/F tilt,
// Space pause, Enter restart, +/- army size, J job system on/off, F11 fullscreen, Esc quit.
// Gamepad: left stick pan, right stick turn and tilt, triggers zoom, A pause, Start restart,
// bumpers army size, Y job system, Back quit.
//
//   dotnet run -c Release --project samples/Battle -- [--per-side 2000] [--gap 90] [--single] [--view wide]
//       [--vsync | --no-vsync] [--seconds 20] [--stats] [--no-splash] [--record out.mp4 --record-seconds 10 --warmup 8]

var battle = new BattleOptions();
var engine = new EngineOptions { Title = "Trinity battle", AppName = "trinity-battle" };
for (var a = 0; a < args.Length; a++)
    switch (args[a])
    {
        case "--per-side": battle = battle with { PerSide = int.Parse(args[++a]) }; break;
        case "--gap": battle = battle with { Gap = float.Parse(args[++a]) }; break;
        case "--single": battle = battle with { SingleThreaded = true }; break;
        case "--view": battle = battle with { WideView = args[++a] == "wide" }; break;
        case "--warmup": battle = battle with { Warmup = float.Parse(args[++a]) }; break;
        case "--vsync": engine = engine with { VSync = true }; break;
        case "--no-vsync": engine = engine with { VSync = false }; break;
        case "--no-splash": engine = engine with { ShowSplash = false }; break;
        case "--seconds": engine = engine with { QuitAfter = double.Parse(args[++a]) }; break;
        case "--stats": engine = engine with { PrintStatus = true }; break;
        case "--record": engine = engine with { RecordPath = args[++a] }; break;
        case "--record-seconds": engine = engine with { RecordSeconds = double.Parse(args[++a]) }; break;
        case "--record-splash": engine = engine with { RecordSplash = true }; break;
        default: Console.Error.WriteLine($"Unknown option {args[a]}"); return 1;
    }

Engine.Run(new BattleGame(battle), engine);
return 0;

namespace BattleSample
{
    public sealed record BattleOptions
    {
        public int PerSide { get; init; } = 2000;
        public float Gap { get; init; } = 90f;
        public bool SingleThreaded { get; init; }
        public bool WideView { get; init; }
        public float Warmup { get; init; }
    }
}
