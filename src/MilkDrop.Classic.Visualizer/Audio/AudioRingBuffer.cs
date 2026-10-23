namespace MilkDrop.Classic.Visualizer.Audio;

/// <summary>
/// 音频环形缓冲，语义对齐 <c>audio/audiobuf.cpp</c>（576 帧 × 2 声道，int8 值域）。
/// </summary>
/// <remarks>
/// <para>
/// C 版把 float32 转成 int8（<c>FltToInt</c>）以节省内存并保持与原版一致的量化误差，
/// 所以这里也存 <see cref="sbyte"/>，不要图方便改成 float。
/// </para>
/// <para>
/// <c>GetAudioBuf</c> 的两个细节必须保留，否则 <c>AlignWaves</c> 的相位基准会漂：
/// 读出顺序是"最老 → 最新"（索引 <c>pcmPos</c> 起步），且同一批数据只能被消费一次
/// （<c>pcmBufDrained</c>），否则每帧都拿同一段波形做 FFT，bass 会一直亮着。
/// </para>
/// </remarks>
public sealed class AudioRingBuffer
{
    /// <summary><c>SAMPLE_SIZE_LPB</c>（audiobuf.cpp:5）。</summary>
    public const int SampleCount = 576;

    private const int ChannelCount = 2;

    private readonly sbyte[] _samples = new sbyte[SampleCount * ChannelCount];

    /// <summary><c>pcmPos</c>：下一个写入位置。缓冲填满后它也就指向最老的样本。</summary>
    private int _position;

    /// <summary><c>pcmLen</c>：缓冲里实际有效的帧数。</summary>
    private int _length;

    /// <summary><c>pcmBufDrained</c>：上一批数据已被可视化线程取走。</summary>
    private bool _drained;

    private long _written;

    /// <summary>已写入的样本帧数。</summary>
    public long Written => Interlocked.Read(ref _written);

    /// <summary>当前有效帧数（<c>pcmLen</c>）。</summary>
    public int Length => Volatile.Read(ref _length);

    /// <summary>把交错 float32 音频（-1..1）写入缓冲，对应 <c>SetAudioBuf</c>。</summary>
    public void Write(ReadOnlySpan<float> interleaved)
    {
        if (interleaved.Length == 0)
        {
            return;
        }

        var frames = interleaved.Length / ChannelCount;
        if (frames == 0)
        {
            return;
        }

        for (var i = 0; i < frames; i++)
        {
            var index = (_position + i) % SampleCount;
            for (var ch = 0; ch < ChannelCount; ch++)
            {
                _samples[index * ChannelCount + ch] = FltToInt(interleaved[i * ChannelCount + ch]);
            }
        }

        // 注意：原版 SetAudioBuf 的 nNumFramesToRead < SAMPLE_SIZE_LPB 分支把数据写在
        // (pcmPos + SAMPLE_SIZE_LPB - frames) 开始的位置，等于绕开了 pcmPos，会读回旧数据。
        // 这里统一采用 nNumFramesToRead >= SAMPLE_SIZE_LPB 分支的语义（顺序写入、只保留最新 576 帧），
        // 这也是 WASAPI 每包 >576 帧时的实际路径。
        _position = (_position + frames) % SampleCount;
        Volatile.Write(ref _length, Math.Min(_length + frames, SampleCount));
        _drained = false;

        Interlocked.Add(ref _written, frames);
    }

    /// <summary>
    /// 对应 <c>GetAudioBuf</c>（audiobuf.cpp:22-38）：把 576 帧按"最老 → 最新"
    /// 拷到 <paramref name="left"/>/<paramref name="right"/>。
    /// </summary>
    /// <returns>
    /// 缓冲欠载或数据已被取走时返回 <c>false</c>，此时两个目标缓冲被清零
    /// （对应原版的 <c>memset</c> 静音策略）。
    /// </returns>
    public bool ReadAudioBuf(Span<sbyte> left, Span<sbyte> right)
    {
        if (left.Length < SampleCount)
        {
            throw new ArgumentException($"left 至少需要 {SampleCount} 个样本", nameof(left));
        }

        if (right.Length < SampleCount)
        {
            throw new ArgumentException($"right 至少需要 {SampleCount} 个样本", nameof(right));
        }

        if (_length < SampleCount || _drained)
        {
            left[..SampleCount].Clear();
            right[..SampleCount].Clear();
            return false;
        }

        for (var i = 0; i < SampleCount; i++)
        {
            var index = (_position + i) % SampleCount;
            left[i] = _samples[index * ChannelCount];
            right[i] = _samples[index * ChannelCount + 1];
        }

        _drained = true;
        return true;
    }

    public void Clear()
    {
        Array.Clear(_samples);
        _position = 0;
        _length = 0;
        _drained = false;
    }

    /// <summary>对应 <c>audiobuf.cpp FltToInt</c>。</summary>
    public static sbyte FltToInt(float x)
    {
        if (float.IsNaN(x))
        {
            return 0;   // C 版这里是对 NaN 的未定义行为，取确定值更安全
        }

        var v = (int)(x * 128f);
        if (v > 127)
        {
            v = 127;
        }
        else if (v < -128)
        {
            v = -128;
        }

        return (sbyte)v;
    }
}
