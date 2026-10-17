# 2. A custom game UI system from the start

Date: 2026-10-17. Status: accepted.

## Context

Games need many screens: HUDs, menus, inventories, dialogs. Options were Dear ImGui for everything at first, a custom retained-mode UI, or binding an HTML/CSS-style library such as RmlUi.

## Decision

Build Trinity's own retained-mode game UI from the start: layout, styles, FreeType text, images, controls, focus and input. Dear ImGui is used only for developer tools, such as Trinity Editor.

## Consequences

- Games get a UI that can look like their own, and screens are built once.
- More engine work before the first game screen (roadmap item E7).
- No C++ UI library binding to maintain; text rendering goes through BRUTAL's FreeType binding.
