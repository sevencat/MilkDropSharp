using MilkDrop.ProjectM.Audio;
using MilkDrop.ProjectM.Renderer;

namespace MilkDrop.ProjectM.MilkdropPreset.Waveforms;

/// <summary>随时间绕圈的 X-Y 振荡螺旋（对应 <c>Waveforms/XYOscillationSpiral.cpp</c>）。</summary>
public sealed class XYOscillationSpiral : WaveformMath
{
    public override bool IsLoop => true;

    protected override bool UsesNormalizedMysteryParam => true;

    protected override void GenerateVertices(PresetState presetState, PerFrameContext presetPerFrameContext)
    {
        Samples = AudioConstants.WaveformSamples / 2;

        Array.Resize(ref Wave1Vertices, Samples);

        for (var i = 0; i < Samples; i++)
        {
            float radius = 0.53f + 0.43f * PcmDataR[i] + MysteryWaveParam;
            float angle = PcmDataL[i + 32] * 1.57f + presetState.RenderContext.Time * 2.3f;

            Wave1Vertices[i] = new RenderItem.Point
            {
                X = radius * MathF.Cos(angle) * AspectY + WaveX,
                Y = radius * MathF.Sin(angle) * AspectX + WaveY,
            };
        }
    }
}
