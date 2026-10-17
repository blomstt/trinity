# Trinity

The game engine for **Varendal: Age of Kings**, built on [BRUTAL](https://github.com/ahwootech/BRUTAL).

BRUTAL provides the foundation: C# access to native libraries, maths, unmanaged memory, jobs, and bindings for Vulkan, GLFW, Jolt, FMOD/OpenAL, FreeType and Dear ImGui. Trinity is everything a game needs above that: a renderer, scenes, animation, terrain, physics integration, navigation, a game UI system, audio, data and saves, and editor tools.

Trinity is built for what Varendal needs, not as a general-purpose engine. The game lives in its own repository and uses Trinity as a library.

**Lightweight first, the BRUTAL way:** nothing ships or runs that the game doesn't use. Games and the editor publish as single native executables (NativeAOT), with no .NET runtime to install. Shaders are compiled to SPIR-V at build time, so no shader compiler ships. Tools only redraw when something changes. Every dependency has to earn its place.

| Measured (17 Oct 2026), native build | Trinity Editor | Battle sample |
|---|---|---|
| On disk, everything that ships | 8.3 MB (5.1 MB executable) | 6.2 MB (4.5 MB executable) + 2.2 MB assets |
| Start to ready | 0.1 s (JIT: 0.3 s) | 0.1 s (JIT: 0.25 s) |
| Memory | 112 MB with an empty project (JIT: 162 MB); most of it is the Vulkan driver | 207 MB peak with 4,000 soldiers (JIT: 241 MB) |
| Threads / CPU when idle | 7 / 0% (JIT: 12) | — |
| 64,000 soldiers, no vsync | — | about 730 fps (JIT: about 755 fps) |

## Status

Early. Roadmap: [docs/ROADMAP.md](docs/ROADMAP.md). Decisions: [docs/adr/](docs/adr/).

| Module | What it is | State |
|---|---|---|
| `Trinity.Core` | Window (with fullscreen), input actions for keyboard, mouse and gamepad, player settings, `Jobs.For`, logging, required credits | E1 done |
| `Trinity.Graphics` | Vulkan device and frame loop, GPU buffers, textures, SPIR-V shader loading, pipelines, cameras and frustums, video recording | First version |
| `Trinity.Animation` | glTF skinned models (its own small `.glb`/`.gltf` reader), animation baked for GPU skinning | First version, from the battle prototype |
| `Trinity` | `Engine.Run(game)`: the BRUTAL splash, then a loop of `FixedUpdate` (60 Hz) / `Update` / `Render`, settings saved on exit | E1 done |
| `Trinity.Editor` | **Trinity Editor**: viewport, hierarchy, inspector, assets, log, stats, ▶ Play | First version |
| `samples/Battle` | Two armies fighting: the BRUTAL battle prototype rebuilt on Trinity. 64,000 skinned soldiers at about 190 fps | Runs |
| `tests/Trinity.Tests` | Unit tests for the core | 19 passing |

## Shaders

GLSL files in a project's `Shaders/` folder (`.vert`, `.frag`, `.comp`) are compiled by `glslc` (Arch: `shaderc`) during `dotnet build` and shipped as `Shaders/<name>.spv`. `#include "skinning.glsl"` and other includes resolve from the project's `Shaders/` folder and `Trinity.Animation/Shaders/`. At runtime `ShaderLibrary.Load("crowd.vert")` just reads the `.spv`. After editing a shader, rebuild.

## Build and run

Needs the .NET 10 SDK, a Vulkan 1.3 GPU, and `glslc` for shaders (Arch: `shaderc`). BRUTAL comes from nuget.org as the official `Ahwoo.Brutal.*` packages. Linux only for now.

```bash
dotnet run -c Release --project samples/Battle
dotnet test tests/Trinity.Tests
```

`dotnet build` and `dotnet run` use the JIT, for quick iteration. To ship, publish native (needs `clang`):

```bash
dotnet publish samples/Battle -c Release -r linux-x64 -o artifacts/battle
```

The folder holds the executable, the few native libraries it loads, `Shaders/`, assets, `NOTICE.txt` and `licenses/`. Nothing else is needed on the player's machine besides a Vulkan driver. Every Trinity project is checked for NativeAOT at build time (`IsAotCompatible`), so reflection-based code is flagged as a warning while you write it. JSON goes through source-generated contexts (`[JsonSerializable]`), never reflection.

## Trinity Editor

```bash
./trinity-editor samples/Battle      # or any folder with a trinity.json; with no argument it reopens the last project
dotnet publish src/Trinity.Editor -c Release -r linux-x64 -o artifacts/editor   # the native editor, which ./trinity-editor then prefers
# It's also in the app launcher (rofi) as "Trinity Editor", via ~/.local/share/applications/trinity-editor.desktop
```

A project is a folder with a `trinity.json`:

```json
{ "name": "Varendal", "game": "src/Varendal/Varendal.csproj", "assets": "Assets", "scenes": "Scenes" }
```

| Panel | What it does |
|---|---|
| Viewport | The scene, live: right-drag to orbit, middle-drag (or Shift + right-drag) to pan, wheel to zoom, **F** to frame the selection |
| Hierarchy | Everything placed. Click to select, double-click to frame, right-click to duplicate or delete |
| Inspector | Name, position, rotation, scale; for rigged models, the animation clip, play/pause, speed and restart |
| Assets | The project's asset folder. Double-click a `.glb`/`.gltf` to place it in front of the camera |
| Log | Engine, editor and game output, colour-coded |
| Stats | Frame rate, GPU, objects and triangles, camera |

- **Play** (or F5) builds and runs the game with `dotnet run`, its output in the Log; **Stop** (Shift+F5) ends it.
- **Scenes** are saved as `Scenes/*.scene.json` (Ctrl+S; File > Save scene as, File > Open scene). If you close with unsaved changes, they're kept in `*.autosave.scene.json`.
- **Shortcuts:** Ctrl+N new scene, Ctrl+D duplicate, Del delete, Ctrl+Q quit.
- **Layout:** panels can be dragged and docked anywhere. The layout is remembered in `~/.config/trinity-editor/`.
- **Models:** any rigged glTF with `…LOD0` meshes and animations plays its clips; anything else loads as a static model. Textures come with the renderer (E2), so models show base colours for now.

Until scenes (E3) arrive, the editor's scene is a list of placed models. E3 replaces it with Trinity's own scene format, and the editor grows with each engine item: materials with E2, terrain painting with E5.

## Making a game

```csharp
Engine.Run(new MyGame(), new EngineOptions { Title = "Varendal", AppName = "varendal" });

sealed class MyGame : Game
{
    public override void Load()
    {
        Input.Bind("attack", GlfwMouseButton.Number1, GlfwGamepadButton.RightBumper);
        Input.Bind("left", GlfwKey.A, Binding.Axis(GlfwGamepadAxis.LeftX, positive: false));
    }

    public override void FixedUpdate(in FrameTime time) { /* simulation, physics: exactly 60 steps a second */ }

    public override void Update(in FrameTime time)
    {
        if (Input.Pressed("attack")) { /* ... */ }
        var move = Input.Axis("left", "right"); // keys give -1/0/1, sticks anything in between
    }

    public override void Render(CommandBuffer cmd, in FrameTime time) { /* draw */ }
}
```

- **Settings** live in `~/.config/<AppName>/settings.json`. The engine applies the `display` section (size, fullscreen, vsync) at start and saves it on exit. Games read and write their own sections with `Settings.Get("audio", MyJson.Default.AudioSettings)` and `Settings.Set("audio", value, MyJson.Default.AudioSettings)`, where `MyJson` is the game's `[JsonSerializable(typeof(AudioSettings))] partial class MyJson : JsonSerializerContext`.
- **Rebinding:** `Input.Rebind(action, ...)` changes an action, and the change is saved with the settings. Only rebound actions are stored, so changing a game's defaults still reaches players who never rebound that action.
- **F11** toggles fullscreen in every Trinity game.
- **Recording:** `RecordPath` in the options renders off screen at a fixed 60 fps and writes an H.264 video. `RecordSplash` puts the logo at the start.

## Licence

Trinity is **free for non-commercial use** under the [PolyForm Noncommercial License 1.0.0](LICENSE.md). Selling a game made with it, paid client work, or building it into a commercial product needs a commercial licence: see [COMMERCIAL.md](COMMERCIAL.md). Contributions: [CONTRIBUTING.md](CONTRIBUTING.md).

Trinity is built on BRUTAL by Ahwoo Limited, whose licence applies to every product made with it. Trinity meets its requirements by default: the BRUTAL logo before gameplay (also in Trinity Editor), the copyright line for credits (`Credits.Required`, shown in the editor's Help > About), and `NOTICE.txt` plus all third-party licence texts (in `licenses/`) next to every executable. Details: [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). For development only, `TRINITY_SKIP_SPLASH=1` skips the logo.

Trinity is not made or endorsed by Ahwoo. "BRUTAL" and its logo belong to Ahwoo Limited.
