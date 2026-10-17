using Brutal.ImGuiApi;
using Brutal.Numerics;

namespace Trinity.Editor;

enum Icon
{
    Node,
    Mesh,
    Character,
    Camera,
    Light,
    Instance,
    Script,
    Eye,
    EyeClosed,
    Lock,
    Folder,
    FolderOpen,
    File,
    Image,
    Code,
    Star,
    StarOutline,
    Plus,
    Link,
    Back,
    Forward,
    Search,
    Play,
    Edit,
    Rename,
    Duplicate,
    Tag,
    Trash,
    Broom,
    Scan,
    Import,
    History,
    Sort,
    Up,
    Save,
}

/// <summary>
/// The editor's icons, drawn as vector shapes with ImGui's draw list: no image files to ship or load,
/// and they stay sharp at any size. Each draws into a square.
/// </summary>
static class Icons
{
    public static readonly float4 NodeColor = ImGuiHost.Color(0.99f, 0.50f, 0.50f);
    public static readonly float4 FolderColor = ImGuiHost.Color(0.45f, 0.68f, 0.98f);
    public static readonly float4 FileColor = ImGuiHost.Color(0.78f, 0.80f, 0.84f);
    public static readonly float4 Dim = ImGuiHost.Color(0.50f, 0.53f, 0.58f);
    public static readonly float4 Gold = ImGuiHost.Color(0.95f, 0.78f, 0.35f);

