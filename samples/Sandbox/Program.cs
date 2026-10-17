using Trinity;
using Trinity.Scenes;

// The scripting sandbox: a scene made in Trinity Editor, brought to life by the C# scripts in Scripts/.
// Right-drag orbits the camera, the wheel zooms, Esc quits.
//
//   dotnet run --project samples/Sandbox -- [--seconds 10] [--record out.mp4 --record-seconds 5]

var options = new EngineOptions { Title = "Trinity sandbox", AppName = "trinity-sandbox" };
for (var a = 0; a < args.Length; a++)
    options = args[a] switch
    {
        "--seconds" => options with { QuitAfter = double.Parse(args[++a]) },
        "--record" => options with { RecordPath = args[++a] },
        "--record-seconds" => options with { RecordSeconds = double.Parse(args[++a]) },
        _ => throw new ArgumentException($"Unknown option {args[a]}"),
    };
Engine.Run(new SceneGame("Scenes/main.scene.json"), options);
