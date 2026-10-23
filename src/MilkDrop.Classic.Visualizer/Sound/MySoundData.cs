namespace MilkDrop.Classic.Visualizer.Sound;

/// <summary>
/// <c>td_mysounddata</c>（plugin.h:54-63）：预 VMS 时代的声音分析结果。
/// </summary>
/// <remarks>
/// 预设里的 <c>bass</c>/<c>mid</c>/<c>treb</c> 用的是这里的 <see cref="ImmRel"/>，
/// <c>bass_att</c>/<c>mid_att</c>/<c>treb_att</c> 用的是 <see cref="AvgRel"/>
/// （milkdropfs.cpp:483-488），**不是** <see cref="SoundAnalysis.Imm"/> 那一套。
/// </remarks>
public sealed class MySoundData
{
    /// <summary><c>MY_FFT_SAMPLES</c>（plugin.h:53）。</summary>
    public const int FftSamples = 512;

    /// <summary>bass / mids / treble 绝对电平（频段内频谱**求和**，未归一化）。</summary>
    public readonly float[] Imm = new float[3];

    /// <summary>相对整首歌的平均电平；1 = 平均，0.9 ≈ 偏低，1.1 ≈ 偏高。</summary>
    public readonly float[] ImmRel = new float[3];

    /// <summary>轻度阻尼后的绝对电平。</summary>
    public readonly float[] Avg = new float[3];

    /// <summary>轻度阻尼后的相对电平（供 <c>*_att</c> 用）。</summary>
    public readonly float[] AvgRel = new float[3];

    /// <summary>长周期平均，作为"整首歌的平均电平"基准。</summary>
    public readonly float[] LongAvg = new float[3];

    /// <summary><c>fWave[2][576]</c>：已对齐的波形副本，画波形时用。</summary>
    public readonly float[][] Wave = [new float[SoundAnalysis.WaveformSlots], new float[SoundAnalysis.WaveformSlots]];

    /// <summary><c>fSpecLeft[MY_FFT_SAMPLES]</c>：左声道频谱。</summary>
    public readonly float[] SpecLeft = new float[FftSamples];

    public void Reset()
    {
        Array.Clear(Imm);
        Array.Clear(ImmRel);
        Array.Clear(Avg);
        Array.Clear(AvgRel);
        Array.Clear(LongAvg);
        Array.Clear(SpecLeft);
        Array.Clear(Wave[0]);
        Array.Clear(Wave[1]);
    }
}
