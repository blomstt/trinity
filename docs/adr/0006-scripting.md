# 6. C# scripts on scene nodes, edited in Trinity Editor

Date: 2026-10-17. Status: accepted.

## Context

blomst wants a script editor like Godot's, running C#. Godot attaches a script to a node, shows its exported fields in the Inspector, and edits code in a built-in editor. Trinity has constraints Godot doesn't: games and the editor ship as NativeAOT executables (ADR 0005), which can't load code at runtime or create types by reflection, and everything has to stay light.

## Decision

- **Scripts run in the game, not the editor.** `Trinity.Scenes.Script` has `Ready`, `Update` and `FixedUpdate`; a scene (`*.scene.json`) names each node's script class and the field values set in the Inspector. The editor stays a native program that never loads game code.
- **No reflection.** `Trinity.Generators`, a source generator, finds `Script` classes at build time and writes code that registers each one: how to create it and how to set each public field. The editor learns scripts and fields by reading the source (`ScriptIndex`), so it knows about a new field as soon as it's typed.
- **The editor's own code editor**, drawn with ImGui: C# colouring, undo, find, indent, comment toggling. No language server and no Roslyn in the editor. Errors come from the compiler itself: saving runs `dotnet build` (Debug, the same build Play uses), and its messages are marked in the code.
- **Hot reload through `dotnet watch`.** Play runs the game under `dotnet watch`, so saving a script applies it to the running game, and a script that threw resumes once its code changes. dotnet watch needs the ASP.NET Core runtime; without it, Play uses `dotnet run` and saving restarts the game.
- **Scripts that throw are paused, not fatal.** The error goes to the log with its file and line, which the editor shows in the code.
- The editor's model drawing moved into the engine (`SceneRenderer`), so the editor and games draw scenes the same way; `SceneGame` runs a scene as a whole game.

## Consequences

- Script authoring works in the native editor (5.5 MB executable, up from 5.1 MB) and costs nothing while idle. Games stay native too.
- No autocomplete yet. It would come from Microsoft's Roslyn language server running beside the editor (a development tool, not shipped), as a later step.
- Inspector fields are limited to `float`, `double`, `int`, `bool`, `string` and `Vector3` public fields of classes deriving directly from `Script`. Properties work in games but don't show in the Inspector.
- `PublishAot` switches off the two runtime features hot reload needs (startup hooks, metadata updates) in every build. `Directory.Build.props` turns them back on outside Release, so Play's Debug build hot-reloads while published games keep them off.
- Code applied by hot reload has no line numbers in stack traces, so a script error after a reload is placed on the script's class rather than its line.
- Hot reload can't change everything (adding a class, changing a field's initial value for objects that already exist): dotnet watch restarts the game for those.
- Scenes are still flat lists of nodes until E3 adds hierarchy and components; scripts then attach to entities the same way.
