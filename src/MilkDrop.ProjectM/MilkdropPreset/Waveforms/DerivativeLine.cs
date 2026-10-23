using MilkDrop.ProjectM.Audio;
using MilkDrop.ProjectM.Renderer;

namespace MilkDrop.ProjectM.MilkdropPreset.Waveforms;

/// <summary>水平"脚本线"，左声道（对应 <c>Waveforms/DerivativeLine.cpp</c>）。</summary>
public sealed class DerivativeLine : WaveformMath
{
    protected override bool UsesNormalizedMysteryParam => true;

    protected override void GenerateVertices(PresetState presetState, PerFrameContext presetPerFrameContext)
    {
        Samples = AudioConstants.WaveformSamples;

        if (Samples > presetState.RenderContext.ViewportSizeX / 3)
        {
            Samples /= 3;
        }

        Array.Resize(ref Wave1Vertices, Samples);

        int sampleOffset = (AudioConstants.WaveformSamples - Samples) / 2;

        float w1 = 0.45f + 0.5f * (MysteryWaveParam * 0.5f + 0.5f);
        float w2 = 1.0f - w1;

        float inverseSamples = 1.0f / Samples;

        for (var i = 0; i < Samples; i++)
        {
            float x = -1.0f + 2.0f * (i * inverseSamples) + WaveX;
            float y = PcmDataL[i + sampleOffset] * 0.47f + WaveY;
            x += PcmDataR[i + 25 + sampleOffset] * 0.44f;

            // 惯性项。
            if (i > 1)
            {
                x = x * w2 + w1 * (Wave1Vertices[i - 1].X * 2.0f - Wave1Vertices[i - 2].X);
                y = y * w2 + w1 * (Wave1Vertices[i - 1].Y * 2.0f - Wave1Vertices[i - 2].Y);
            }

            Wave1Vertices[i] = new RenderItem.Point { X = x, Y = y };
        }
    }
}
