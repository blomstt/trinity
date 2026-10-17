# Roadmap

Trinity grows one engine system at a time. Each item ends with something you can see working in the editor or a sample, and builds only what that needs.

| # | Item | Done when |
|---|---|---|
| E1 ✅ | **Core**: app host, main loop, time, input actions (keyboard, mouse, gamepad), logging, settings, BRUTAL logo splash and notice | A game is a class with `Update`/`Render`; input is read through named actions. **Done 2026-10-17:** `Engine.Run`, `FixedUpdate` at 60 Hz with `Alpha` for smoothing, named actions with rebinding saved to settings, gamepad sticks/triggers/buttons, `settings.json` with display settings, F11 fullscreen, BRUTAL splash, `NOTICE.txt`, `Credits.Brutal`, unit tests. Gamepad and fullscreen aren't yet tested on real hardware |
| E2 | **Renderer v1**: PBR materials with textures (glTF + KTX2), directional light with cascaded shadows, sky, fog, tone mapping, instancing, debug lines | A textured glTF scene, lit, with shadows |
| E3 | **Scenes and entities**: transforms, hierarchy, components, prefabs, scene files (JSON), loading | A scene with nested entities and components saves, loads and edits in the editor |
| E4 | **Animation**: clip blending, state machines, events (for example footsteps), root motion; large animated crowds | A character blends walk, run and attack; crowds blend instead of cutting |
| E5 | **Terrain**: heightmap, splat-painted materials, LOD, collision, grass and foliage instancing | Large terrain that characters stand on, painted in the editor |
| E6 | **Physics**: Jolt integration, character controller, colliders, raycasts | A character runs, collides, and is hit through physics queries |
| E7 | **Game UI**: retained UI with layout, styles, FreeType text, images, bars, buttons, focus and input (ADR 0002) | A HUD and a menu are built with it |
| E8 | **Audio**: 2D/3D sound, buses | Positional sound and music play in a scene |
| E9 | **Trinity Editor** (ADR 0003, started early): ImGui app with viewport, hierarchy, inspector, assets, log, stats, Play button; grows with every item | Open a project, place and animate models, press Play to run the game. **First version done 2026-10-17:** `./trinity-editor`, docked viewport/hierarchy/inspector/assets/log/stats, glTF placing (rigged and static), animation preview, scene save/load, ▶ Play. **Scripting done 2026-10-17** (ADR 0006): C# scripts on scene nodes, a Godot-style script editor with compiler errors in place, Inspector fields, Play with hot reload. Next: picking and move gizmos in the viewport, undo, a file dialog for opening projects, autocomplete (Roslyn language server) |

## Later

- **Navigation**: navmesh building and pathfinding for many agents.
- **Data and saves**: game data loading, save games.

## Order of work

E1 → E2 → E3 → E4 → E6 → E5 → E7 → E8, with the editor (E9) growing alongside each.

Rendering and the core come first, because everything shows up through them.
