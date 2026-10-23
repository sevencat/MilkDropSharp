using MilkDrop.ProjectM.Audio;
using MilkDrop.ProjectM.Renderer;

namespace MilkDrop.ProjectM.MilkdropPreset.Waveforms;

/// <summary>Milkdrop2077 追加波形：套索形（对应 <c>Waveforms/Milkdrop2077WaveLasso.cpp</c>）。</summary>
public sealed class Milkdrop2077WaveLasso : WaveformMath
{
    protected override void GenerateVertices(PresetState presetState, PerFrameContext presetPerFrameContext)
    {
        Samples = AudioConstants.WaveformSamples / 2;

        Array.Resize(ref Wave1Vertices, Samples);

        float time = presetState.RenderContext.Time;

        for (var sample = 0; sample < Samples; sample++)
        {
            float angle = PcmDataL[sample + 32] * 1.57f + time * 2.0f;

            Wave1Vertices[sample] = new RenderItem.Point
            {
                X = MathF.Cos(time) / 2.0f + MathF.Cos(angle * 2.0f + MathF.Tan(time / angle)),
                Y = MathF.Sin(time) * 2.0f * MathF.Sin(angle * 3.14f) * AspectX / 2.8f + WaveY,
            };
        }
    }
}
