namespace MilkDrop.ProjectM.Audio;

/// <summary>
/// 基于 mip（倍频程）的波形对齐。
/// </summary>
/// <remarks>
/// <para>对应 <c>Audio/WaveformAligner.hpp:26-53</c> 与 <c>WaveformAligner.cpp:10-228</c>。</para>
/// <para>
/// 逐倍频程、逐偏移量计算与上一帧波形的加权绝对误差，取误差最小的偏移把新波形前移，
/// 让相似特征留在原地而不是每帧乱跳。
/// </para>
/// </remarks>
public sealed class WaveformAligner
{
    private const int MaxOctaves = 10;

    private readonly int _octaves;

    /// <summary>各倍频程的采样权重（每个 576 长，只有前 <c>compareSamples</c> 个有效）。</summary>
    private readonly float[][] _alignmentWeights;

    private readonly int[] _firstNonzeroWeights;
    private readonly int[] _lastNonzeroWeights;
    private readonly int[] _octaveSamples;
    private readonly int[] _octaveSampleSpacing;

    /// <summary>上一帧各倍频程的波形。</summary>
    private readonly float[][] _oldWaveformMips;

    /// <summary>本帧各倍频程的波形（复用缓冲）。</summary>
    private readonly float[][] _newWaveformMips;

    /// <summary>首次填充需要特殊处理（权重表在那时才生成）。</summary>
    private bool _alignWaveReady;

    public WaveformAligner()
    {
        // AudioBufferSamples=576、WaveformSamples=480 时 floor(log2(96)) = 6。
        var numOctaves = (int)Math.Floor(Math.Log(AudioConstants.AudioBufferSamples - AudioConstants.WaveformSamples) / Math.Log(2.0));
        _octaves = Math.Min(numOctaves, MaxOctaves);

        _alignmentWeights = new float[_octaves][];
        _oldWaveformMips = new float[_octaves][];
        _newWaveformMips = new float[_octaves][];
        for (var octave = 0; octave < _octaves; octave++)
        {
            _alignmentWeights[octave] = new float[AudioConstants.AudioBufferSamples];
            _oldWaveformMips[octave] = new float[AudioConstants.AudioBufferSamples];
            _newWaveformMips[octave] = new float[AudioConstants.AudioBufferSamples];
        }

        _firstNonzeroWeights = new int[_octaves];
        _lastNonzeroWeights = new int[_octaves];
        _octaveSamples = new int[_octaves];
        _octaveSampleSpacing = new int[_octaves];

        _octaveSamples[0] = AudioConstants.AudioBufferSamples;
        _octaveSampleSpacing[0] = AudioConstants.AudioBufferSamples - AudioConstants.WaveformSamples;
        for (var octave = 1; octave < _octaves; octave++)
        {
            _octaveSamples[octave] = _octaveSamples[octave - 1] / 2;
            _octaveSampleSpacing[octave] = _octaveSampleSpacing[octave - 1] / 2;
        }
    }

    /// <summary>倍频程数（AudioBufferSamples/WaveformSamples 固定时是 6）。</summary>
    public int Octaves => _octaves;

    /// <summary>把新波形对齐到上一帧；偏移量为正时整体前移并把尾部清零。</summary>
    /// <param name="newWaveform">长度必须是 <see cref="AudioConstants.AudioBufferSamples"/>，原地修改。</param>
    /// <remarks>对应 <c>WaveformAligner.cpp:193-228</c>。</remarks>
    public void Align(Span<float> newWaveform)
    {
        if (newWaveform.Length < AudioConstants.AudioBufferSamples)
        {
            throw new ArgumentException($"波形长度必须至少 {AudioConstants.AudioBufferSamples}。", nameof(newWaveform));
        }

        if (_octaves < 4)
        {
            // 原版在样本余量不足时直接不对齐（没有解释为什么门槛是 2^4）。
            return;
        }

        ResampleOctaves(_newWaveformMips, newWaveform);

        if (!_alignWaveReady)
        {
            GenerateWeights();
            _alignWaveReady = true;
        }

        var alignOffset = CalculateOffset();

        if (alignOffset > 0)
        {
            newWaveform.Slice(alignOffset, AudioConstants.WaveformSamples)
                .CopyTo(newWaveform);

            newWaveform.Slice(AudioConstants.WaveformSamples,
                    AudioConstants.AudioBufferSamples - AudioConstants.WaveformSamples)
                .Clear();
        }

        // 移位后 mip 要按新波形重算，不能复用旧值。
        ResampleOctaves(_oldWaveformMips, newWaveform);
    }

