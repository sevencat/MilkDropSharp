using MilkDrop.ProjectM.Audio;
using MilkDrop.ProjectM.Renderer;

namespace MilkDrop.ProjectM.MilkdropPreset.Waveforms;

/// <summary>爆炸式复合图案（对应 <c>Waveforms/ExplosiveHash.cpp</c>）。</summary>
public sealed class ExplosiveHash : WaveformMath
{
    protected override void GenerateVertices(PresetState presetState, PerFrameContext presetPerFrameContext)
    {
        Samples = AudioConstants.WaveformSamples;

        Array.Resize(ref Wave1Vertices, Samples);

        float cosineRotation = MathF.Cos(presetState.RenderContext.Time * 0.3f);
        float sineRotation = MathF.Sin(presetState.RenderContext.Time * 0.3f);

        for (var i = 0; i < Samples; i++)
        {
            float x0 = PcmDataR[i] * PcmDataL[i + 32] + PcmDataL[i] * PcmDataR[i + 32];
            float y0 = PcmDataR[i] * PcmDataR[i] - PcmDataL[i + 32] * PcmDataL[i + 32];

            Wave1Vertices[i] = new RenderItem.Point
            {
                X = (x0 * cosineRotation - y0 * sineRotation) * AspectY + WaveX,
                Y = (x0 * sineRotation + y0 * cosineRotation) * AspectX + WaveY,
            };
        }
    }
}
