using System.Runtime.InteropServices;
using Brutal.ImGuiApi;
using Brutal.ImGuiApi.Backends;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Trinity.Graphics;

namespace Trinity.Editor;

/// <summary>Dear ImGui (BRUTAL's docking-branch binding) wired to Trinity's window and GPU.</summary>
sealed unsafe class ImGuiHost : IDisposable
{
    readonly GlfwBackend _platform;
    readonly VulkanBackend _renderer;
    readonly nint _iniPath;
    public readonly UiPass Pass;

    /// <summary>A monospaced font, for the script editor.</summary>
    public readonly ImFontPtr Mono;

    /// <summary>True the first time the editor runs (no saved layout yet).</summary>
    public readonly bool FirstRun;

    public ImGuiHost(Window window, GraphicsDevice gpu, string configDir)
    {
        ImGui.CreateContext();
        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.DockingEnable | ImGuiConfigFlags.NavEnableKeyboard;

        // Panel layout is remembered between runs
        Directory.CreateDirectory(configDir);
        var ini = Path.Combine(configDir, "layout.ini");
        FirstRun = !File.Exists(ini);
        _iniPath = Marshal.StringToCoTaskMemUTF8(ini);
        io.IniFilename = (byte*)_iniPath;

        const string font = "/usr/share/fonts/noto/NotoSans-Regular.ttf";
        if (File.Exists(font))
            io.Fonts.AddFontFromFileTTF(font, 17f);
        // For code; ImGui's built-in font is monospaced too, if Noto Sans Mono isn't installed
        const string mono = "/usr/share/fonts/noto/NotoSansMono-Regular.ttf";
        Mono = File.Exists(mono) ? io.Fonts.AddFontFromFileTTF(mono, 16f) : io.Fonts.AddFontDefault();
        Theme();

        Pass = new UiPass(gpu);
        _platform = new GlfwBackend(window.Native, GlfwBackend.ClientApi.Vulkan);
        _renderer = new VulkanBackend(
            new VulkanBackend.CreateInfo
            {
                Device = gpu.Device,
                GraphicsQueue = gpu.Queue,
                RenderPass = Pass.RenderPass,
                MinImageCount = gpu.MinImageCount,
                ImageCount = Math.Max(gpu.MinImageCount, gpu.ImageViews.Count),
                SampleCount = VkSampleCountFlags._1Bit,
                DescriptorPoolSize = 64,
            }
        );
    }

