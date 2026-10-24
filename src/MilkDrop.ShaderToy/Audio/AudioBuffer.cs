namespace MilkDrop.ShaderToy.Audio;

/// <summary>
/// 立体声环形缓冲 + 线性重采样（供音频后端按自己的节奏取数据）。
/// </summary>
/// <remarks>
/// <para>
/// 写端是 GL 线程（每帧批量渲染一批采样），读端是声卡线程，所以全部操作都在锁里做。
/// </para>
/// <para>
/// 重采样只做"44100 → 设备采样率"这一件事，保音高：按 <c>step = 44100 / deviceRate</c>
/// 推进读取位置，在相邻两个源帧之间线性插值（S6-4）。反过来的做法（改 <c>mainSound</c>
/// 里的 <c>time</c> 公式）会让作品里的音高全变。
/// </para>
/// <para>
/// 缓冲满时**丢最旧的**：宁可丢一点音频也不让延迟越攒越大（攒到几秒就完全不同步了）。
/// </para>
/// </remarks>
public sealed class AudioBuffer
{
    /// <summary>Shadertoy 的渲染采样率（固定 44100，见 S6-4）。</summary>
    public const int SampleRate = 44100;

    private readonly float[] _frames;
    private readonly Lock _gate = new();

    private int _readIndex;
    private int _count;
    private double _fraction;

    private long _totalWritten;
    private long _totalConsumed;
    private long _underrunFrames;

    /// <param name="capacityFrames">容量（采样帧数，每帧含左右两个 float）。</param>
    public AudioBuffer(int capacityFrames = SampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityFrames, 16);

        CapacityFrames = capacityFrames;
        _frames = new float[capacityFrames * 2];
    }

    /// <summary>容量（帧）。</summary>
    public int CapacityFrames { get; }

    /// <summary>当前可读的帧数。</summary>
    public int AvailableFrames
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>累计写入的帧数。</summary>
    public long TotalWrittenFrames
    {
        get
        {
            lock (_gate)
            {
                return _totalWritten;
            }
        }
    }

    /// <summary>累计被后端取走的帧数（输出侧，含重采样）。</summary>
    public long TotalConsumedFrames
    {
        get
        {
            lock (_gate)
            {
                return _totalConsumed;
            }
        }
    }

    /// <summary>累计"欠载"的输出帧数（填了静音）。持续增长说明渲染跟不上。</summary>
    public long UnderrunFrames
    {
        get
        {
            lock (_gate)
            {
                return _underrunFrames;
            }
        }
    }

    /// <summary>写入交错立体声（<c>[L,R,L,R,...]</c>）。</summary>
    public void Write(ReadOnlySpan<float> interleavedStereo)
    {
        int frames = interleavedStereo.Length / 2;
        if (frames == 0)
        {
            return;
        }

        lock (_gate)
        {
            // 超容量时只保留最后 capacityFrames 帧
            if (frames > CapacityFrames)
            {
                interleavedStereo = interleavedStereo[^((CapacityFrames) * 2)..];
                frames = CapacityFrames;
            }

            // 满了丢最旧的（最多丢已经存在的那部分，别把 _count 减成负数）
            int overflow = Math.Min(_count, _count + frames - CapacityFrames);
            if (overflow > 0)
            {
                _readIndex = (_readIndex + overflow) % CapacityFrames;
                _count -= overflow;
            }

            int writeIndex = (_readIndex + _count) % CapacityFrames;

            for (var i = 0; i < frames; i++)
            {
                _frames[writeIndex * 2] = Clamp(interleavedStereo[i * 2]);
                _frames[(writeIndex * 2) + 1] = Clamp(interleavedStereo[(i * 2) + 1]);

                writeIndex++;
                if (writeIndex == CapacityFrames)
                {
                    writeIndex = 0;
                }
            }

            _count += frames;
            _totalWritten += frames;
        }
    }

    /// <summary>清空（重启作品/重置时钟时用）。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _readIndex = 0;
            _count = 0;
            _fraction = 0;
            _underrunFrames = 0;
        }
    }

    /// <summary>
    /// 取 <paramref name="frames"/> 个输出帧（交错立体声），按需从 44100 重采样到
    /// <paramref name="deviceSampleRate"/>。数据不够时填静音并累加 <see cref="UnderrunFrames"/>。
    /// </summary>
    public void Read(Span<float> destination, int frames, int deviceSampleRate)
    {
        if (deviceSampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(deviceSampleRate), "采样率必须为正。");
        }

        if (destination.Length < frames * 2)
        {
            throw new ArgumentException("目标缓冲太小。", nameof(destination));
        }

        double step = (double)SampleRate / deviceSampleRate;

        lock (_gate)
        {
            for (var i = 0; i < frames; i++)
            {
                if (_count == 0)
                {
                    destination[i * 2] = 0f;
                    destination[(i * 2) + 1] = 0f;
                    _underrunFrames++;
                    continue;
                }

                int baseIndex = (int)_fraction;
                if (baseIndex < 0)
                {
                    baseIndex = 0;
                }

                // 相位跑到缓冲末尾（设备采样率低于 44100 时会出现）：夹到最后一帧
                if (baseIndex > _count - 1)
                {
                    baseIndex = _count - 1;
                    _fraction = baseIndex;
                }

                int first = ((_readIndex + baseIndex) % CapacityFrames) * 2;
                bool hasNext = baseIndex + 1 < _count;
                int second = hasNext
                    ? ((_readIndex + baseIndex + 1) % CapacityFrames) * 2
                    : first;

                var t = hasNext ? (float)(_fraction - baseIndex) : 0f;
                destination[i * 2] = _frames[first] + ((_frames[second] - _frames[first]) * t);
                destination[(i * 2) + 1] = _frames[first + 1] + ((_frames[second + 1] - _frames[first + 1]) * t);

                _totalConsumed++;

                _fraction += step;
                int advance = (int)_fraction;
                if (advance > 0)
                {
                    _fraction -= advance;

                    // 永远留住最后一帧：它是下一次插值（或下批数据）的起点
                    int maxAdvance = Math.Max(0, _count - 1);
                    if (advance > maxAdvance)
                    {
                        advance = maxAdvance;
                    }

                    _readIndex = (_readIndex + advance) % CapacityFrames;
                    _count -= advance;
                }
            }
        }
    }

    private static float Clamp(float value) => float.IsFinite(value) ? Math.Clamp(value, -1f, 1f) : 0f;
}
