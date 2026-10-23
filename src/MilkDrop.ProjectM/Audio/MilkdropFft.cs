namespace MilkDrop.ProjectM.Audio;

/// <summary>
/// Nullsoft 版 FFT（带均衡表与正弦包络）。
/// </summary>
/// <remarks>
/// <para>对应 <c>Audio/MilkdropFFT.hpp:43-156</c> 与 <c>MilkdropFFT.cpp:37-203</c>。</para>
/// <para>
/// 输出最后一个频点代表输入采样率的 1/4（输入 44100 Hz → 最后一格 11025 Hz），
/// 前面各点线性分布。总体是"位反转基 2 FFT + 幅度取对数前的经验均衡"，与数值等价实现不同，
/// 为了和原生 projectM 逐像素对齐，这里严格按 C++ 的 float 精度和循环顺序来。
/// </para>
/// </remarks>
public sealed class MilkdropFft
{
    private const float Pi = 3.141592653589793238462643383279502884197169399f;

    private readonly int _samplesIn;
    private readonly int _numFrequencies;
    private readonly int[] _bitRevTable;
    private readonly float[] _envelope;
    private readonly float[] _equalize;
    private readonly ComplexF[] _cosSinTable;

    /// <summary>FFT 工作缓冲（C++ 每次调用都新建一个 vector，这里复用避免每帧分配）。</summary>
    private readonly ComplexF[] _workBuffer;

