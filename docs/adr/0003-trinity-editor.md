# 3. Trinity Editor, started early

Date: 2026-10-17. Status: accepted.

## Context

Trinity was planned as a library with only a small debug overlay (E9), late in the roadmap. An editor application like Godot's makes the engine usable much sooner.

## Decision

Build Trinity Editor (`src/Trinity.Editor`) now, as a Dear ImGui app (BRUTAL's docking-branch binding), and grow it with each engine item: viewport with the renderer, hierarchy and inspector with scenes, terrain painting with terrain.

## Consequences

- The editor draws its 3D view into a `RenderTarget` with Trinity's renderer, then draws its UI through a classic render pass, which BRUTAL's ImGui Vulkan backend requires (it doesn't support dynamic rendering).
- `GraphicsDevice` now uses `DeviceEx`, exposes its swapchain image views, and `EndFrame(endRendering: false)` lets a tool end its own pass.
- Until E3, a scene is a simple list of placed models; E3 adds hierarchy and components.
