# Roadmap

Trinity grows in vertical slices: each milestone ends with something playable in Varendal, and only builds the engine parts that slice needs. The Godot version of Varendal (`~/varendal--age-of-kings`) stays untouched as the reference until the new one catches up.

## M1: A battle

**Done when:** you play the hero (combo, dodge, stamina, as in the Godot game) inside a large army battle on real terrain. You give orders, armies hold lines and break on morale, there's a HUD, and it runs at Bannerlord scale or beyond.

### Engine

| # | Item | Done when |
|---|---|---|
| E1 ✅ | **Core**: app host, main loop, time, input actions (keyboard, mouse, gamepad), logging, settings, BRUTAL logo splash and notice | A game is a class with `Update`/`Render`; input is read through named actions. **Done 2026-10-17:** `Engine.Run`, `FixedUpdate` at 60 Hz with `Alpha` for smoothing, named actions with rebinding saved to settings, gamepad sticks/triggers/buttons, `settings.json` with display settings, F11 fullscreen, BRUTAL splash, `NOTICE.txt`, `Credits.Brutal`, unit tests. Gamepad and fullscreen aren't yet tested on real hardware |
| E2 | **Renderer v1**: PBR materials with textures (glTF + KTX2), directional light with cascaded shadows, sky, fog, tone mapping, instancing, debug lines | A textured glTF scene lit like the concept art, with shadows |
| E3 | **Scenes and entities**: transforms, hierarchy, components, prefabs, scene files (JSON), loading | A battle scene file describes terrain, props and armies, and loads |
| E4 | **Animation**: clip blending, state machines, events (hit frames, footsteps), root motion; crowd animation from the prototype | The hero blends walk, run, attack, dodge; soldiers blend instead of cutting |
| E5 | **Terrain**: heightmap, splat-painted materials, LOD, collision, grass and foliage instancing; import from Varendal's Terrain3D data | The battlefield is real terrain that soldiers stand on |
| E6 | **Physics**: Jolt integration, character controller, colliders, raycasts | The hero runs, collides, and gets hit through physics queries |
| E7 | **Game UI**: retained UI with layout, styles, FreeType text, images, bars, buttons, focus and input; built to theme like `UiTheme` | The battle HUD (health, stamina, orders, army strength) is built with it |
| E8 | **Audio**: 2D/3D sound, buses, from the existing sound set | Combat, footsteps and ambience play in the battle |
| E9 | **Trinity Editor** (ADR 0005, started early): ImGui app with viewport, hierarchy, inspector, assets, log, stats, Play button; grows with every item | Open a project, place and animate models, press Play to run the game. **First version done 2026-10-17:** `./trinity-editor`, docked viewport/hierarchy/inspector/assets/log/stats, glTF placing (rigged and static), animation preview, scene save/load, ▶ Play. **Scripting done 2026-10-17** (ADR 0008): C# scripts on scene nodes, a Godot-style script editor with compiler errors in place, Inspector fields, Play with hot reload. Next: picking and move gizmos in the viewport, undo, a file dialog for opening projects, autocomplete (Roslyn language server) |

### Game (in the Varendal repo)

| # | Item | Done when |
|---|---|---|
| G1 | **Project**: Varendal C# game project using Trinity | It starts, shows the logo, and loads a battle |
| G2 | **Hero combat** ported from `battle/` (combo, dodge, stamina, hits) | Feels like the Godot version |
| G3 | **Army battle** from the prototype plus Varendal's rules: formations, orders, morale, routing (per the battle-polish spec) | Bannerlord-style lines and breaks |
| G4 | **Battle data** from `defs/` and `data/`: units, weapons, factions | Units come from the game's data, not hardcoded |
| G5 | **Battle HUD** on Trinity UI | Matches the Godot HUD's information |

## Later milestones (rough order)

- **M2: The campaign map.** Map terrain, parties, fog of war, locations, clock, encounters, travel. Adds navigation and a lot of UI.
- **M3: Realm and camp.** Economy, recruitment, camp, settling, hero growth, inventory, saves. Mostly game logic and UI screens.
- **M4: Parity.** Everything the Godot version does, then past it. Then the Godot version can retire.

## Order of work for M1

E1 → E2 → G1 → E3 → E4 → E6 → G2 → E5 → G3 → E7 → G5 → E8 → E9 → G4

Rendering and the core come first because everything shows up through them. Then the hero, since that's where feel matters most. The army systems go on top of the prototype, which already holds 64,000 animated soldiers at about 200 fps.
