using MilkDrop.ProjectM.Audio;
using MilkDrop.ProjectM.Renderer;

namespace MilkDrop.ProjectM;

/// <summary>
/// 预设的抽象基类：以"渲染一帧"为核心，由具体格式（目前只有 Milkdrop）实现。
/// </summary>
/// <remarks>对应 <c>Preset.hpp:13-62</c>。</remarks>
public abstract class Preset : IPreset, IDisposable
{
    /// <summary>预设文件名（从 URL/文件加载时由工厂写入，流加载时为空）。</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>申请额外的预设资源，必须在第一帧之前调用一次。</summary>
    public abstract void Initialize(RenderContext renderContext);

    /// <summary>把预设渲染进当前帧缓冲。</summary>
    public abstract void RenderFrame(FrameAudioData audioData, RenderContext renderContext);

    /// <summary>当前输出纹理；逐帧可能变化，不要跨帧保存。</summary>
    public abstract Texture? OutputTexture();

    /// <summary>
    /// 把一张图像（通常是上一个预设的最后一帧）画进本预设作为初始画面。
    /// </summary>
    public abstract void DrawInitialImage(Texture? image, RenderContext renderContext);

    public virtual void Dispose()
    {
    }
}
