using MilkDrop.ProjectM.Audio;
using MilkDrop.ProjectM.Renderer;

namespace MilkDrop.ProjectM.MilkdropPreset.Waveforms;

/// <summary>Milkdrop2077 追加波形：X 形（对应 <c>Waveforms/Milkdrop2077WaveX.cpp</c>）。</summary>
public sealed class Milkdrop2077WaveX : LineBase
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

        ClipWaveformEdges(-0.75f + MysteryWaveParam * 3.15f);

        for (var i = 0; i < Samples; i++)
        {
            Wave1Vertices[i] = new RenderItem.Point
            {
                X = EdgeX + DistanceX * i + PerpetualDX * 0.35f * PcmDataL[i + SampleOffset],
                Y = EdgeY + DistanceY * i + PerpetualDY * 0.35f * PcmDataL[i + SampleOffset],
            };
        }

        ClipWaveformEdges(0.75f + MysteryWaveParam * 3.15f);

        for (var i = 0; i < Samples; i++)
        {
            Wave2Vertices[i] = new RenderItem.Point
            {
                X = EdgeX + DistanceX * i + PerpetualDX * 0.35f * PcmDataR[i + SampleOffset],
                Y = EdgeY + DistanceY * i + PerpetualDY * 0.35f * PcmDataR[i + SampleOffset],
            };
        }
    }
}
