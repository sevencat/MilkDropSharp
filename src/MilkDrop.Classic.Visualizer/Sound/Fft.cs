namespace MilkDrop.Classic.Visualizer.Sound;

/// <summary>
/// <c>fft.cpp</c> 的逐行移植（Nullsoft 的 radix-2 FFT + 正弦包络 + 对数均衡）。
/// </summary>
/// <remarks>
/// 不要换成 MathNet/FFT 库：这里的包络表、均衡表以及输出标定都被
/// <see cref="SoundAnalyzer"/> 的经验常数（0.326781557 / 0.380873770 / 0.199888934）绑定了，
/// 换实现会让 bass/mid/treb 的绝对值全变。
/// </remarks>
public sealed class Fft
{
    private readonly int _samplesIn;
    private readonly int _nfreq;
    private readonly int[] _bitRev;

    /// <summary>包络表；<c>envelope_power &lt;= 0</c> 时与原版一样为 null（不乘包络）。</summary>
    private readonly float[]? _envelope;
    private readonly float[] _equalize;
    private readonly float[][] _cosSin;
    private readonly float[] _real;
    private readonly float[] _imag;

    public Fft(int samplesIn, int samplesOut, bool equalize = true, float envelopePower = 1.0f)
    {
        if (samplesOut <= 0 || (samplesOut & (samplesOut - 1)) != 0)
        {
            throw new ArgumentException("samples_out 必须是 2 的幂", nameof(samplesOut));
        }

        _samplesIn = samplesIn;
        _nfreq = samplesOut * 2;
        _bitRev = BuildBitRevTable(_nfreq);
        _cosSin = BuildCosSinTable(_nfreq);
        _envelope = envelopePower > 0f ? BuildEnvelopeTable(samplesIn, envelopePower) : null;
        _equalize = equalize ? BuildEqualizeTable(_nfreq) : [];
        _real = new float[_nfreq];
        _imag = new float[_nfreq];
    }

    public int NumFrequencies => _nfreq;

    /// <summary>时域 → 频域。输出长度 = <c>samples_out</c>（<c>NFREQ/2</c>）。</summary>
    public void TimeToFrequencyDomain(ReadOnlySpan<float> wave, Span<float> spectrum)
    {
        var n = _nfreq;

        // 1. 按位反转把输入搬到 temp1，顺带乘正弦包络（抑制方窗的频响振荡）
        var envelope = _envelope;
        for (var i = 0; i < n; i++)
        {
            var idx = _bitRev[i];
            if (idx < _samplesIn && idx < wave.Length)
            {
                _real[i] = envelope is null ? wave[idx] : wave[idx] * envelope[idx];
            }
            else
            {
                _real[i] = 0f;
            }

            _imag[i] = 0f;
        }

        // 2. 原地 radix-2 Cooley-Tukey
        var dftSize = 2;
        var t = 0;
        while (dftSize <= n)
        {
            var wpr = _cosSin[t][0];
            var wpi = _cosSin[t][1];
            var wr = 1.0f;
            var wi = 0.0f;

            var half = dftSize >> 1;
            for (var m = 0; m < half; m++)
            {
                for (var i = m; i < n; i += dftSize)
                {
                    var j = i + half;
                    var tempr = wr * _real[j] - wi * _imag[j];
                    var tempi = wr * _imag[j] + wi * _real[j];
                    _real[j] = _real[i] - tempr;
                    _imag[j] = _imag[i] - tempi;
                    _real[i] += tempr;
                    _imag[i] += tempi;
                }

                var wtemp = wr;
                wr = wtemp * wpr - wi * wpi;
                wi = wi * wpr + wtemp * wpi;
            }

            dftSize <<= 1;
            t++;
        }

        // 3. 取模并做对数均衡
        var half2 = n / 2;
        for (var i = 0; i < half2 && i < spectrum.Length; i++)
        {
            var magnitude = MathF.Sqrt(_real[i] * _real[i] + _imag[i] * _imag[i]);
            spectrum[i] = _equalize.Length > 0 ? _equalize[i] * magnitude : magnitude;
        }
    }

    private static int[] BuildBitRevTable(int nfreq)
    {
        var table = new int[nfreq];
        for (var i = 0; i < nfreq; i++)
        {
            table[i] = i;
        }

        for (int i = 0, j = 0; i < nfreq; i++)
        {
            if (j > i)
            {
                (table[i], table[j]) = (table[j], table[i]);
            }

            var m = nfreq >> 1;
            while (m >= 1 && j >= m)
            {
                j -= m;
                m >>= 1;
            }

            j += m;
        }

        return table;
    }

    private static float[][] BuildCosSinTable(int nfreq)
    {
        var tabsize = 0;
        for (var dftsize = 2; dftsize <= nfreq; dftsize <<= 1)
        {
            tabsize++;
        }

        var table = new float[tabsize][];
        var index = 0;
        for (var dftsize = 2; dftsize <= nfreq; dftsize <<= 1)
        {
            var theta = -2.0f * MathF.PI / dftsize;
            table[index++] = [MathF.Cos(theta), MathF.Sin(theta)];
        }

        return table;
    }

    private static float[] BuildEnvelopeTable(int samplesIn, float power)
    {
        var envelope = new float[samplesIn];
        var mult = 1.0f / samplesIn * 6.2831853f;

        for (var i = 0; i < samplesIn; i++)
        {
            var sine = 0.5f + 0.5f * MathF.Sin(i * mult - 1.5707963268f);
            envelope[i] = power == 1.0f ? sine : MathF.Pow(sine, power);
        }

        return envelope;
    }

    private static float[] BuildEqualizeTable(int nfreq)
    {
        const float scaling = -0.02f;
        var invHalf = 1.0f / (nfreq / 2);
        var table = new float[nfreq / 2];

        for (var i = 0; i < table.Length; i++)
        {
            table[i] = scaling * MathF.Log((nfreq / 2 - i) * invHalf);
        }

        return table;
    }
}