    /// <param name="samplesIn">输入波形样本数。</param>
    /// <param name="samplesOut">输出频点数，必须是 2 的幂。</param>
    /// <param name="equalize">是否对低频/高频做经验均衡。</param>
    /// <param name="envelopePower">包络幂次；负数表示不做包络。</param>
    public MilkdropFft(int samplesIn, int samplesOut, bool equalize = true, float envelopePower = 1f)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(samplesIn, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(samplesOut, 1);
        if ((samplesOut & (samplesOut - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(samplesOut), "samplesOut 必须是 2 的幂。");
        }

        _samplesIn = samplesIn;
        _numFrequencies = samplesOut * 2;
        _workBuffer = new ComplexF[_numFrequencies];

        _bitRevTable = new int[_numFrequencies];
        InitBitRevTable();

        _cosSinTable = BuildCosSinTable(_numFrequencies);

        _envelope = new float[samplesIn];
        InitEnvelopeTable(envelopePower);

        _equalize = new float[_numFrequencies / 2];
        InitEqualizeTable(equalize);
    }

    /// <summary>FFT 计算出的频点数（<c>samplesOut * 2</c>）。</summary>
    public int NumFrequencies => _numFrequencies;

    /// <summary>输出频点数（<see cref="NumFrequencies"/> 的一半，对应 <c>samplesOut</c>）。</summary>
    public int NumOutputBins => _numFrequencies / 2;

    /// <summary>
    /// 时域 → 频域。
    /// </summary>
    /// <param name="waveformData">时域波形，至少 <c>samplesIn</c> 个样本。</param>
    /// <param name="spectralData">输出频域数据，长度至少 <see cref="NumOutputBins"/>。</param>
    /// <returns>
    /// 成功返回 true；输入不足或表未初始化时返回 false（C++ 是清空输出 vector，C# 这里不清空 span，
    /// 由调用方按返回值决定是否使用）。
    /// </returns>
    /// <remarks>对应 <c>MilkdropFFT.cpp:150-203</c>。</remarks>
    public bool TimeToFrequencyDomain(ReadOnlySpan<float> waveformData, Span<float> spectralData)
    {
        if (_bitRevTable.Length == 0 || _cosSinTable.Length == 0
            || waveformData.Length < _samplesIn || spectralData.Length < NumOutputBins)
        {
            return false;
        }

        // 1. 位反转重排 + 乘包络
        _workBuffer.AsSpan().Clear();
        for (var i = 0; i < _numFrequencies; i++)
        {
            var idx = _bitRevTable[i];
            if (idx < _samplesIn)
            {
                _workBuffer[i] = new ComplexF(waveformData[idx] * _envelope[idx], 0f);
            }
        }

        // 2. 基 2 FFT（与原版一样用累乘的旋转因子，float 精度，保证数值一致）
        var dftSize = 2;
        var octave = 0;
        while (dftSize <= _numFrequencies)
        {
            var w = new ComplexF(1f, 0f);
            var wp = _cosSinTable[octave];

            var halfDftSize = dftSize >> 1;

            for (var m = 0; m < halfDftSize; m++)
            {
                for (var i = m; i < _numFrequencies; i += dftSize)
                {
                    var j = i + halfDftSize;
                    var tempNum = _workBuffer[j] * w;
                    _workBuffer[j] = _workBuffer[i] - tempNum;
                    _workBuffer[i] = _workBuffer[i] + tempNum;
                }

                w *= wp;
            }

            dftSize <<= 1;
            octave++;
        }

        // 3. 取幅度并乘以均衡系数
        for (var i = 0; i < NumOutputBins; i++)
        {
            var c = _workBuffer[i];
            spectralData[i] = _equalize[i] * MathF.Sqrt(c.Real * c.Real + c.Imag * c.Imag);
        }

        return true;
    }

    private void InitEnvelopeTable(float power)
    {
        if (power < 0f)
        {
            Array.Fill(_envelope, 1f);
            return;
        }

        var multiplier = 1f / _samplesIn * 2f * Pi;

        if (power == 1f)
        {
            for (var i = 0; i < _samplesIn; i++)
            {
                _envelope[i] = 0.5f + 0.5f * MathF.Sin(i * multiplier - Pi * 0.5f);
            }
        }
        else
        {
            for (var i = 0; i < _samplesIn; i++)
            {
                _envelope[i] = MathF.Pow(0.5f + 0.5f * MathF.Sin(i * multiplier - Pi * 0.5f), power);
            }
        }
    }

    private void InitEqualizeTable(bool equalize)
    {
        if (!equalize)
        {
            Array.Fill(_equalize, 1f);
            return;
        }

        const float scaling = -0.02f;
        var half = _numFrequencies / 2;
        var inverseHalfNumFrequencies = 1f / half;

        for (var i = 0; i < half; i++)
        {
            _equalize[i] = scaling * MathF.Log((half - i) * inverseHalfNumFrequencies);
        }
    }

    /// <summary>位反转索引表。</summary>
    /// <remarks>对应 <c>MilkdropFFT.cpp:95-124</c>。</remarks>
    private void InitBitRevTable()
    {
        for (var i = 0; i < _numFrequencies; i++)
        {
            _bitRevTable[i] = i;
        }

        var j = 0;
        for (var i = 0; i < _numFrequencies; i++)
        {
            if (j > i)
            {
                (_bitRevTable[i], _bitRevTable[j]) = (_bitRevTable[j], _bitRevTable[i]);
            }

            var m = _numFrequencies >> 1;
            while (m >= 1 && j >= m)
            {
                j -= m;
                m >>= 1;
            }

            j += m;
        }
    }

    /// <summary>每一级蝶形的单位根（<c>std::polar(1, -2π/dftSize)</c>）。</summary>
    /// <remarks>对应 <c>MilkdropFFT.cpp:126-148</c>。</remarks>
    private static ComplexF[] BuildCosSinTable(int numFrequencies)
    {
        var tabSize = 0;
        var dftSize = 2;
        while (dftSize <= numFrequencies)
        {
            tabSize++;
            dftSize <<= 1;
        }

        var table = new ComplexF[tabSize];

        dftSize = 2;
        var index = 0;
        while (dftSize <= numFrequencies)
        {
            var theta = -2f * Pi / dftSize;
            table[index] = new ComplexF(MathF.Cos(theta), MathF.Sin(theta));
            index++;
            dftSize <<= 1;
        }

        return table;
    }

    /// <summary>
    /// float 精度的复数（对应 <c>std::complex&lt;float&gt;</c>）。
    /// <c>System.Numerics.Complex</c> 是 double，会让 FFT 结果与原生有可见偏差。
    /// </summary>
    private readonly struct ComplexF(float real, float imag)
    {
        public readonly float Real = real;

        public readonly float Imag = imag;

        public static ComplexF operator +(ComplexF a, ComplexF b) => new(a.Real + b.Real, a.Imag + b.Imag);

        public static ComplexF operator -(ComplexF a, ComplexF b) => new(a.Real - b.Real, a.Imag - b.Imag);

        public static ComplexF operator *(ComplexF a, ComplexF b)
            => new(a.Real * b.Real - a.Imag * b.Imag, a.Real * b.Imag + a.Imag * b.Real);
    }
}
