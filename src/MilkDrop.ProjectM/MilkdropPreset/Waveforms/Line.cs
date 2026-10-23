using MilkDrop.ProjectM.Audio;
using MilkDrop.ProjectM.Renderer;

namespace MilkDrop.ProjectM.MilkdropPreset.Waveforms;

/// <summary>可调角度的左声道直线，带时间对齐（对应 <c>Waveforms/Line.cpp</c>）。</summary>
public sealed class Line : LineBase
{
    protected override void GenerateVertices(PresetState presetState, PerFrameContext presetPerFrameContext)
    {
        Samples = AudioConstants.WaveformSamples / 2;

        if (Samples > presetState.RenderContext.ViewportSizeX / 3)
        {
            Samples /= 3;
        }

        Array.Resize(ref Wave1Vertices, Samples);

        ClipWaveformEdges(1.57f * MysteryWaveParam);

        for (var i = 0; i < Samples; i++)
        {
            Wave1Vertices[i] = new RenderItem.Point
            {
                X = EdgeX + DistanceX * i + PerpetualDX * 0.25f * PcmDataL[i + SampleOffset],
                Y = EdgeY + DistanceY * i + PerpetualDY * 0.25f * PcmDataL[i + SampleOffset],
            };
        }
    }
}
