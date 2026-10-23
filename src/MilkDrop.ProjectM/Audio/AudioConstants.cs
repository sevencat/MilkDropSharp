namespace MilkDrop.ProjectM.Audio;

/// <summary>音频缓冲尺寸常量。</summary>
/// <remarks>对应 <c>Audio/AudioConstants.hpp:8-13</c>。</remarks>
public static class AudioConstants
{
    /// <summary>环形输入缓冲/波形分析缓冲的样本数（<c>AudioBufferSamples</c>）。</summary>
    public const int AudioBufferSamples = 576;

    /// <summary>每帧可用于渲染的波形样本数（<c>WaveformSamples</c>），是 <see cref="AudioBufferSamples"/> 的前缀。</summary>
    public const int WaveformSamples = 480;

    /// <summary>频谱分析输出的样本数（<c>SpectrumSamples</c>）。</summary>
    public const int SpectrumSamples = 512;
}
