# Trinity

A lightweight C# game engine built on [BRUTAL](https://github.com/ahwootech/BRUTAL).

BRUTAL provides the foundation: C# access to native libraries, maths, unmanaged memory, jobs, and bindings for Vulkan, GLFW, Jolt, FMOD/OpenAL, FreeType and Dear ImGui. Trinity is everything a game needs above that: a renderer, scenes, animation, terrain, physics integration, navigation, a game UI system, audio, data and saves, and editor tools.

A game is its own C# project that uses Trinity as a library, made and run from Trinity Editor.

**Lightweight first, the BRUTAL way:** nothing ships or runs that the game doesn't use. Games and the editor publish as single native executables (NativeAOT), with no .NET runtime to install. Shaders are compiled to SPIR-V at build time, so no shader compiler ships. Tools only redraw when something changes. Every dependency has to earn its place.

| Measured (17 Oct 2026), native build | Trinity Editor | Sandbox sample |
|---|---|---|
| On disk, everything that ships | 9.3 MB (6.0 MB executable) | 11 MB with its test models (5.2 MB executable) |
| Start to ready | 0.1 s (JIT: 0.3 s) | 0.1 s (JIT: 0.28 s) |
| Memory | 53 MB resident when idle with an empty project, 150 MB peak at start | 145 MB peak (JIT: 174 MB) |
| Threads / CPU when idle | 7 / 0% (JIT: 12) | — |

## Status

Early. Roadmap: [docs/ROADMAP.md](docs/ROADMAP.md). Decisions: [docs/adr/](docs/adr/).

| Module | What it is | State |
|---|---|---|
| `Trinity.Core` | Window (with fullscreen), input actions for keyboard, mouse and gamepad, player settings, `Jobs.For`, logging, required credits | E1 done |
| `Trinity.Graphics` | Vulkan device and frame loop, GPU buffers, textures, SPIR-V shader loading, pipelines, cameras and frustums, video recording | First version |
| `Trinity.Animation` | glTF models (its own small `.glb`/`.gltf` reader): meshes with tangents, materials, images, skins; animation baked for GPU skinning | First version |
| `Trinity.Rendering` | The renderer: HDR forward PBR, bindless materials and textures, procedural sky, cascaded shadows, fog, tone mapping, culling, debug lines | E2 done |
| `Trinity` | `Engine.Run(game)`: the BRUTAL splash, then a loop of `FixedUpdate` (60 Hz) / `Update` / `Render`, settings saved on exit. `Trinity.Scenes`: scene files, C# scripts on nodes, `SceneRenderer`, `SceneGame` | E1 done; scenes and scripts first version |
| `Trinity.Generators` | Build-time only: registers a game's scripts and their Inspector fields, so scripts need no reflection (NativeAOT) | First version |
| `Trinity.Editor` | **Trinity Editor**: viewport, hierarchy, inspector, assets, log, stats, a C# script editor, ▶ Play with hot reload | First version |
| `samples/Sandbox` | Scenes made in the editor with small scripts: scripting (`main`), a material test (`materials`), and a 5,000-character stress test with a day cycle (`crowd`) | Runs |
| `tests/Trinity.Tests` | Unit tests for the core, scenes and scripting | 24 passing |

## Renderer

Each frame: four cascaded shadow maps for the sun, then opaque meshes in HDR (`RGBA16F`), the procedural sky behind them, transparent meshes sorted back to front, then exposure and tone mapping into the window (or an editor viewport) with debug lines on top.

- **Materials:** glTF metallic-roughness: base colour, metallic-roughness, normal, occlusion and emissive textures; opaque, alpha-masked and alpha-blended; one- or two-sided. GGX specular with height-correlated Smith visibility, Lambert diffuse.
- **Textures are bindless:** every texture in one descriptor array (16,384 slots here), every material in one GPU buffer; a draw passes a material index. GPUs without descriptor indexing get a fixed array of 64 with the same shaders (`TRINITY_NO_BINDLESS=1` forces it). Images decode on worker threads and upload a few per frame, with mipmaps, showing placeholders until they arrive; models load on worker threads too.
- **Sky:** a physically based atmosphere (Rayleigh, Mie, ozone; after Hillaire 2020) computed on the GPU into lookup tables when the sun or sky changes (0.11 ms), giving the sky, the sun's colour, diffuse sky light (spherical harmonics) and reflections (a cubemap prefiltered by roughness). Nothing is baked.
- **Shadows:** four cascades of 2048², fitted as spheres and snapped to texels so they don't shimmer, blended at the seams, 3×3 filtered compare samples, normal-offset and slope bias. Skinned and alpha-masked casters cast correctly.
- **Fog and tone mapping:** exponential height fog coloured by the sky in the view direction, in linear HDR; exposure in stops; AgX-style or ACES tone mapping.
- **Culling and LOD:** per pass (the camera and each cascade), bounding spheres are tested against the frustum and far instances use their low-detail mesh; vertex shaders read instances through the visible list.
- **Debug:** `Debug.Line`, `Debug.Box`, `Debug.Sphere`, `Debug.Arrow` (for a frame or some seconds, depth-tested or on top); they compile away in Release builds. Viewport shading views: albedo, normals, shadow cascades. GPU time per pass is measured with timestamps (editor Stats panel, game status line).
- **Scenes carry their environment:** sun angle and strength, haze, shadows, fog, exposure and tone mapper, edited in the editor's Environment panel; scripts can change it at runtime (`samples/Sandbox/Scripts/DayCycle.cs`).

| GPU time (RX 9070 XT, 1600×900) | Total | Shadows | Opaque | Sky |
|---|---|---|---|---|
| Material test: 87,000 triangles, textures, glass | 0.4 ms | 0.24 ms | 0.12 ms | 0.01 ms |
| 5,000 animated characters, moving sun | 0.72 ms (3.4 ms before culling) | 0.36 ms | 0.23 ms | 0.11 ms (update) |

