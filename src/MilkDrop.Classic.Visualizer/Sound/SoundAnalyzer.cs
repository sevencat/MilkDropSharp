namespace MilkDrop.Classic.Visualizer.Sound;

/// <summary>
/// <c>pluginshell.cpp:1653 AnalyzeNewSound</c> 与 <c>:2342 AlignWaves</c> 的逐行移植。
/// </summary>
/// <remarks>
/// <para>
/// 这两个方法在原版里是每帧成对调用的（pluginshell.cpp:1323-1324），
/// 所以对外提供一个 <see cref="ProcessFrame"/> 来保证顺序。
/// </para>
/// <para>
/// 不要"顺手优化"这里的浮点运算顺序：bass/mid/treb 的绝对值被原版的
/// 经验标定常数（0.326781557 / 0.380873770 / 0.199888934）锚定了。
/// </para>
/// </remarks>
public sealed class SoundAnalyzer
{
    /// <summary><c>MAX_OCTAVES</c>（pluginshell.cpp:2353）。</summary>
    private const int MaxOctaves = 10;

    private readonly SoundAnalysis _sound;
    private readonly Fft _fft;

    /// <summary><c>myfft</c>（plugin.cpp:1063 <c>Init(576, 512, -1)</c>：同样带均衡 + 包络）。</summary>
    private readonly Fft _myFft;

    /// <summary><c>mysound</c>：预 VMS 时代的声音分析，预设变量读的是它。</summary>
    private readonly MySoundData _mySound = new();

    /// <summary><c>float fWaveLeft[576]</c>：旧式 FFT 的输入副本。</summary>
    private readonly float[] _legacyWave = new float[SoundAnalysis.WaveformSlots];

    /// <summary><c>m_oldwave[2][576]</c>：上一帧的波形，用于对齐。</summary>
    private readonly float[][] _oldWave = [new float[SoundAnalysis.WaveformSlots], new float[SoundAnalysis.WaveformSlots]];

    /// <summary><c>m_prev_align_offset[2]</c>。</summary>
    private readonly int[] _prevAlignOffset = new int[2];

    /// <summary><c>m_align_weights_ready</c>：权重表只算一次（原版里是 static 数组）。</summary>
    private bool _alignWeightsReady;

    // AlignWaves 的每帧临时/静态缓冲，改成字段以免每帧分配（原版是栈上 + static）。
    private readonly int[] _spls = new int[MaxOctaves];
    private readonly int[] _space = new int[MaxOctaves];
    private readonly float[][] _tempNew = CreateMipLevels();
    private readonly float[][] _tempOld = CreateMipLevels();
    private readonly float[][] _alignWeight = CreateMipLevels();
    private readonly int[] _firstNonzeroWeight = new int[MaxOctaves];
    private readonly int[] _lastNonzeroWeight = new int[MaxOctaves];

    public SoundAnalyzer(SoundAnalysis? sound = null)
    {
        _sound = sound ?? new SoundAnalysis();
        _sound.Reset();

        // 对应 pluginshell.cpp:274 的 m_fftobj.Init(576, NUM_FREQUENCIES)：
        // samples_in=576、samples_out=512、equalize=true、envelope_power=1.0f。
        _fft = new Fft(SoundAnalysis.WaveformSlots, SoundAnalysis.Frequencies);

        // plugin.cpp:1063 的 myfft.Init(576, MY_FFT_SAMPLES, -1)：bEqualize=-1 即开启均衡，
        // 包络用默认的 1.0f，配置与上面完全相同，但保留独立实例以对应原版的两个 FFT 对象。
        _myFft = new Fft(SoundAnalysis.WaveformSlots, MySoundData.FftSamples);
    }

    public SoundAnalysis Sound => _sound;

    /// <summary><c>mysound</c>：预设变量 <c>bass/mid/treb/*_att</c> 的来源。</summary>
    public MySoundData MySound => _mySound;

    /// <summary>
    /// 一帧的完整声音处理：<c>AnalyzeNewSound</c> → <c>AlignWaves</c> → <c>DoCustomSoundAnalysis</c>
    /// （pluginshell.cpp:1323-1324 + plugin.cpp:3985）。
    /// </summary>
    /// <param name="waveL">左声道 576 个 int8 样本。</param>
    /// <param name="waveR">右声道 576 个 int8 样本。</param>
    /// <param name="fps">当前帧率（原版 <c>m_fps</c>，由 DoTime 在分析前更新）。</param>
    /// <param name="frame">当前帧号（原版 <c>GetFrame()</c>，只在前 50 帧影响 long_avg 收敛速度）。</param>
    public void ProcessFrame(ReadOnlySpan<sbyte> waveL, ReadOnlySpan<sbyte> waveR, float fps, long frame)
    {
        AnalyzeNewSound(waveL, waveR, fps);
        AlignWaves();
        DoCustomSoundAnalysis(fps, frame);
    }

