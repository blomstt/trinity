using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

namespace Trinity.Graphics;

/// <summary>
/// Loads shaders compiled to SPIR-V at build time (see Directory.Build.targets): <c>Load("crowd.vert")</c>
/// reads <c>Shaders/crowd.vert.spv</c> next to the executable. No compiler runs or ships with the game.
/// </summary>
public sealed class ShaderLibrary(GraphicsDevice gpu)
{
    public readonly string Folder = Path.Combine(AppContext.BaseDirectory, "Shaders");

    public VkShaderModule Load(string name)
    {
        var path = Path.Combine(Folder, name + ".spv");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Shader {name}.spv missing; is Shaders/{name} in a project that this one references?", path);
        return ShaderModuleExtensions.FromSpirv(gpu.Device, File.ReadAllBytes(path), null);
    }
}
