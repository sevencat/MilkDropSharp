using MilkDrop.ProjectM.Audio;
using MilkDrop.ProjectM.Renderer;

namespace MilkDrop.ProjectM.MilkdropPreset.Waveforms;

/// <summary>
/// 主波形顶点计算的基类：取音频数据、按预设平滑系数滤波、再交给各模式的
/// <see cref="GenerateVertices"/> 生成顶点，最后统一做一次雅可比平滑。
/// </summary>
/// <remarks>对应 <c>MilkdropPreset/Waveforms/WaveformMath.hpp</c> 与 <c>WaveformMath.cpp</c>。</remarks>
public abstract class WaveformMath
{
    /// <summary>波形是否画成闭合线环（<c>GL_LINE_LOOP</c>）而不是折线。</summary>
    public virtual bool IsLoop => false;

    /// <summary>是否使用频谱数据（否则用示波器数据）。</summary>
    protected virtual bool IsSpectrumWave => false;

    /// <summary>是否要把 <c>wave_mystery</c> 归一化到 <c>[-1, 1]</c>。</summary>
    protected virtual bool UsesNormalizedMysteryParam => false;

    /// <summary>本帧的样本数（由各模式在 <see cref="GenerateVertices"/> 里设定）。</summary>
    protected int Samples;

    protected readonly float[] PcmDataL = new float[Constants.WaveformMaxPoints];
    protected readonly float[] PcmDataR = new float[Constants.WaveformMaxPoints];

    protected float AspectX = 1.0f;
    protected float AspectY = 1.0f;

    protected float WaveX = 0.5f;
    protected float WaveY = 0.5f;

    protected float MysteryWaveParam = 1.0f;

    protected RenderItem.Point[] Wave1Vertices = [];
    protected RenderItem.Point[] Wave2Vertices = [];

    /// <summary>
    /// 计算本帧的两组波形顶点（第二组可能为空，取决于模式）。
    /// </summary>
    /// <remarks>对应 <c>WaveformMath::GetVertices</c>。</remarks>
    public RenderItem.Point[][] GetVertices(PresetState presetState, PerFrameContext presetPerFrameContext)
    {
        ArgumentNullException.ThrowIfNull(presetState);
        ArgumentNullException.ThrowIfNull(presetPerFrameContext);

        FrameAudioData audioData = presetState.AudioData;

        if (IsSpectrumWave)
        {
            Array.Copy(audioData.SpectrumLeft, 0, PcmDataL, 0, AudioConstants.SpectrumSamples);
            Array.Copy(audioData.SpectrumRight, 0, PcmDataR, 0, AudioConstants.SpectrumSamples);
        }
        else
        {
            Array.Copy(audioData.WaveformLeft, 0, PcmDataL, 0, AudioConstants.WaveformSamples);
            Array.Copy(audioData.WaveformRight, 0, PcmDataR, 0, AudioConstants.WaveformSamples);
        }

        // 缩放 + 平滑（一阶 IIR，alpha = waveSmoothing）。
        float scale = presetState.WaveScale / 128.0f;

        // 第一个样本不参与混合，直接缩放。
        PcmDataL[0] *= scale;
        PcmDataR[0] *= scale;

        float mix2 = presetState.WaveSmoothing;
        float mix1 = scale * (1.0f - mix2);

        for (var i = 1; i < PcmDataL.Length; i++)
        {
            PcmDataL[i] = PcmDataL[i] * mix1 + PcmDataL[i - 1] * mix2;
            PcmDataR[i] = PcmDataR[i] * mix1 + PcmDataR[i - 1] * mix2;
        }

        // 宽高比修正。
        RenderContext renderContext = presetState.RenderContext;
        if (renderContext.ViewportSizeX > renderContext.ViewportSizeY)
        {
            AspectY = renderContext.ViewportSizeY / (float)renderContext.ViewportSizeX;
        }
        else
        {
            AspectX = renderContext.ViewportSizeX / (float)renderContext.ViewportSizeY;
        }

        MysteryWaveParam = (float)presetPerFrameContext.WaveMystery.Value;

        if (UsesNormalizedMysteryParam && (MysteryWaveParam < -1.0f || MysteryWaveParam > 1.0f))
        {
            MysteryWaveParam = MysteryWaveParam * 0.5f + 0.5f;
            MysteryWaveParam -= MathF.Floor(MysteryWaveParam);
            MysteryWaveParam = MathF.Abs(MysteryWaveParam);
            MysteryWaveParam = MysteryWaveParam * 2 - 1;
        }

        WaveX = 2.0f * (float)presetPerFrameContext.WaveX.Value - 1.0f;
        WaveY = 2.0f * (float)presetPerFrameContext.WaveY.Value - 1.0f;

        GenerateVertices(presetState, presetPerFrameContext);

        return
        [
            SmoothWave(Wave1Vertices),
            SmoothWave(Wave2Vertices),
        ];
    }

    /// <summary>各模式自己的顶点计算（不做平滑）。</summary>
    protected abstract void GenerateVertices(PresetState presetState, PerFrameContext presetPerFrameContext);

    /// <summary>
    /// 比线性更好的平滑：点数量大致翻倍（对应 <c>WaveformMath::SmoothWave</c>）。
    /// </summary>
    protected RenderItem.Point[] SmoothWave(RenderItem.Point[] inputVertices)
    {
        const float c1 = -0.15f;
        const float c2 = 1.15f;
        const float c3 = 1.15f;
        const float c4 = -0.15f;
        const float inverseSum = 1.0f / (c1 + c2 + c3 + c4);

        if (inputVertices.Length == 0)
        {
            return [];
        }

        var outputVertices = new RenderItem.Point[Samples * 2 - 1];

        var outputIndex = 0;
        var indexBelow = 0;
        var indexAbove2 = 1;

        for (var inputIndex = 0; inputIndex < Samples - 1; inputIndex++)
        {
            int indexAbove = indexAbove2;
            indexAbove2 = Math.Min(Samples - 1, inputIndex + 2);

            outputVertices[outputIndex] = inputVertices[inputIndex];
            outputVertices[outputIndex + 1].X =
                (c1 * inputVertices[indexBelow].X + c2 * inputVertices[inputIndex].X +
                 c3 * inputVertices[indexAbove].X + c4 * inputVertices[indexAbove2].X) * inverseSum;
            outputVertices[outputIndex + 1].Y =
                (c1 * inputVertices[indexBelow].Y + c2 * inputVertices[inputIndex].Y +
                 c3 * inputVertices[indexAbove].Y + c4 * inputVertices[indexAbove2].Y) * inverseSum;

            indexBelow = inputIndex;
            outputIndex += 2;
        }

        outputVertices[outputIndex] = inputVertices[Samples - 1];

        return outputVertices;
    }
}
