namespace MilkDrop.ProjectM.Renderer;

/// <summary>每帧变化的全局渲染参数。</summary>
/// <remarks>
/// 对应 <c>Renderer/RenderContext.hpp:15-33</c>。
/// C++ 里还有一个 <c>textureManager</c> 裸指针，等 P2 把 <c>TextureManager</c> 移植过来再补字段。
/// </remarks>
public sealed class RenderContext
{
    /// <summary>预设启动以来的秒数。</summary>
    public float Time { get; set; }

    /// <summary>已渲染帧数。</summary>
    public int Frame { get; set; }

    /// <summary>帧率。</summary>
    public float Fps { get; set; }

    /// <summary>预设进度（0..1）。</summary>
    public float Progress { get; set; }

    /// <summary>视口宽度（像素）。</summary>
    public int ViewportSizeX { get; set; }

    /// <summary>视口高度（像素）。</summary>
    public int ViewportSizeY { get; set; }

    public float AspectX { get; set; } = 1f;

    public float AspectY { get; set; } = 1f;

    public float InvAspectX { get; set; } = 1f;

    public float InvAspectY { get; set; } = 1f;

    /// <summary>per-pixel/per-vertex 网格 X 分辨率。</summary>
    public int PerPixelMeshX { get; set; } = 64;

    /// <summary>per-pixel/per-vertex 网格 Y 分辨率。</summary>
    public int PerPixelMeshY { get; set; } = 48;
}