    /// <remarks>对应 <c>WaveformAligner.cpp:34-48</c>。</remarks>
    private void ResampleOctaves(float[][] dstWaveformMips, ReadOnlySpan<float> newWaveform)
    {
        // 倍频程 0 直接拷贝。
        newWaveform[..AudioConstants.AudioBufferSamples].CopyTo(dstWaveformMips[0]);

        // 每级把上一级的相邻两点平均（降采样一半）。
        for (var octave = 1; octave < _octaves; octave++)
        {
            for (var sample = 0; sample < _octaveSamples[octave]; sample++)
            {
                dstWaveformMips[octave][sample] = 0.5f
                    * (dstWaveformMips[octave - 1][sample * 2] + dstWaveformMips[octave - 1][sample * 2 + 1]);
            }
        }
    }

    /// <remarks>对应 <c>WaveformAligner.cpp:50-120</c>。</remarks>
    private void GenerateWeights()
    {
        for (var octave = 0; octave < _octaves; octave++)
        {
            var compareSamples = _octaveSamples[octave] - _octaveSampleSpacing[octave];

            for (var sample = 0; sample < compareSamples; sample++)
            {
                // 先给一个金字塔形 PDF（0..1..0）。
                float weight;
                if (sample < compareSamples / 2)
                {
                    weight = (float)(sample * 2) / compareSamples;
                }
                else
                {
                    weight = (float)((compareSamples - 1 - sample) * 2) / compareSamples;
                }

                // 调整"中心相对边缘"的权重：把零点从 0.5 峰顶挪到 0.32 / 0.68 处。
                weight = (weight - 0.8f) * 5f + 0.8f;

                // 上面这步会把结果拉到 -3.2..1.8，钳到 0..1。
                if (weight > 1f)
                {
                    weight = 1f;
                }

                if (weight < 0f)
                {
                    weight = 0f;
                }

                _alignmentWeights[octave][sample] = weight;
            }

            var index = 0;
            while (_alignmentWeights[octave][index] == 0 && index < compareSamples)
            {
                index++;
            }

            _firstNonzeroWeights[octave] = index;

            index = compareSamples - 1;
            while (_alignmentWeights[octave][index] == 0 && compareSamples > 1)
            {
                index--;
            }

            _lastNonzeroWeights[octave] = index;
        }
    }

    /// <remarks>对应 <c>WaveformAligner.cpp:122-191</c>。</remarks>
    private int CalculateOffset()
    {
        var alignOffset = 0;
        var offsetStart = 0;
        var offsetEnd = _octaveSampleSpacing[_octaves - 1];

        // 从最粗的倍频程往细的走，用上一级的偏移收窄下一级的搜索范围。
        for (var octave = _octaves - 1; octave >= 0; octave--)
        {
            var lowestErrorOffset = -1;
            var lowestErrorAmount = 0f;

            for (var sample = offsetStart; sample < offsetEnd; sample++)
            {
                var errorSum = 0f;

                for (var i = _firstNonzeroWeights[octave]; i <= _lastNonzeroWeights[octave]; i++)
                {
                    errorSum += MathF.Abs(
                        (_newWaveformMips[octave][i + sample] - _oldWaveformMips[octave][i]) * _alignmentWeights[octave][i]);
                }

                if (lowestErrorOffset == -1 || errorSum < lowestErrorAmount)
                {
                    lowestErrorOffset = sample;
                    lowestErrorAmount = errorSum;
                }
            }

            if (octave > 0)
            {
                offsetStart = lowestErrorOffset * 2 - 1;
                offsetEnd = lowestErrorOffset * 2 + 2 + 1;
                if (offsetStart < 0)
                {
                    // 原版同样不允许负偏移。
                    offsetStart = 0;
                }

                if (offsetEnd > _octaveSampleSpacing[octave - 1])
                {
                    offsetEnd = _octaveSampleSpacing[octave - 1];
                }
            }
            else
            {
                alignOffset = lowestErrorOffset;
            }
        }

        return alignOffset;
    }
}
