namespace MilkDrop.ProjectM.Renderer;

/// <summary>
/// 转场需要的预设最小接口（对应 C++ 的 <c>Preset</c> 抽象基类的 <c>OutputTexture()</c>）。
/// </summary>
public interface IPreset
{
    /// <summary>本预设上一帧的输出纹理。</summary>
    Texture? OutputTexture();
}
