using System.Numerics;

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

    /// <summary>纹理管理器（C++ 里的 <c>textureManager</c> 裸指针）。</summary>
    public TextureManager? TextureManager { get; set; }

    /// <summary>
    /// 鼠标位置，供预设 shader 的兼容层 <c>mouse</c>（<c>_mouse</c> uniform）使用。
    /// </summary>
    /// <remarks>
    /// xy 是归一化到 <c>-1..1</c> 的光标坐标（与 <c>MilkDrop.Classic</c> 的
    /// <c>PerFrameContext.Mouse</c> 一致），zw 暂未使用。原版 projectM 没有这个变量，
    /// 只有 MilkDrop3 的预设会用。
    /// </remarks>
    public Vector4 Mouse { get; set; }

    /// <summary>
    /// 把另一份渲染上下文拷进本实例（对应 C++ 的值赋值 <c>m_state.renderContext = renderContext</c>，
    /// 因为预设持有自己的 <see cref="RenderContext"/> 副本）。
    /// </summary>
    public void CopyFrom(RenderContext other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (ReferenceEquals(this, other))
        {
            return;
        }

        Time = other.Time;
        Frame = other.Frame;
        Fps = other.Fps;
        Progress = other.Progress;
        ViewportSizeX = other.ViewportSizeX;
        ViewportSizeY = other.ViewportSizeY;
        AspectX = other.AspectX;
        AspectY = other.AspectY;
        InvAspectX = other.InvAspectX;
        InvAspectY = other.InvAspectY;
        PerPixelMeshX = other.PerPixelMeshX;
        PerPixelMeshY = other.PerPixelMeshY;
        TextureManager = other.TextureManager;
        Mouse = other.Mouse;
    }
}
