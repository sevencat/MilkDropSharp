using MilkDrop.ProjectM.Audio;
using MilkDrop.ProjectM.Renderer;

namespace MilkDrop.ProjectM.MilkdropPreset.Waveforms;

/// <summary>双声道直线（对应 <c>Waveforms/DoubleLine.cpp</c>）。</summary>
public sealed class DoubleLine : LineBase
{
    protected override void GenerateVertices(PresetState presetState, PerFrameContext presetPerFrameContext)
    {
        Samples = AudioConstants.WaveformSamples / 2;

        if (Samples > presetState.RenderContext.ViewportSizeX / 3)
        {
            Samples /= 3;
        }

        Array.Resize(ref Wave1Vertices, Samples);
        Array.Resize(ref Wave2Vertices, Samples);

        ClipWaveformEdges(1.57f * MysteryWaveParam);

        float separation = MathF.Pow(WaveY * 0.5f + 0.5f, 2.0f);

        for (var i = 0; i < Samples; i++)
        {
            Wave1Vertices[i] = new RenderItem.Point
            {
                X = EdgeX + DistanceX * i + PerpetualDX * (0.25f * PcmDataL[i + SampleOffset] + separation),
                Y = EdgeY + DistanceY * i + PerpetualDY * (0.25f * PcmDataL[i + SampleOffset] + separation),
            };

            Wave2Vertices[i] = new RenderItem.Point
            {
                X = EdgeX + DistanceX * i + PerpetualDX * (0.25f * PcmDataR[i + SampleOffset] - separation),
                Y = EdgeY + DistanceY * i + PerpetualDY * (0.25f * PcmDataR[i + SampleOffset] - separation),
            };
        }
    }
}
