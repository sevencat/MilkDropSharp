namespace MilkDrop.ProjectM.Audio;

/// <summary>渲染一帧所需的全部音频数据（波形、频谱、节拍强度）。</summary>
/// <remarks>对应 <c>Audio/FrameAudioData.hpp:17-35</c>。</remarks>
public sealed class FrameAudioData
{
    /// <summary>低频段强度（相对上一帧，围绕 1.0 波动）。</summary>
    public float Bass { get; set; }

    /// <summary>低频段的平滑值。</summary>
    public float BassAtt { get; set; }

    public float Mid { get; set; }

    public float MidAtt { get; set; }

    public float Treb { get; set; }

    public float TrebAtt { get; set; }

    /// <summary>三频段相对强度的平均值。</summary>
    public float Vol { get; set; }

    /// <summary>三频段平滑值的平均值。</summary>
    public float VolAtt { get; set; }

    /// <summary>左声道波形，只有前 <see cref="AudioConstants.WaveformSamples"/> 个样本有效。</summary>
    public float[] WaveformLeft { get; } = new float[AudioConstants.WaveformSamples];

    public float[] WaveformRight { get; } = new float[AudioConstants.WaveformSamples];

    public float[] SpectrumLeft { get; } = new float[AudioConstants.SpectrumSamples];

    public float[] SpectrumRight { get; } = new float[AudioConstants.SpectrumSamples];

    /// <summary>
    /// 把另一份音频数据拷进来（对应 C++ 的值赋值 <c>m_state.audioData = audioData</c>）。
    /// </summary>
    public void CopyFrom(FrameAudioData other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (ReferenceEquals(this, other))
        {
            return;
        }

        Bass = other.Bass;
        BassAtt = other.BassAtt;
        Mid = other.Mid;
        MidAtt = other.MidAtt;
        Treb = other.Treb;
        TrebAtt = other.TrebAtt;
        Vol = other.Vol;
        VolAtt = other.VolAtt;

        other.WaveformLeft.CopyTo(WaveformLeft, 0);
        other.WaveformRight.CopyTo(WaveformRight, 0);
        other.SpectrumLeft.CopyTo(SpectrumLeft, 0);
        other.SpectrumRight.CopyTo(SpectrumRight, 0);
    }
}
