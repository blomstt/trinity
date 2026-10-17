using Trinity;
using Trinity.Scenes;

// The scripting sandbox: a scene made in Trinity Editor, brought to life by the C# scripts in Scripts/.
// Right-drag orbits the camera, the wheel zooms, Esc quits.
//
//   dotnet run --project samples/Sandbox -- [--scene Scenes/materials.scene.json] [--view albedo|normals|cascades]
//       [--seconds 10] [--no-vsync] [--stats] [--record out.mp4 --record-seconds 5]

var options = new EngineOptions { Title = "Trinity sandbox", AppName = "trinity-sandbox" };
var scene = "Scenes/main.scene.json";
var view = Trinity.Rendering.DebugView.None;
for (var a = 0; a < args.Length; a++)
    options = args[a] switch
    {
        "--scene" => options with { Title = $"Trinity sandbox - {scene = args[++a]}" },
        "--view" => options with { Title = $"Trinity sandbox ({view = Enum.Parse<Trinity.Rendering.DebugView>(args[++a], ignoreCase: true)})" },
        "--seconds" => options with { QuitAfter = double.Parse(args[++a]) },
        "--no-vsync" => options with { VSync = false },
        "--stats" => options with { PrintStatus = true },
        "--record" => options with { RecordPath = args[++a] },
        "--record-seconds" => options with { RecordSeconds = double.Parse(args[++a]) },
        _ => throw new ArgumentException($"Unknown option {args[a]}"),
    };
Engine.Run(new SceneGame(scene) { DebugView = view }, options);
