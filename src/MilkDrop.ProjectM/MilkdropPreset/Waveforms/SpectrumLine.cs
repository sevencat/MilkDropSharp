using MilkDrop.ProjectM.Renderer;

namespace MilkDrop.ProjectM.MilkdropPreset.Waveforms;

/// <summary>用频谱数据的直线（对应 <c>Waveforms/SpectrumLine.cpp</c>）。</summary>
public sealed class SpectrumLine : LineBase
{
    protected override bool IsSpectrumWave => true;

    protected override void GenerateVertices(PresetState presetState, PerFrameContext presetPerFrameContext)
    {
        Samples = 256;

        Array.Resize(ref Wave1Vertices, Samples);

        ClipWaveformEdges(1.57f * MysteryWaveParam);

        for (var i = 0; i < Samples; i++)
        {
            float f = 0.1f * MathF.Log(PcmDataL[i * 2] + PcmDataL[i * 2 + 1]);

            Wave1Vertices[i] = new RenderItem.Point
            {
                X = EdgeX + DistanceX * i + PerpetualDX * f,
                Y = EdgeY + DistanceY * i + PerpetualDY * f,
            };
        }
    }
}
