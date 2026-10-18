# macOS

Trinity runs on Apple silicon Macs through MoltenVK, which translates Vulkan to Metal. This page is what that needs. Who is doing what, and the order, is in #20.

## Requirements

- **Apple silicon.** BRUTAL ships `osx-arm64` natives only (GLFW, Dear ImGui, Stb, Vulkan).
- **Vulkan 1.3**, for dynamic rendering and synchronization2. MoltenVK 1.4 provides it. `PickGpu` doesn't check the device's version yet.
- **Portability.**
  - The instance enables `VK_KHR_portability_enumeration` and sets the enumerate-portability flag. Without them, `vkCreateInstance` itself fails with `VK_ERROR_INCOMPATIBLE_DRIVER`.
  - The device enables `VK_KHR_portability_subset` when the GPU lists it.

## Development setup

```bash
brew install molten-vk vulkan-loader vulkan-validationlayers shaderc
```

BRUTAL's `libVulkanEx.dylib` loads `@rpath/libvulkan.1.dylib`, and its only rpaths are `/usr/local/lib` and `@loader_path`. GLFW opens `libvulkan.1.dylib` by bare name. Homebrew installs it to `/opt/homebrew/lib`, so one of these is needed:

- **No sudo:** `DYLD_LIBRARY_PATH=/opt/homebrew/lib dotnet run -c Release --project samples/Sandbox`. It covers both lookups. macOS strips `DYLD_*` only for SIP-protected binaries, and `dotnet` isn't one.
- **Once for the whole machine:** `sudo ln -s /opt/homebrew/lib/libvulkan.1.dylib /usr/local/lib/`.

`TRINITY_VALIDATION=1` needs `vulkan-validationlayers`; without them it quietly does nothing. `VK_LOADER_DEBUG=layer` shows whether `VK_LAYER_KHRONOS_validation` was inserted.

The three Sandbox scenes have run validation-clean this way on an M1 and an M2 (MoltenVK 1.4.2).

## MoltenVK limits that shape the renderer

- **Samplers.** MoltenVK allows 16 samplers per stage, or 1024 with update-after-bind.
  - A combined image sampler counts as a sampler, a sampled image and a resource at once.
  - So the bindless array is update-after-bind, and is sized from all three limits, per stage and per set, minus the frame's own bindings. That gives 1016 textures.
  - The fixed-array fallback (`TRINITY_NO_BINDLESS=1`) is sized from the plain sampler limit, which leaves 8 textures on MoltenVK. Keep it as a diagnostic, not as the macOS path.
- **Limit types.** BRUTAL declares the Vulkan limits as `int`, so a driver's `0xFFFFFFFF` ("no limit", as NVIDIA reports) arrives as -1. Read limits as unsigned before subtracting from them.
- **Comparison samplers** can't go through a descriptor (`mutableComparisonSamplers` is false), so the shadow sampler is immutable in its layout.
- **Threadgroup memory** is 32 KiB, and Metal pads a shared `vec3` to 16 bytes. Size `shared` arrays for that.
- **Fast maths.** A NaN that another driver turns into noise can fill a whole mip under Metal. GGX at roughness 0 was one: it divides 0 by 0.
- Check every pass when this changes: the meshes, shadows, sky, fog and transparency, and the five `.comp` shaders.

## Shipping a build

- **The loader won't find MoltenVK next to the executable.** It looks for ICD manifests in a `.app`'s `Contents/Resources/vulkan/icd.d` and in system and XDG folders. Setting `VK_DRIVER_FILES` from C# doesn't help either, because `Environment.SetEnvironmentVariable` doesn't reach native `getenv` on Unix. Two lighter options:
  - **Ship MoltenVK alone, named `libvulkan.1.dylib`,** next to the executable. `@loader_path` finds it, and there is no loader or ICD file. Validation layers are for development only. dyld's version check needs a try first.
  - **A `.app` layout,** with the ICD under `Contents/Resources/vulkan/icd.d`.

  Either way, call `Glfw.InitVulkanLoader` before `Glfw.Init`, so GLFW uses the same library.
- **Gatekeeper.** Gatekeeper blocks unsigned binaries and dylibs that were downloaded. A build for others needs a `.app`, Developer ID signing and notarization.
- **Licences.** `TrinityCollectLicenses` collects only from NuGet packages, so a hand-copied MoltenVK or loader has to be credited by hand (both are Apache-2.0).
- **Publish output.** `TrinitySlimPublish` strips only the `.so` names, so an `osx-arm64` publish ships three identical GLFW dylibs and `libglad.dylib`. The README's publish commands are for `linux-x64`.

## Linux-only code

- **Show in file manager** runs `xdg-open`. On `main` that call isn't caught, so on a Mac the menu item closes the editor. #17 routes it through `Safely`. macOS uses `open`.
- **Trash.** `ProjectFiles.Trash` follows the freedesktop.org layout, so on a Mac deleted files go to `~/.local/share/Trash`. `NSFileManager trashItemAtURL` (through `objc_msgSend`, no new dependency) gives *Put Back* and each volume's own `.Trashes`.
- **Editor fonts** are hard-coded to `/usr/share/fonts/noto/...` (`ImGuiHost.cs`), so a Mac gets ImGui's 13 px default.
- **Config and cache folders** use `XDG_CONFIG_HOME` and `XDG_CACHE_HOME`, or `~/.config` and `~/.cache` (`Settings.cs`, `EditorApp.cs`, `ProjectManager.cs`, `GraphicsDevice.cs`). To decide: keep those, or use `~/Library/Application Support` and `~/Library/Caches`.
- **Shortcuts.** ImGui 1.92.2 swaps Cmd and Ctrl on macOS by default. The menu labels, F11 and the code editor's word movement still assume Linux.
- **Retina.** The editor viewport renders at half resolution.

## CI

There is no CI yet. A macOS job would build and run the tests, which need no GPU. The runner needs `brew install shaderc` for `glslc`.
