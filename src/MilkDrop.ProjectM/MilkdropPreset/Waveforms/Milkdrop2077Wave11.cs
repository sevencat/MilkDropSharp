using MilkDrop.ProjectM.Audio;
using MilkDrop.ProjectM.Renderer;

namespace MilkDrop.ProjectM.MilkdropPreset.Waveforms;

/// <summary>Milkdrop2077 追加波形：双线（对应 <c>Waveforms/Milkdrop2077Wave11.cpp</c>）。</summary>
public sealed class Milkdrop2077Wave11 : LineBase
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

        ClipWaveformEdges(1.57f);

        for (var i = 0; i < Samples; i++)
        {
            Wave1Vertices[i] = new RenderItem.Point
            {
                X = EdgeX - 0.45f + DistanceX * i + PerpetualDX * 0.35f * PcmDataL[i + SampleOffset],
                Y = EdgeY + DistanceY * i + PerpetualDY * 0.35f * PcmDataL[i + SampleOffset],
            };

            Wave2Vertices[i] = new RenderItem.Point
            {
                X = EdgeX + 0.45f + DistanceX * i + PerpetualDX * 0.35f * PcmDataR[i + SampleOffset],
                Y = EdgeY + DistanceY * i + PerpetualDY * 0.35f * PcmDataR[i + SampleOffset],
            };
        }
    }
}
