using MilkDrop.ProjectM.Audio;
using MilkDrop.ProjectM.Renderer;

namespace MilkDrop.ProjectM.MilkdropPreset.Waveforms;

/// <summary>
/// 居中的 spiro（对应 <c>Waveforms/CenteredSpiro.cpp</c>）。
/// <c>CenteredSpiro</c> 与 <c>CenteredSpiroVolume</c> 共用本实现，只有 alpha 计算不同。
/// </summary>
public sealed class CenteredSpiro : WaveformMath
{
    protected override void GenerateVertices(PresetState presetState, PerFrameContext presetPerFrameContext)
    {
        // alpha 由 Waveform.MaximizeColors 处理。
        Samples = AudioConstants.WaveformSamples;

        Array.Resize(ref Wave1Vertices, Samples);

        for (var i = 0; i < Samples; i++)
        {
            Wave1Vertices[i] = new RenderItem.Point
            {
                X = PcmDataR[i] * AspectY + WaveX,
                Y = PcmDataL[i + 32] * AspectX + WaveY,
            };
        }
    }
}