## Shaders

GLSL files in a project's `Shaders/` folder (`.vert`, `.frag`, `.comp`) are compiled by `glslc` (Arch: `shaderc`) during `dotnet build` and shipped as `Shaders/<name>.spv`. `#include "skinning.glsl"` and other includes resolve from the project's `Shaders/` folder and `Trinity.Animation/Shaders/`. At runtime `ShaderLibrary.Load("mesh.vert")` just reads the `.spv`. After editing a shader, rebuild.

## Build and run

Needs the .NET 10 SDK, a Vulkan 1.3 GPU, and `glslc` for shaders (Arch: `shaderc`). BRUTAL comes from nuget.org as the official `Ahwoo.Brutal.*` packages. Linux only for now.

```bash
dotnet run -c Release --project samples/Sandbox
dotnet test tests/Trinity.Tests
```

`dotnet build` and `dotnet run` use the JIT, for quick iteration. To ship, publish native (needs `clang`):

```bash
dotnet publish samples/Sandbox -c Release -r linux-x64 -o artifacts/sandbox
```

The folder holds the executable, the few native libraries it loads, `Shaders/`, assets, `NOTICE.txt` and `licenses/`. Nothing else is needed on the player's machine besides a Vulkan driver. Every Trinity project is checked for NativeAOT at build time (`IsAotCompatible`), so reflection-based code is flagged as a warning while you write it. JSON goes through source-generated contexts (`[JsonSerializable]`), never reflection.

## Trinity Editor

```bash
./trinity-editor samples/Sandbox     # or any folder with a trinity.json; with no argument it reopens the last project
dotnet publish src/Trinity.Editor -c Release -r linux-x64 -o artifacts/editor   # the native editor, which ./trinity-editor then prefers
# It's also in the app launcher (rofi) as "Trinity Editor", via ~/.local/share/applications/trinity-editor.desktop
```

A project is a folder with a `trinity.json`:

```json
{ "name": "My game", "game": "src/MyGame/MyGame.csproj", "assets": "Assets", "scenes": "Scenes", "scripts": "Scripts" }
```

| Panel | What it does |
|---|---|
| Viewport | The scene, live: right-drag to orbit, middle-drag (or Shift + right-drag) to pan, wheel to zoom, **F** to frame the selection |
| Hierarchy | Everything placed. Click to select, double-click to frame, right-click to duplicate or delete |
| Inspector | Name, position, rotation, scale; the attached script and its fields; for rigged models, the animation clip, play/pause, speed and restart |
| Assets | The project's asset folder. Double-click a `.glb`/`.gltf` to place it in front of the camera |
| Log | Engine, editor and game output, colour-coded |
| Stats | Frame rate, GPU, objects and triangles, camera |
| Script | The C# script editor (next to the Viewport): the project's scripts, files in tabs, errors marked in the code and listed underneath |

- **Play** (or F5) saves, then builds and runs the game, its output in the Log; **Stop** (Shift+F5) ends it. With the ASP.NET Core runtime installed (Arch: `aspnet-runtime`, needed by `dotnet watch`), saving a script while the game runs applies the change to the running game (hot reload). Without it, saving restarts the game.
- **Scenes** are saved as `Scenes/*.scene.json` (Ctrl+S; File > Save scene as, File > Open scene). If you close with unsaved changes, they're kept in `*.autosave.scene.json`.
- **Shortcuts:** Ctrl+N new scene, Ctrl+D duplicate, Del delete, Ctrl+Q quit.
- **Layout:** panels can be dragged and docked anywhere. The layout is remembered in `~/.config/trinity-editor/`.
- **Models:** any rigged glTF with `…LOD0` meshes and animations plays its clips; anything else loads as a static model. Textures come with the renderer (E2), so models show base colours for now.

Scenes are a list of placed models (nodes) until E3 brings hierarchy and components. The editor grows with each engine item: materials with E2, terrain painting with E5.

### Scripts

Like Godot: select an object, pick **Script > New script...** in the Inspector, and write its behaviour in C#:

```csharp
public sealed class Spin : Script
{
    public float DegreesPerSecond = 90; // public fields show in the Inspector, values saved per object

    public override void Update(in FrameTime time) =>
        Node.Rotation += new Vector3(0, DegreesPerSecond * time.Delta, 0);
}
```

- `Ready()` runs once when the scene starts, `Update` every frame, `FixedUpdate` at 60 Hz. A script reaches its `Node` (position, rotation, scale, animation clip), the `Scene` (other nodes, the camera) and `Input`.
- Inspector fields: public `float`, `double`, `int`, `bool`, `string` and `Vector3` fields. A value set in the Inspector is saved in the scene; **Reset** goes back to the script's default.
- **Script editor:** C# colours, line numbers, undo/redo, Ctrl+F find (F3 next), Ctrl+/ comment, Tab/Shift+Tab indent, Alt+Up/Down move lines, auto-indent. Saving (Ctrl+S) checks the code with the compiler; errors are underlined where they are. A script that throws while the game runs is reported with its line, and pauses until its code changes, so the game keeps running.
- Scripts are found at build time by `Trinity.Generators`, so games stay NativeAOT. A game in its own repository references it like the samples do (see `Directory.Build.targets`).
- `./trinity-editor PROJECT --open Scripts/Spin.cs:12` opens a script at a line.
- A game that's just its scenes is one line: `Engine.Run(new SceneGame("Scenes/main.scene.json"), options)` (see `samples/Sandbox`).

## Making a game

```csharp
Engine.Run(new MyGame(), new EngineOptions { Title = "My game", AppName = "my-game" });

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