    /// <summary>Dark, with bronze as the accent.</summary>
    static void Theme()
    {
        ImGui.StyleColorsDark();
        var style = ImGui.GetStyle();
        style.WindowRounding = 4;
        style.FrameRounding = 3;
        style.TabRounding = 3;
        style.GrabRounding = 3;
        style.WindowPadding = new float2(8, 8);
        style.FramePadding = new float2(6, 4);
        var bronze = new float4(0.88f, 0.66f, 0.36f, 1f);
        var bronzeDim = new float4(0.43f, 0.33f, 0.19f, 1f);
        ref var colors = ref style.Colors;
        colors[(int)ImGuiCol.WindowBg] = new float4(0.07f, 0.08f, 0.10f, 1f);
        colors[(int)ImGuiCol.ChildBg] = new float4(0.07f, 0.08f, 0.10f, 1f);
        colors[(int)ImGuiCol.PopupBg] = new float4(0.09f, 0.10f, 0.12f, 1f);
        colors[(int)ImGuiCol.TitleBg] = new float4(0.07f, 0.08f, 0.10f, 1f);
        colors[(int)ImGuiCol.TitleBgActive] = new float4(0.11f, 0.12f, 0.15f, 1f);
        colors[(int)ImGuiCol.MenuBarBg] = new float4(0.09f, 0.10f, 0.12f, 1f);
        colors[(int)ImGuiCol.FrameBg] = new float4(0.14f, 0.16f, 0.19f, 1f);
        colors[(int)ImGuiCol.FrameBgHovered] = new float4(0.19f, 0.21f, 0.25f, 1f);
        colors[(int)ImGuiCol.FrameBgActive] = bronzeDim;
        colors[(int)ImGuiCol.Header] = new float4(0.17f, 0.19f, 0.23f, 1f);
        colors[(int)ImGuiCol.HeaderHovered] = new float4(0.22f, 0.24f, 0.29f, 1f);
        colors[(int)ImGuiCol.HeaderActive] = bronzeDim;
        colors[(int)ImGuiCol.Button] = new float4(0.17f, 0.19f, 0.23f, 1f);
        colors[(int)ImGuiCol.ButtonHovered] = bronzeDim;
        colors[(int)ImGuiCol.ButtonActive] = bronze;
        colors[(int)ImGuiCol.Tab] = new float4(0.11f, 0.12f, 0.15f, 1f);
        colors[(int)ImGuiCol.TabHovered] = bronzeDim;
        colors[(int)ImGuiCol.TabSelected] = new float4(0.20f, 0.17f, 0.13f, 1f);
        colors[(int)ImGuiCol.TabSelectedOverline] = bronze;
        colors[(int)ImGuiCol.TabDimmed] = new float4(0.09f, 0.10f, 0.12f, 1f);
        colors[(int)ImGuiCol.TabDimmedSelected] = new float4(0.15f, 0.14f, 0.12f, 1f);
        colors[(int)ImGuiCol.TabDimmedSelectedOverline] = bronzeDim;
        colors[(int)ImGuiCol.TitleBgCollapsed] = new float4(0.07f, 0.08f, 0.10f, 1f);
        colors[(int)ImGuiCol.NavCursor] = bronze;
        colors[(int)ImGuiCol.CheckMark] = bronze;
        colors[(int)ImGuiCol.SliderGrab] = bronze;
        colors[(int)ImGuiCol.SliderGrabActive] = bronze;
        colors[(int)ImGuiCol.DockingPreview] = bronzeDim;
        colors[(int)ImGuiCol.SeparatorHovered] = bronzeDim;
        colors[(int)ImGuiCol.SeparatorActive] = bronze;
        colors[(int)ImGuiCol.ResizeGrip] = bronzeDim;
        colors[(int)ImGuiCol.TextSelectedBg] = bronzeDim;

        // ImGui colours are authored as screen (sRGB) colours, but the swapchain is sRGB, which
        // encodes what's drawn: hand ImGui linear values so they come out as authored
        for (var i = 0; i < (int)ImGuiCol.COUNT; i++)
            colors[i] = Linear(colors[i]);
    }

    /// <summary>A screen colour converted for drawing through ImGui onto the sRGB swapchain.</summary>
    public static float4 Linear(float4 c) => new(MathF.Pow(c.X, 2.2f), MathF.Pow(c.Y, 2.2f), MathF.Pow(c.Z, 2.2f), c.W);

    public static float4 Color(float r, float g, float b, float a = 1f) => Linear(new float4(r, g, b, a));

    public void NewFrame()
    {
        // C# strings passed to ImGui are copied into this arena; last frame's are no longer needed
        ImString.SharedStorage.Reset();
        _platform.NewFrame();
        _renderer.NewFrame();
        ImGui.NewFrame();
    }

    /// <summary>Finishes the UI and records it into the frame, over the swapchain image.</summary>
    public void Render(CommandBuffer cmd)
    {
        ImGui.Render();
        Pass.Begin(cmd, new VkClearColorValue { Float32 = new float4(0.05f, 0.055f, 0.065f, 1f) });
        _renderer.RenderDrawData(ImGui.GetDrawData(), cmd);
        UiPass.End(cmd);
    }

    public ImTextureRef AddTexture(VkSampler sampler, VkImageView view) =>
        _renderer.AddTexture(sampler, view, VkImageLayout.ShaderReadOnlyOptimal);

    public void RemoveTexture(ImTextureRef texture) => _renderer.RemoveTexture(texture);

    public void Dispose()
    {
        _renderer.Dispose();
        _platform.Dispose();
        Pass.Dispose();
        ImGui.DestroyContext();
        Marshal.FreeCoTaskMem(_iniPath);
    }
}
