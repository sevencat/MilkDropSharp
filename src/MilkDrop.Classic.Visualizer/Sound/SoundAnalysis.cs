namespace MilkDrop.Classic.Visualizer.Sound;

/// <summary>
/// 声音分析结果，逐字段对应 <c>pluginshell.h:52-61</c> 的 <c>td_soundinfo</c>。
/// </summary>
/// <remarks>
/// C 版是 <c>float imm[2][3]</c> 这样的二维数组；这里用交错数组，是为了能直接把
/// 某一维当成 <see cref="Span{T}"/> 递给 <see cref="Fft"/>（<c>float[,]</c> 取行会强制拷贝）。
/// </remarks>
public sealed class SoundAnalysis
{
    /// <summary><c>NUM_WAVEFORM_SAMPLES</c>（defines.h:188）：每声道**有效**波形点数。</summary>
    public const int WaveformSamples = 480;

    /// <summary>波形缓冲总长度。<c>[480,576)</c> 在 <c>AlignWaves</c> 对齐后会被清零。</summary>
    public const int WaveformSlots = 576;

    /// <summary><c>NUM_FREQUENCIES</c>（defines.h:199）：0–11,025 Hz 的频谱点数。</summary>
    public const int Frequencies = 512;

    public const int Channels = 2;
    public const int Bands = 3;

    /// <summary>bass / mids / treble，无阻尼（长期平均 ≈ 1）。[ch][band]</summary>
    public readonly float[][] Imm = CreateBands();

    /// <summary>轻度阻尼。[ch][band]</summary>
    public readonly float[][] Avg = CreateBands();

    /// <summary>较强阻尼，主要给 velocity_damping 用。[ch][band]</summary>
    public readonly float[][] MedAvg = CreateBands();

    /// <summary>重度阻尼，主要给 smoke plume 用。[ch][band]</summary>
    public readonly float[][] LongAvg = CreateBands();

    /// <summary>Winamp 的长期平均电平。[ch][band]</summary>
    public readonly float[][] InfiniteAvg = CreateBands();

    /// <summary><c>fWaveform[2][576]</c>。只有前 <see cref="WaveformSamples"/> 个是有效数据。</summary>
    public readonly float[][] Waveform = [new float[WaveformSlots], new float[WaveformSlots]];

    /// <summary><c>fSpectrum[2][NUM_FREQUENCIES]</c>，范围 0–22,050 Hz 均匀分布。</summary>
    public readonly float[][] Spectrum = [new float[Frequencies], new float[Frequencies]];

    public float Vol { get; set; }

    public float Fps { get; set; }

    /// <summary>对应 <c>CPluginShell::CPluginShell</c>（pluginshell.cpp:927-932）的字段初值。</summary>
    public void Reset()
    {
        for (var ch = 0; ch < Channels; ch++)
        {
            Array.Clear(Imm[ch]);
            Array.Clear(Waveform[ch]);
            Array.Clear(Spectrum[ch]);

            // 注意：imm 的初值是 0，而 avg / med_avg / long_avg / infinite_avg 的初值是 1.0f。
            for (var i = 0; i < Bands; i++)
            {
                Avg[ch][i] = 1.0f;
                MedAvg[ch][i] = 1.0f;
                LongAvg[ch][i] = 1.0f;
                InfiniteAvg[ch][i] = 1.0f;
            }
        }

        Vol = 0f;
    }

    private static float[][] CreateBands() => [new float[Bands], new float[Bands]];
}
