# macOS port

Trinity runs on Linux today. This page is the plan for macOS (Apple silicon), and who does what.

## Where it stands (17 Oct 2026)

- On an Apple silicon Mac (.NET 10.0.402, `glslc` from Homebrew's `shaderc`), `dotnet build` works and `dotnet test tests/Trinity.Tests` passes (149 tests).
- Nothing that opens a window runs. `samples/Sandbox` stops at the first Vulkan call (`GraphicsDevice.cs:100`): BRUTAL's `libVulkanEx.dylib` needs `libvulkan.1.dylib`, and macOS has no Vulkan loader.
- BRUTAL already ships `osx-arm64` builds of GLFW, Dear ImGui, Stb and its Vulkan bindings, so the native side is mostly there. Vulkan runs on macOS through MoltenVK, which translates it to Metal.

## The work

### A. Platform (Ege)

1. **Vulkan loader and MoltenVK.**
   - For development: Homebrew's `vulkan-loader` and `molten-vk`, or the LunarG SDK.
   - For a published game or editor: `libvulkan.1.dylib`, `libMoltenVK.dylib` and its ICD file next to the NativeAOT executable, credited in `THIRD_PARTY_NOTICES.md` (both are Apache-2.0).
2. **Portability.**
   - The instance enables `VK_KHR_portability_enumeration` and sets `EnumeratePortabilityBit`; without that the loader lists no MoltenVK device.
   - The device enables `VK_KHR_portability_subset` when the GPU lists it.
3. **Linux-only paths.**
   - *Show in file manager* runs `xdg-open` (`Panels/FileSystem.cs`); macOS uses `open`.
   - `ProjectFiles.Trash` follows the freedesktop.org layout and reads `/proc/self/mounts` and `/proc/self/status`; macOS uses `~/.Trash`.
   - Settings, editor state and the pipeline cache use `XDG_CONFIG_HOME` and `XDG_CACHE_HOME` or `~/.config` and `~/.cache` (`Settings.cs`, `EditorApp.cs`, `ProjectManager.cs`, `GraphicsDevice.cs`). To decide: keep those, or use `~/Library/Application Support` and `~/Library/Caches`.
   - `trinity-editor` publishes for `linux-x64`; it needs `osx-arm64` on a Mac.
4. **CI.** A `macos` job that builds and runs the tests (no GPU needed). Later, an offscreen render check if MoltenVK works on the runner.

### B. Rendering (Kadir)

1. **Device and swapchain on MoltenVK.** The GLFW surface (`VK_EXT_metal_surface`), present modes and formats, and resizing.
2. **Features MoltenVK limits.**
   - Bindless textures go through Metal argument buffers, with lower per-stage limits. The fixed-array fallback (`TRINITY_NO_BINDLESS=1`) is the first thing to try.
   - Then check depth clamp (shadows), anisotropy and timestamp queries (GPU timings), and whatever the validation layer reports.
3. **Shaders.** MoltenVK turns SPIR-V into Metal shaders. Check every pass: meshes, shadows, sky, fog and transparency.
4. **Retina.** Framebuffer size against window size (GLFW content scale), for the viewport, ImGui and picking.
5. **Window and input on Cocoa.** Cmd against Ctrl in the editor's shortcuts, and fullscreen.

## Order

A1 and A2 come first, since both of us need a window. Then A3 and B run in parallel. A4 can come at any time.

## Open questions

- Intel Macs: BRUTAL ships `osx-arm64` only.
- Whether BRUTAL will ship MoltenVK in its Vulkan package, which would make A1 smaller.
- CODEOWNERS areas for this work, once we agree on them.
