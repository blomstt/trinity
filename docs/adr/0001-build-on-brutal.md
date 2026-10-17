# 1. Build on BRUTAL

Date: 2026-10-17. Status: accepted.

## Context

Trinity is a game engine for large-scale 3D games, where performance and control matter more than a general-purpose engine's convenience. BRUTAL (Ahwoo / RocketWerkz, released 2026-10-16) is a C# framework with direct native interop, unmanaged memory, a job system, and bindings for Vulkan, GLFW, Jolt, audio and ImGui. A prototype on it rendered 64,000 GPU-skinned, animated characters at about 200 fps on an RX 9070 XT.

## Decision

Build Trinity on BRUTAL: BRUTAL is the foundation (native access, maths, memory, jobs, bindings), and Trinity is the engine layer above it.

## Consequences

- Trinity has to provide what a full engine does: renderer, scenes, animation, terrain, physics integration, navigation, UI and editor tools. That's the bulk of the work.
- Full control over simulation and rendering, with no engine layers in between.
- BRUTAL's licence requires its logo before the product's main function, a credit, and shipping `NOTICE.txt`. Trinity builds that in.
