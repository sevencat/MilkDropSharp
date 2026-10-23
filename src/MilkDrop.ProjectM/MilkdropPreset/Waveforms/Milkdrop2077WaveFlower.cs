using MilkDrop.ProjectM.Audio;
using MilkDrop.ProjectM.Renderer;

namespace MilkDrop.ProjectM.MilkdropPreset.Waveforms;

/// <summary>Milkdrop2077 追加波形：花形（对应 <c>Waveforms/Milkdrop2077WaveFlower.cpp</c>）。</summary>
public sealed class Milkdrop2077WaveFlower : WaveformMath
{
    public override bool IsLoop => true;

    protected override void GenerateVertices(PresetState presetState, PerFrameContext presetPerFrameContext)
    {
        Samples = AudioConstants.WaveformSamples / 2;

        Array.Resize(ref Wave1Vertices, Samples + 1);

        int sampleOffset = (AudioConstants.WaveformSamples - Samples) / 2;

        float invertedSamplesMinusOne = 1.0f / (Samples - 1);
        float tenthSamples = Samples * 0.1f;

        for (var sample = 0; sample < Samples; sample++)
        {
            float radius = 0.7f + 0.7f * PcmDataR[sample + sampleOffset] + MysteryWaveParam;
            float angle = sample * invertedSamplesMinusOne * 6.28f + presetState.RenderContext.Time * 0.2f;

            if (sample < (float)Samples / radius)
            {
                float mix = sample / tenthSamples;
                // 花形。
                mix = 0.7f - 0.7f * MathF.Cos(mix * 3.1416f);
                // 这里要把样本偏移减掉而不是加上：原始 Milkdrop2077 代码会越界读取。
                float radius2 = 0.7f + 0.7f * PcmDataR[sample + Samples - sampleOffset] + MysteryWaveParam;
                radius = radius2 * (1.0f - mix) + radius * mix * 0.25f;
            }

            Wave1Vertices[sample] = new RenderItem.Point
            {
                X = radius * MathF.Cos(angle * 3.1416f) * AspectY / 1.5f + WaveX * MathF.Cos(3.1416f),
                Y = radius * MathF.Sin(angle - presetState.RenderContext.Time / 3.0f) * AspectX / 1.5f + WaveY * MathF.Cos(3.1416f),
            };
        }
    }
}