    /// <summary>
    /// 对应 <c>CPlugin::DoCustomSoundAnalysis</c>（plugin.cpp:8720-8780）：
    /// 用对齐后的左声道再做一次 FFT（0–11kHz 三等分求和），并算出相对电平。
    /// </summary>
    /// <remarks>
    /// 注意这里**不除以频段宽度**、也不做经验标定 —— 与 <see cref="SoundAnalysis.Imm"/> 那套不同。
    /// </remarks>
    public void DoCustomSoundAnalysis(float fps, long frame)
    {
        var my = _mySound;

        // 已对齐的波形（AlignWaves 之后）整段复制过去
        Array.Copy(_sound.Waveform[0], my.Wave[0], SoundAnalysis.WaveformSlots);
        Array.Copy(_sound.Waveform[1], my.Wave[1], SoundAnalysis.WaveformSlots);

        Array.Copy(_sound.Waveform[0], _legacyWave, SoundAnalysis.WaveformSlots);

        // 原版先 memset fSpecLeft，Fft 会把 512 个输出全部写满，这里无需清。
        _myFft.TimeToFrequencyDomain(_legacyWave, my.SpecLeft);

        // 三个频段：只看频谱下半部分（所以是 /6 而不是 /3）
        for (var i = 0; i < 3; i++)
        {
            var start = MySoundData.FftSamples * i / 6;
            var end = MySoundData.FftSamples * (i + 1) / 6;

            var sum = 0f;
            for (var j = start; j < end; j++)
            {
                sum += my.SpecLeft[j];
            }

            my.Imm[i] = sum;
        }

        // 时间维混合 + 相对电平
        for (var i = 0; i < 3; i++)
        {
            var rate = my.Imm[i] > my.Avg[i] ? 0.2f : 0.5f;
            rate = AdjustRateToFPS(rate, 30.0f, fps);
            my.Avg[i] = my.Avg[i] * rate + my.Imm[i] * (1 - rate);

            // 前 50 帧让 long_avg 收敛得快一些，否则开场会有一段"相对电平"失真
            rate = frame < 50 ? 0.9f : 0.992f;
            rate = AdjustRateToFPS(rate, 30.0f, fps);
            my.LongAvg[i] = my.LongAvg[i] * rate + my.Imm[i] * (1 - rate);

            my.ImmRel[i] = MathF.Abs(my.LongAvg[i]) < 0.001f ? 1.0f : my.Imm[i] / my.LongAvg[i];

            // 原版这里两个判断都看 long_avg（不是 avg），照抄
            my.AvgRel[i] = MathF.Abs(my.LongAvg[i]) < 0.001f ? 1.0f : my.Avg[i] / my.LongAvg[i];
        }
    }