    /// <summary>An icon on the current line, the height of a line of text.</summary>
    public static void Inline(Icon icon, float4 color)
    {
        var s = ImGui.GetTextLineHeight();
        var p = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new float2(s, s));
        Draw(ImGui.GetWindowDrawList(), icon, p, s, color);
    }

    /// <summary>A square button showing just an icon, highlighted on hover.</summary>
    public static bool Button(string id, Icon icon, float4 color, string? tooltip = null, float size = 0)
    {
        size = size > 0 ? size : ImGui.GetFrameHeight();
        var p = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton(id, new float2(size, size));
        var dl = ImGui.GetWindowDrawList();
        if (ImGui.IsItemHovered())
        {
            dl.AddRectFilled(p, new float2(p.X + size, p.Y + size), ImGui.GetColorU32(ImGuiCol.ButtonHovered), 3);
            if (tooltip != null)
                ImGui.SetTooltip(tooltip);
        }
        var pad = size * 0.18f;
        Draw(dl, icon, new float2(p.X + pad, p.Y + pad), size - 2 * pad, color);
        return clicked;
    }

    /// <summary>A normal button with an icon before its label.</summary>
    public static bool TextButton(string label, Icon icon, float width = 0, float4? color = null)
    {
        var h = ImGui.GetFrameHeight();
        var clicked = ImGui.Button("      " + label, new float2(width, 0));
        var min = ImGui.GetItemRectMin();
        var pad = h * 0.22f;
        Draw(ImGui.GetWindowDrawList(), icon, new float2(min.X + 8, min.Y + pad), h - 2 * pad, color ?? FileColor);
        return clicked;
    }

    public static void Draw(ImDrawListPtr dl, Icon icon, float2 p, float s, float4 color)
    {
        var c = ImGui.GetColorU32(color);
        var t = MathF.Max(1f, s / 10f); // line thickness
        float2 P(float x, float y) => new(p.X + x * s, p.Y + y * s);
        switch (icon)
        {
            case Icon.Node:
                dl.AddCircle(P(0.5f, 0.5f), s * 0.36f, c, 0, t * 1.4f);
                break;
            case Icon.Mesh:
                // A box seen from above a corner
                dl.AddQuadFilled(P(0.5f, 0.08f), P(0.92f, 0.3f), P(0.5f, 0.52f), P(0.08f, 0.3f), c);
                dl.AddQuad(P(0.08f, 0.3f), P(0.5f, 0.52f), P(0.5f, 0.94f), P(0.08f, 0.72f), c, t);
                dl.AddQuad(P(0.5f, 0.52f), P(0.92f, 0.3f), P(0.92f, 0.72f), P(0.5f, 0.94f), c, t);
                break;
            case Icon.Character:
                dl.AddCircleFilled(P(0.5f, 0.2f), s * 0.16f, c);
                dl.AddLine(P(0.5f, 0.36f), P(0.5f, 0.66f), c, t * 1.6f);
                dl.AddLine(P(0.2f, 0.46f), P(0.8f, 0.46f), c, t * 1.4f);
                dl.AddLine(P(0.5f, 0.64f), P(0.28f, 0.95f), c, t * 1.4f);
                dl.AddLine(P(0.5f, 0.64f), P(0.72f, 0.95f), c, t * 1.4f);
                break;
            case Icon.Camera:
                dl.AddRectFilled(P(0.05f, 0.3f), P(0.65f, 0.78f), c, s * 0.08f);
                dl.AddTriangleFilled(P(0.62f, 0.54f), P(0.95f, 0.3f), P(0.95f, 0.78f), c);
                dl.AddCircleFilled(P(0.22f, 0.2f), s * 0.11f, c);
                dl.AddCircleFilled(P(0.47f, 0.2f), s * 0.11f, c);
                break;
            case Icon.Light:
                dl.AddCircleFilled(P(0.5f, 0.5f), s * 0.2f, c);
                for (var i = 0; i < 8; i++)
                {
                    var a = i * MathF.PI / 4;
                    var (x, y) = (MathF.Cos(a), MathF.Sin(a));
                    dl.AddLine(P(0.5f + x * 0.3f, 0.5f + y * 0.3f), P(0.5f + x * 0.46f, 0.5f + y * 0.46f), c, t * 1.3f);
                }
                break;
            case Icon.Instance:
                // A clapperboard
                dl.AddRectFilled(P(0.08f, 0.4f), P(0.92f, 0.9f), c, s * 0.06f);
                dl.AddQuadFilled(P(0.08f, 0.16f), P(0.92f, 0.16f), P(0.92f, 0.32f), P(0.08f, 0.32f), c);
                for (var i = 0; i < 3; i++)
                    dl.AddLine(P(0.22f + i * 0.25f, 0.16f), P(0.32f + i * 0.25f, 0.32f), ImGui.GetColorU32(ImGuiCol.WindowBg), t * 1.3f);
                break;
            case Icon.Script:
                dl.AddRect(P(0.18f, 0.06f), P(0.82f, 0.94f), c, s * 0.08f, default, t * 1.2f);
                for (var i = 0; i < 3; i++)
                    dl.AddLine(P(0.32f, 0.3f + i * 0.2f), P(0.68f, 0.3f + i * 0.2f), c, t);
                break;
            case Icon.Eye:
                dl.AddEllipse(P(0.5f, 0.5f), new float2(s * 0.44f, s * 0.26f), c, 0, 0, t * 1.2f);
                dl.AddCircleFilled(P(0.5f, 0.5f), s * 0.13f, c);
                break;
            case Icon.EyeClosed:
                dl.AddEllipse(P(0.5f, 0.5f), new float2(s * 0.44f, s * 0.26f), c, 0, 0, t);
                dl.AddLine(P(0.12f, 0.88f), P(0.88f, 0.12f), c, t * 1.4f);
                break;
            case Icon.Lock:
                dl.AddRect(P(0.3f, 0.1f), P(0.7f, 0.6f), c, s * 0.2f, default, t * 1.4f);
                dl.AddRectFilled(P(0.16f, 0.42f), P(0.84f, 0.94f), c, s * 0.08f);
                break;
            case Icon.Folder:
            case Icon.FolderOpen:
                dl.AddRectFilled(P(0.04f, 0.16f), P(0.44f, 0.34f), c, s * 0.05f);
                if (icon == Icon.Folder)
                    dl.AddRectFilled(P(0.04f, 0.26f), P(0.96f, 0.86f), c, s * 0.07f);
                else
                {
                    dl.AddRect(P(0.04f, 0.26f), P(0.9f, 0.86f), c, s * 0.07f, default, t);
                    dl.AddQuadFilled(P(0.2f, 0.44f), P(0.98f, 0.44f), P(0.86f, 0.86f), P(0.06f, 0.86f), c);
                }
                break;
            case Icon.File:
                Page(dl, P, c, t);
                break;
            case Icon.Image:
                dl.AddRect(P(0.06f, 0.14f), P(0.94f, 0.86f), c, s * 0.06f, default, t * 1.2f);
                dl.AddTriangleFilled(P(0.12f, 0.8f), P(0.42f, 0.4f), P(0.66f, 0.8f), c);
                dl.AddTriangleFilled(P(0.5f, 0.8f), P(0.7f, 0.54f), P(0.88f, 0.8f), c);
                dl.AddCircleFilled(P(0.72f, 0.32f), s * 0.08f, c);
                break;
            case Icon.Code:
                Page(dl, P, c, t);
                dl.AddPolyline([P(0.42f, 0.42f), P(0.3f, 0.58f), P(0.42f, 0.74f)], c, default, t * 1.2f);
                dl.AddPolyline([P(0.58f, 0.42f), P(0.7f, 0.58f), P(0.58f, 0.74f)], c, default, t * 1.2f);
                break;
            case Icon.Star:
            case Icon.StarOutline:
                Span<float2> points = stackalloc float2[10];
                for (var i = 0; i < 10; i++)
                {
                    var a = -MathF.PI / 2 + i * MathF.PI / 5;
                    var r = i % 2 == 0 ? 0.48f : 0.2f;
                    points[i] = P(0.5f + MathF.Cos(a) * r, 0.54f + MathF.Sin(a) * r);
                }
                if (icon == Icon.Star)
                {
                    // The inner pentagon, then a triangle for each point
                    Span<float2> inner = [points[1], points[3], points[5], points[7], points[9]];
                    dl.AddConvexPolyFilled(inner, c);
                    for (var i = 0; i < 10; i += 2)
                        dl.AddTriangleFilled(points[i], points[(i + 1) % 10], points[(i + 9) % 10], c);
                }
                else
                    dl.AddPolyline(points, c, ImDrawFlags.Closed, t * 1.2f);
                break;
            case Icon.Plus:
                dl.AddLine(P(0.5f, 0.1f), P(0.5f, 0.9f), c, t * 1.6f);
                dl.AddLine(P(0.1f, 0.5f), P(0.9f, 0.5f), c, t * 1.6f);
                break;
            case Icon.Link:
                dl.AddEllipse(P(0.36f, 0.62f), new float2(s * 0.3f, s * 0.16f), c, -MathF.PI / 4, 0, t * 1.4f);
                dl.AddEllipse(P(0.64f, 0.38f), new float2(s * 0.3f, s * 0.16f), c, -MathF.PI / 4, 0, t * 1.4f);
                break;
            case Icon.Back:
                dl.AddPolyline([P(0.66f, 0.12f), P(0.3f, 0.5f), P(0.66f, 0.88f)], c, default, t * 1.6f);
                break;
            case Icon.Forward:
                dl.AddPolyline([P(0.34f, 0.12f), P(0.7f, 0.5f), P(0.34f, 0.88f)], c, default, t * 1.6f);
                break;
            case Icon.Search:
                dl.AddCircle(P(0.42f, 0.42f), s * 0.3f, c, 0, t * 1.5f);
                dl.AddLine(P(0.64f, 0.64f), P(0.94f, 0.94f), c, t * 2f);
                break;
            case Icon.Play:
                dl.AddTriangleFilled(P(0.2f, 0.1f), P(0.9f, 0.5f), P(0.2f, 0.9f), c);
                break;
            case Icon.Edit:
                dl.AddLine(P(0.2f, 0.8f), P(0.8f, 0.2f), c, t * 2.4f);
                dl.AddTriangleFilled(P(0.08f, 0.92f), P(0.14f, 0.68f), P(0.32f, 0.86f), c);
                break;
            case Icon.Rename:
                dl.AddLine(P(0.5f, 0.12f), P(0.5f, 0.88f), c, t * 1.5f);
                dl.AddLine(P(0.3f, 0.12f), P(0.7f, 0.12f), c, t * 1.5f);
                dl.AddLine(P(0.3f, 0.88f), P(0.7f, 0.88f), c, t * 1.5f);
                break;
            case Icon.Duplicate:
                dl.AddRect(P(0.08f, 0.08f), P(0.62f, 0.66f), c, s * 0.06f, default, t * 1.2f);
                dl.AddRectFilled(P(0.38f, 0.36f), P(0.92f, 0.92f), c, s * 0.06f);
                break;
            case Icon.Tag:
                dl.AddQuadFilled(P(0.06f, 0.06f), P(0.5f, 0.06f), P(0.94f, 0.5f), P(0.5f, 0.94f), c);
                dl.AddTriangleFilled(P(0.06f, 0.06f), P(0.5f, 0.94f), P(0.06f, 0.5f), c);
                dl.AddCircleFilled(P(0.28f, 0.28f), s * 0.08f, ImGui.GetColorU32(ImGuiCol.WindowBg));
                break;
            case Icon.Trash:
                dl.AddLine(P(0.1f, 0.2f), P(0.9f, 0.2f), c, t * 1.4f);
                dl.AddRect(P(0.36f, 0.06f), P(0.64f, 0.2f), c, 0, default, t);
                dl.AddQuad(P(0.18f, 0.28f), P(0.82f, 0.28f), P(0.74f, 0.94f), P(0.26f, 0.94f), c, t * 1.2f);
                dl.AddLine(P(0.42f, 0.4f), P(0.44f, 0.82f), c, t);
                dl.AddLine(P(0.58f, 0.4f), P(0.56f, 0.82f), c, t);
                break;
            case Icon.Broom:
                dl.AddLine(P(0.85f, 0.08f), P(0.5f, 0.55f), c, t * 1.5f);
                dl.AddTriangleFilled(P(0.38f, 0.45f), P(0.62f, 0.62f), P(0.18f, 0.94f), c);
                break;
            case Icon.Scan:
                dl.AddCircle(P(0.42f, 0.42f), s * 0.3f, c, 0, t * 1.5f);
                dl.AddLine(P(0.64f, 0.64f), P(0.94f, 0.94f), c, t * 2f);
                dl.AddLine(P(0.28f, 0.42f), P(0.56f, 0.42f), c, t);
                break;
            case Icon.Import:
                dl.AddLine(P(0.5f, 0.06f), P(0.5f, 0.66f), c, t * 1.6f);
                dl.AddPolyline([P(0.26f, 0.44f), P(0.5f, 0.68f), P(0.74f, 0.44f)], c, default, t * 1.6f);
                dl.AddPolyline([P(0.1f, 0.62f), P(0.1f, 0.92f), P(0.9f, 0.92f), P(0.9f, 0.62f)], c, default, t * 1.4f);
                break;
            case Icon.Up:
                dl.AddPolyline([P(0.12f, 0.66f), P(0.5f, 0.28f), P(0.88f, 0.66f)], c, default, t * 1.6f);
                break;
            case Icon.Save:
                dl.AddRect(P(0.1f, 0.1f), P(0.9f, 0.9f), c, s * 0.08f, default, t * 1.3f);
                dl.AddRectFilled(P(0.28f, 0.1f), P(0.72f, 0.36f), c);
                dl.AddRectFilled(P(0.24f, 0.56f), P(0.76f, 0.9f), c, s * 0.04f);
                break;
            case Icon.Sort:
                dl.AddLine(P(0.1f, 0.22f), P(0.9f, 0.22f), c, t * 1.5f);
                dl.AddLine(P(0.1f, 0.5f), P(0.66f, 0.5f), c, t * 1.5f);
                dl.AddLine(P(0.1f, 0.78f), P(0.42f, 0.78f), c, t * 1.5f);
                break;
            case Icon.History:
                dl.AddCircle(P(0.5f, 0.5f), s * 0.42f, c, 0, t * 1.3f);
                dl.AddLine(P(0.5f, 0.5f), P(0.5f, 0.22f), c, t * 1.4f);
                dl.AddLine(P(0.5f, 0.5f), P(0.72f, 0.62f), c, t * 1.4f);
                break;
        }
    }

    static void Page(ImDrawListPtr dl, Func<float, float, float2> P, ImColor8 c, float t)
    {
        dl.AddPolyline([P(0.18f, 0.06f), P(0.6f, 0.06f), P(0.82f, 0.28f), P(0.82f, 0.94f), P(0.18f, 0.94f)], c, ImDrawFlags.Closed, t * 1.2f);
        dl.AddPolyline([P(0.6f, 0.06f), P(0.6f, 0.28f), P(0.82f, 0.28f)], c, default, t);
    }

    /// <summary>The icon for a file, by its kind.</summary>
    public static (Icon, float4) ForFile(string path)
    {
        if (path.EndsWith(".scene.json", StringComparison.OrdinalIgnoreCase))
            return (Icon.Instance, NodeColor);
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".glb" or ".gltf" => (Icon.Mesh, ImGuiHost.Color(0.95f, 0.66f, 0.52f)),
            ".png" or ".jpg" or ".jpeg" or ".ktx2" or ".webp" => (Icon.Image, ImGuiHost.Color(0.62f, 0.84f, 0.62f)),
            ".cs" => (Icon.Code, ImGuiHost.Color(0.66f, 0.56f, 0.92f)),
            ".glsl" or ".vert" or ".frag" or ".comp" => (Icon.Code, ImGuiHost.Color(0.5f, 0.78f, 0.86f)),
            _ => (Icon.File, FileColor),
        };
    }
}
