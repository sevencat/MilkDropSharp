using MilkDrop.ProjectM.Audio;
using MilkDrop.ProjectM.Renderer;

namespace MilkDrop.ProjectM.MilkdropPreset.Waveforms;

/// <summary>Milkdrop2077 追加波形：同 Line，振幅 ×4（对应 <c>Waveforms/Milkdrop2077Wave9.cpp</c>）。</summary>
public sealed class Milkdrop2077Wave9 : LineBase
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

        for (var i = 0; i < Samples; i++)
        {
            Wave1Vertices[i] = new RenderItem.Point
            {
                X = EdgeX + DistanceX * i + PerpetualDX * 0.35f * PcmDataL[i + SampleOffset],
                Y = EdgeY + DistanceY * i + PerpetualDY * 0.35f * PcmDataL[i + SampleOffset],
            };
        }
    }
}