    /// <summary>对应 <c>CPluginShell::AnalyzeNewSound</c>（pluginshell.cpp:1653-1754）。</summary>
    public void AnalyzeNewSound(ReadOnlySpan<sbyte> waveL, ReadOnlySpan<sbyte> waveR, float fps)
    {
        if (waveL.Length < SoundAnalysis.WaveformSlots)
        {
            throw new ArgumentException($"waveL 至少需要 {SoundAnalysis.WaveformSlots} 个样本", nameof(waveL));
        }

        if (waveR.Length < SoundAnalysis.WaveformSlots)
        {
            throw new ArgumentException($"waveR 至少需要 {SoundAnalysis.WaveformSlots} 个样本", nameof(waveR));
        }

        _sound.Fps = fps;

        // 原版: float temp_wave[2][576];
        Span<float> tempWaveL = stackalloc float[SoundAnalysis.WaveformSlots];
        Span<float> tempWaveR = stackalloc float[SoundAnalysis.WaveformSlots];

        var wave = _sound.Waveform;
        var oldI = 0;
        for (var i = 0; i < SoundAnalysis.WaveformSlots; i++)
        {
            // 原版 pWave* 是 unsigned char，取值 0..255，然后 (x^128)-128 映射到 -128..127。
            // AudioRingBuffer 里已经按 int8 存（FltToInt），直接赋值就等价于那一步。
            wave[0][i] = waveL[i];
            wave[1][i] = waveR[i];

            // 轻微阻尼，压一下高频噪声（i=0 时 old_i==0，等价于原值不变）
            tempWaveL[i] = 0.5f * (wave[0][i] + wave[0][oldI]);
            tempWaveR[i] = 0.5f * (wave[1][i] + wave[1][oldI]);
            oldI = i;
        }

        _fft.TimeToFrequencyDomain(tempWaveL, _sound.Spectrum[0]);
        _fft.TimeToFrequencyDomain(tempWaveR, _sound.Spectrum[1]);

        // 把左/右声道频谱各自汇总成 3 个等音程的频段。
        const float minFreq = 200.0f;
        const float maxFreq = 11025.0f;
        var netOctaves = MathF.Log(maxFreq / minFreq) / MathF.Log(2.0f);   // 5.7846348
        var octavesPerBand = netOctaves / 3.0f;                            // 1.9282116
        var mult = MathF.Pow(2.0f, octavesPerBand);                        // 3.8058313

        for (var ch = 0; ch < SoundAnalysis.Channels; ch++)
        {
            for (var i = 0; i < SoundAnalysis.Bands; i++)
            {
                // bass: 200-761Hz, mids: 761-2897Hz, treb: 2897-11025Hz
                var start = (int)(SoundAnalysis.Frequencies * minFreq * MathF.Pow(mult, i) / maxFreq);
                var end = (int)(SoundAnalysis.Frequencies * minFreq * MathF.Pow(mult, i + 1) / maxFreq);
                if (start < 0) start = 0;
                if (end > SoundAnalysis.Frequencies) end = SoundAnalysis.Frequencies;

                var sum = 0f;
                for (var j = start; j < end; j++)
                {
                    sum += _sound.Spectrum[ch][j];
                }

                _sound.Imm[ch][i] = sum / (end - start);
            }
        }

        // 乘上长期经验反平均值（244 首歌、每首 10 秒统计出来的）。
        for (var ch = 0; ch < SoundAnalysis.Channels; ch++)
        {
            _sound.Imm[ch][0] /= 0.326781557f;
            _sound.Imm[ch][1] /= 0.380873770f;
            _sound.Imm[ch][2] /= 0.199888934f;
        }

        // 时间维度的混合，得到阻尼/强阻尼版本。
        for (var ch = 0; ch < SoundAnalysis.Channels; ch++)
        {
            for (var i = 0; i < SoundAnalysis.Bands; i++)
            {
                var imm = _sound.Imm[ch][i];

                var avgMix = imm > _sound.Avg[ch][i]
                    ? AdjustRateToFPS(0.2f, 14.0f, fps)
                    : AdjustRateToFPS(0.5f, 14.0f, fps);
                _sound.Avg[ch][i] = _sound.Avg[ch][i] * avgMix + imm * (1 - avgMix);

                var medMix = AdjustRateToFPS(0.91f, 14.0f, fps);
                var longMix = AdjustRateToFPS(0.96f, 14.0f, fps);
                _sound.MedAvg[ch][i] = _sound.MedAvg[ch][i] * medMix + imm * (1 - medMix);
                _sound.LongAvg[ch][i] = _sound.LongAvg[ch][i] * longMix + imm * (1 - longMix);
            }
        }
    }

