namespace MilkDrop.ShaderToy.Audio;

/// <summary>
/// 自产音频的闭环：声卡要数据 → 让 GPU 渲染 <c>mainSound</c> → 环形缓冲 → 声卡线程取走，
/// 同一批采样同时喂给 <see cref="MusicTexture"/>，这样 image pass 能对它做可视化。
/// </summary>
/// <remarks>
/// <para>
/// 只有 GL 线程能碰 GPU，所以"补数据"发生在 <see cref="Pump"/>（由主循环每帧调用）；
/// 声卡线程只从 <see cref="Buffer"/> 里拿已经算好的样本。
/// </para>
/// <para>
/// 目标水位 <see cref="TargetBufferedFrames"/>：约 200 ms。60 FPS 时每帧只该播 735 个采样，
/// 而一批是 1024 个 —— 所以"每帧跑固定批次数"会逐渐溢出，按水位补就自然稳定（S6-2）。
/// </para>
/// </remarks>
public sealed class AudioEngine : IDisposable
{
    /// <summary>目标缓冲水位（帧）：200 ms。</summary>
    public const int TargetBufferedFrames = AudioBuffer.SampleRate / 5;

    /// <summary>单次 Pump 最多补几批，避免卡顿时一次性把一帧时间吃掉。</summary>
    private const int MaxBatchesPerPump = 6;

    private readonly SoundPassRenderer _sound;
    private readonly MusicTexture _music;
    private readonly float[] _scratch;
    private long _nextSample;
    private bool _disposed;

    public AudioEngine(SoundPassRenderer sound, MusicTexture music)
    {
        ArgumentNullException.ThrowIfNull(sound);
        ArgumentNullException.ThrowIfNull(music);

        _sound = sound;
        _music = music;
        _scratch = new float[sound.SamplesPerBatch * 2];
    }

    /// <summary>待播数据（声卡线程从这里读）。</summary>
    public AudioBuffer Buffer { get; } = new();

    /// <summary>当前后端；null 表示静音（没设备或被关掉）。</summary>
    public IAudioSink? Sink { get; private set; }

    /// <summary>是否在出声。</summary>
    public bool IsPlaying => Sink is not null;

    /// <summary>累计渲染的采样点数。</summary>
    public long SamplesRendered => _nextSample;

    /// <summary>音频通道纹理（<c>music</c> / <c>musicstream</c>）。</summary>
    public MusicTexture Music => _music;

    /// <summary>启动播放。</summary>
    /// <remarks>
    /// 先把缓冲填到水位再开设备：实测不预填时开头会欠载 2646 帧（约 60 ms 静音），
    /// 听感上是"咔"一下才开始。
    /// </remarks>
    public void Start(IAudioSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        Stop();
        Prefill();
        sink.Start(Buffer);
        Sink = sink;
    }

    /// <summary>停止播放（保留已渲染的样本）。</summary>
    public void Stop()
    {
        Sink?.Stop();
        Sink = null;
    }

    /// <summary>从头开始（换作品/重置时钟时用）。</summary>
    public void Reset() => SeekToSample(0);

    /// <summary>跳到指定采样点（时间轴拖动时用）：清空缓冲，从该点重新合成。</summary>
    public void SeekToSample(long sample)
    {
        _nextSample = Math.Max(0, sample);
        Buffer.Clear();
    }

    /// <summary>
    /// 按水位补渲染样本。**必须在 GL 上下文当前的线程上调用**（主循环每帧一次）。
    /// </summary>
    /// <returns>本次渲染的采样点数。</returns>
    public int Pump()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 没有后端时不渲染：没人消费的话缓冲会被撑满，music 纹理也会变成快进的垃圾
        return Sink is null ? 0 : Fill(MaxBatchesPerPump);
    }

    /// <summary>不要求后端已启动，直接把缓冲填到水位（<see cref="Start"/> 用）。</summary>
    public int Prefill()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Fill(int.MaxValue);
    }

    private int Fill(int maxBatches)
    {
        var rendered = 0;

        for (var batch = 0; batch < maxBatches; batch++)
        {
            if (Buffer.AvailableFrames >= TargetBufferedFrames)
            {
                break;
            }

            int samples = _sound.RenderBatch(_nextSample, _scratch);
            if (samples <= 0)
            {
                break;
            }

            _nextSample += samples;
            rendered += samples;

            var span = _scratch.AsSpan(0, samples * 2);
            Buffer.Write(span);
            _music.Push(span);
        }

        return rendered;
    }

    /// <summary>只停播放：<see cref="SoundPassRenderer"/> / <see cref="MusicTexture"/> 归管线所有。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        Buffer.Clear();
    }
}
