using MilkDrop.ProjectM.Audio;
using MilkDrop.ProjectM.Renderer;

namespace MilkDrop.ProjectM.MilkdropPreset.Waveforms;

/// <summary>圆形波形（对应 <c>Waveforms/Circle.cpp</c>）。</summary>
public sealed class Circle : WaveformMath
{
    public override bool IsLoop => true;

    protected override bool UsesNormalizedMysteryParam => true;

    protected override void GenerateVertices(PresetState presetState, PerFrameContext presetPerFrameContext)
    {
        Samples = AudioConstants.WaveformSamples / 2;

        Array.Resize(ref Wave1Vertices, Samples);

        int sampleOffset = (AudioConstants.WaveformSamples - Samples) / 2;

        float inverseSamples = 1.0f / Samples;

        for (var i = 0; i < Samples; i++)
        {
            float radius = 0.5f + 0.4f * PcmDataR[i + sampleOffset] + MysteryWaveParam;
            float angle = i * inverseSamples * 6.28f + presetState.RenderContext.Time * 0.2f;

            if (i < Samples / 10)
            {
                float mix = i / (Samples * 0.1f);
                mix = 0.5f - 0.5f * MathF.Cos(mix * 3.1416f);
                float radius2 = 0.5f + 0.4f * PcmDataR[i + Samples + sampleOffset] + MysteryWaveParam;
                radius = radius2 * (1.0f - mix) + radius * mix;
            }

            Wave1Vertices[i] = new RenderItem.Point
            {
                X = radius * MathF.Cos(angle) * AspectY + WaveX,
                Y = radius * MathF.Sin(angle) * AspectX + WaveY,
            };
        }
    }
}