    /// <summary>
    /// 对应 <c>CPluginShell::AlignWaves</c>（pluginshell.cpp:2342-2489）：
    /// 递归（mipmap 式）最小误差匹配，找出最佳对齐偏移，消掉波形的横向抖动。
    /// </summary>
    public void AlignWaves()
    {
        Span<int> alignOffset = stackalloc int[2];

        const int nSamples = SoundAnalysis.WaveformSamples;

        // 原版整段被 #if (NUM_WAVEFORM_SAMPLES < 576) 包着；480 < 576，所以生效。
        if (nSamples < SoundAnalysis.WaveformSlots)
        {
            // log2(576-480) = 6.58 → 6 级 mipmap
            var octaves = (int)MathF.Floor(
                MathF.Log(SoundAnalysis.WaveformSlots - nSamples) / MathF.Log(2.0f));
            if (octaves < 4)
            {
                return;
            }

            if (octaves > MaxOctaves)
            {
                octaves = MaxOctaves;
            }

            for (var ch = 0; ch < SoundAnalysis.Channels; ch++)
            {
                // 只关心低位 nSamples 个样本的对齐
                Array.Copy(_sound.Waveform[ch], _tempNew[0], SoundAnalysis.WaveformSlots);

                // 原版这里只 memcpy nSamples 个，剩下的 96 个是未初始化的栈内存。
                // 这些样本虽然会参与 mip 计算，但计算用到的下标永远 < 480，
                // 所以补零不影响结果，只是把 UB 变成确定行为。
                Array.Clear(_tempOld[0]);
                Array.Copy(_oldWave[ch], _prevAlignOffset[ch], _tempOld[0], 0, nSamples);

                _spls[0] = SoundAnalysis.WaveformSlots;
                _space[0] = SoundAnalysis.WaveformSlots - nSamples;   // 96

                for (var octave = 1; octave < octaves; octave++)
                {
                    var prev = octave - 1;
                    _spls[octave] = _spls[prev] / 2;    // 288,144,72,36,18
                    _space[octave] = _space[prev] / 2;  // 48,24,12,6,3

                    for (var n = 0; n < _spls[octave]; n++)
                    {
                        _tempNew[octave][n] = 0.5f * (_tempNew[prev][n * 2] + _tempNew[prev][n * 2 + 1]);
                        _tempOld[octave][n] = 0.5f * (_tempOld[prev][n * 2] + _tempOld[prev][n * 2 + 1]);
                    }
                }

                if (!_alignWeightsReady)
                {
                    BuildAlignWeights(octaves);
                }

                var n1 = 0;
                var n2 = _space[octaves - 1];

                for (var octave = octaves - 1; octave >= 0; octave--)
                {
                    // 例如 space[octave]==4、spls[octave]==36：比较 32 个样本、4 个偏移。
                    var lowestErrOffset = -1;
                    var lowestErrAmount = 0f;

                    for (var n = n1; n < n2; n++)
                    {
                        var errSum = 0f;
                        for (var i = _firstNonzeroWeight[octave]; i <= _lastNonzeroWeight[octave]; i++)
                        {
                            var x = (_tempNew[octave][i + n] - _tempOld[octave][i]) * _alignWeight[octave][i];
                            if (x > 0f)
                            {
                                errSum += x;
                            }
                            else
                            {
                                errSum -= x;
                            }
                        }

                        if (lowestErrOffset == -1 || errSum < lowestErrAmount)
                        {
                            lowestErrOffset = n;
                            lowestErrAmount = errSum;
                        }
                    }

                    // 用这一级的最优偏移来收窄下一级（采样率翻倍）的搜索范围，
                    // 并把范围向两侧各放宽 1 个样本。
                    if (octave > 0)
                    {
                        n1 = lowestErrOffset * 2 - 1;
                        n2 = lowestErrOffset * 2 + 2 + 1;
                        if (n1 < 0) n1 = 0;
                        if (n2 > _space[octave - 1]) n2 = _space[octave - 1];
                    }
                    else
                    {
                        alignOffset[ch] = lowestErrOffset;
                    }
                }
            }
        }

        Array.Copy(_sound.Waveform[0], _oldWave[0], SoundAnalysis.WaveformSlots);
        Array.Copy(_sound.Waveform[1], _oldWave[1], SoundAnalysis.WaveformSlots);
        _prevAlignOffset[0] = alignOffset[0];
        _prevAlignOffset[1] = alignOffset[1];

        // 应用对齐结果：把样本整体前移，尾部清零（视觉上能看出这些点已失效）。
        for (var ch = 0; ch < SoundAnalysis.Channels; ch++)
        {
            if (alignOffset[ch] <= 0)
            {
                continue;
            }

            var wave = _sound.Waveform[ch];
            for (var i = 0; i < nSamples; i++)
            {
                wave[i] = wave[i + alignOffset[ch]];
            }

            Array.Clear(wave, nSamples, SoundAnalysis.WaveformSlots - nSamples);
        }
    }

    /// <summary>对应 <c>utility.cpp:80 AdjustRateToFPS</c>。</summary>
    public static float AdjustRateToFPS(float perFrameDecayRateAtFps1, float fps1, float actualFps)
    {
        var perSecondDecayRateAtFps1 = MathF.Pow(perFrameDecayRateAtFps1, fps1);
        return MathF.Pow(perSecondDecayRateAtFps1, 1.0f / actualFps);
    }

    /// <summary>对应 pluginshell.cpp:2389-2421 的权重表构造（原本是 static，只算一次）。</summary>
    private void BuildAlignWeights(int octaves)
    {
        _alignWeightsReady = true;

        for (var octave = 0; octave < octaves; octave++)
        {
            var compareSamples = _spls[octave] - _space[octave];
            var weight = _alignWeight[octave];

            for (var n = 0; n < compareSamples; n++)
            {
                // 先来一个金字塔形的概率分布，0..1..0
                var w = n < compareSamples / 2
                    ? n * 2 / (float)compareSamples
                    : (compareSamples - 1 - n) * 2 / (float)compareSamples;

                // 提高中心的重要性、压低边缘
                w = (w - 0.8f) * 5.0f + 0.8f;

                if (w > 1f) w = 1f;
                if (w < 0f) w = 0f;

                weight[n] = w;
            }

            var k = 0;
            while (k < compareSamples && weight[k] == 0f)
            {
                k++;
            }

            _firstNonzeroWeight[octave] = k;

            k = compareSamples - 1;
            while (k >= 0 && weight[k] == 0f)
            {
                k--;
            }

            _lastNonzeroWeight[octave] = k;
        }
    }

    private static float[][] CreateMipLevels()
    {
        var levels = new float[MaxOctaves][];
        for (var i = 0; i < MaxOctaves; i++)
        {
            levels[i] = new float[SoundAnalysis.WaveformSlots];
        }

        return levels;
    }
}
