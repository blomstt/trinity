# 5. Ship native executables (NativeAOT)

Date: 2026-10-17. Status: accepted.

## Context

Trinity should be light, the BRUTAL way. A JIT build needs the .NET runtime installed (74 MB), compiles code at every start, and keeps JIT and tiering threads around. .NET's NativeAOT compiles a program ahead of time into one native executable. It rules out runtime reflection and code generation, which BRUTAL's own design already avoids. Two things in Trinity used reflection: `System.Text.Json` for settings, project and scene files, and Brutal.Gltf, which reads glTF through reflection-based JSON.

## Decision

- Games and Trinity Editor publish with NativeAOT (`PublishAot` in the executable projects). `dotnet build` and `dotnet run` stay JIT, so iteration stays fast.
- Every project sets `IsAotCompatible`, so the trim and AOT analyzers flag reflection at build time, not when a player runs the game.
- JSON is always source-generated: `Settings.Get/Set` take a `JsonTypeInfo<T>` from a `JsonSerializerContext` (`TrinityJson` for the engine's sections, `EditorJson` for the editor).
- Trinity.Animation reads glTF itself (`GltfFile`, about 150 lines, `.glb` and `.gltf`), replacing Brutal.Gltf. Its output is identical on the sample assets, and it drops Brutal.Gltf with its texture libraries (KTX, GLI) from every build.
- Publishing leaves out what doesn't run: debug symbols and `.pdb` files, two duplicate copies of GLFW, and the ImGui OpenGL loader.
- Default code generation: optimising for speed or targeting AVX2 (`x86-64-v3`) made no measurable difference on the crowd stress test and only added size.

## Consequences

Measured on the editor and a crowd stress test of 64,000 GPU-skinned, animated characters (RX 9070 XT, 16 threads):

| | JIT | Native |
|---|---|---|
| Editor on disk | 11 MB + 74 MB .NET runtime | 8.3 MB, no runtime |
| Editor start to ready | 0.3 s | 0.1 s |
| Editor idle memory, threads | 162 MB, 12 | 112 MB, 7 |
| Crowd test, 64,000 characters | about 755 fps, simulation 4.6 ms | about 730 fps, simulation 5.0–5.5 ms |

- About 3% lower frame rate at 64,000 characters. Without the JIT's dynamic profile-guided optimisation, the simulation's hot loop runs about 10% slower. If it starts to matter, the answer is hand-tuning that loop (SIMD, data layout), not going back to the JIT.
- A native publish takes about a minute and needs `clang`. The `./trinity-editor` launcher runs the native editor from `artifacts/editor/` unless a file under `src/` is newer, then falls back to the JIT build.
- Watch out for one pitfall: with generated JSON code, an `init` property missing from the file becomes null, so deserialised types use `set`.
- The editor's Play runs the game as a JIT Debug build (under `dotnet watch`, ADR 0006), the fast path for development.
