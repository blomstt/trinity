# 1. Build on BRUTAL

Date: 2026-10-17. Status: accepted.

## Context

Varendal runs on Godot 4.7. Its battles are meant to feel like Bannerlord, at a scale and with a control over performance that Godot makes hard. BRUTAL (Ahwoo / RocketWerkz, released 2026-10-16) is a C# framework with direct native interop, unmanaged memory, a job system and bindings for Vulkan, GLFW, Jolt, audio and ImGui. A prototype on it (`~/Projects/brutal-battle`) ran 128,000 soldiers in melee at 110 fps and 64,000 GPU-skinned soldiers at about 200 fps on an RX 9070 XT.

## Decision

Move Varendal off Godot onto BRUTAL, with Trinity as the engine layer between them.

## Consequences

- Trinity has to provide what Godot did: renderer, scenes, animation, terrain, navigation, UI, editor tools. That's the bulk of the work.
- Full control over the battle simulation and rendering, which is what the switch is for.
- BRUTAL's licence requires its logo before gameplay, a credit, and shipping `NOTICE.txt`. Trinity builds that in.
- Godot Varendal stays as the reference until the new version reaches parity.
