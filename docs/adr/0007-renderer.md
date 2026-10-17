# 7. Renderer v1: HDR forward, bindless materials, procedural sky, cascaded shadows

Date: 2026-10-17. Status: accepted.

## Context

E2 asks for a renderer that lights textured glTF scenes well, with shadows, and stays small. The engine targets large outdoor scenes with many animated characters, on Vulkan 1.3, and every build ships as a NativeAOT executable. The full specification (pipeline order, materials, sky, shadows, post-processing, debugging, performance) came from blomst on 2026-10-17.

## Decision

- **Forward rendering into HDR (`RGBA16F`)** with a depth buffer, per view: the camera's pass, then the sky where nothing was drawn, then transparency, then tone mapping into the window or an editor viewport, with debug lines and editor overlays after tone mapping, tested against the scene's depth. Forward keeps one light model and works with transparency; a G-buffer isn't needed for one sun.
- **Reversed depth** (1 near, 0 far, cleared to 0, greater-or-equal) everywhere, for precision over long distances.
- **Bindless materials:** all textures in one partially-bound, variable-size descriptor array; all materials in one storage buffer; a draw pushes its material index, and instances can override it. Without descriptor indexing the same shaders compile against a fixed array of 64 (a specialization constant), and draws stay per material so indices are uniform. Descriptor sets are per frame in flight and updated at the start of their frame, so no update-after-bind is needed.
- **Resources are released late:** `GraphicsDevice.Release` runs destruction once the frames that might use a resource are done; `GpuImage` tracks layouts, so passes say where an image is going, not where it was.
- **Loading is asynchronous:** models parse, and images decode, on worker threads; uploads (with blit-generated mipmaps) run within a per-frame budget; placeholders show until data arrives. Games can wait for everything (`SceneRenderer.LoadAll`) for a load screen or a recording.
- **The sky is computed, not baked:** a single-scattering atmosphere (Hillaire 2020: Rayleigh, Mie, ozone, plus a cheap multiple-scattering term) into a transmittance table and a sky-view table, from which the sky pass, the fog colour, 9 spherical-harmonic coefficients (diffuse sky light) and a GGX-prefiltered cubemap (reflections) all come. It reruns only when sun or sky settings change, in about 0.1 ms. The sun's colour at the ground comes from the same model on the CPU.
- **Cascaded shadow maps:** four 2048² layers of one depth image; each cascade a sphere around its slice of the view, sized in 1/16 m steps and snapped to texels, so edges don't crawl or shimmer; a blend band between cascades; nine bilinear compare taps; slope-scaled depth bias plus normal offset; depth clamp, so casters behind the light's near plane still cast. Masked casters run a fragment shader; opaque ones are depth-only.
- **Tone mapping:** exposure in photographic stops, then an AgX-style curve (AgX's working-space matrices with Trinity's own sigmoid) or Narkowicz's ACES fit, into an sRGB target.
- **Culling and LOD on the CPU, laid out for the GPU:** instances are written once; each pass (camera, four cascades) gets a list of visible instance indices per mesh and level of detail, which the vertex shaders read through. Moving the culling to a compute shader later only changes who writes the lists.
- **Debug drawing compiles away:** `Debug.*` methods are `[Conditional("TRINITY_DEBUG_DRAW")]`, defined for every build except Release; the editor always defines it.

## Consequences

- One frame of the material test scene costs 0.4 ms of GPU at 1600×900; 5,000 animated characters with a moving sun cost 0.72 ms, down from 3.4 ms before culling and LOD (shadows were 70% of it).
- Tiled (clustered) lighting and GPU-driven culling are not built: there is one light, and culling 5,000 instances on the CPU costs little. Both become worth it with many lights or far more instances, and the data layout is ready for them.
- Textures are uncompressed RGBA8 with mipmaps. Compressed textures (BC7 in KTX2, cooked at build time like shaders) are the next memory win.
- The AgX curve is an approximation, not the reference; ACES is there for a punchier look.
- Checked with the Khronos validation layers, synchronisation validation included (`TRINITY_VALIDATION=1 VK_KHRONOS_VALIDATION_VALIDATE_SYNC=true`): no messages in the game (window and off-screen), the editor, or the fixed-array fallback. Validation found two problems, both fixed: `discard` compiles to demote-to-helper for Vulkan 1.3, which needs its device feature enabled, and resources released while releasing others were leaked at shutdown.
