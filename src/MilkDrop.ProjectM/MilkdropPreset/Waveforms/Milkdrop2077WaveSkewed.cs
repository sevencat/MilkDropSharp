using MilkDrop.ProjectM.Audio;
using MilkDrop.ProjectM.Renderer;

namespace MilkDrop.ProjectM.MilkdropPreset.Waveforms;

/// <summary>Milkdrop2077 追加波形：斜切形（对应 <c>Waveforms/Milkdrop2077WaveSkewed.cpp</c>）。</summary>
public sealed class Milkdrop2077WaveSkewed : WaveformMath
{
    protected override void GenerateVertices(PresetState presetState, PerFrameContext presetPerFrameContext)
    {
        Samples = AudioConstants.WaveformSamples / 2;

        Array.Resize(ref Wave1Vertices, Samples);

        float alpha = (float)presetPerFrameContext.WaveA.Value * 1.25f;
        if (presetState.ModWaveAlphaByVolume)
        {
            alpha *= presetState.AudioData.Vol;
        }

        alpha = Math.Clamp(alpha, 0.0f, 1.0f);

        for (var i = 0; i < Samples; i++)
        {
            float rad = 0.63f + 0.23f * PcmDataR[i] + MysteryWaveParam;
            float ang = PcmDataL[i + 32] * 0.9f + presetState.RenderContext.Time * 3.3f;

            Wave1Vertices[i] = new RenderItem.Point
            {
                X = rad * MathF.Cos(ang + alpha) * AspectY + WaveX,
                Y = rad * MathF.Sin(ang) * AspectX + WaveY,
            };
        }
    }
}
