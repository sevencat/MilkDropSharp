namespace MilkDrop.ShaderToy.Audio;

/// <summary>
/// 音频输出后端。
/// </summary>
/// <remarks>
/// <para>
/// 抽象出这一层是因为 AOT 下 NAudio 的 WASAPI 会直接崩（<c>projectMIssue.md</c>：
/// <c>MMDeviceEnumeratorComObject..ctor()</c> 被编译成必定抛异常的桩），所以音频设备相关的
/// 具体实现留在 App 层，库层只认这个接口（<c>shaderToyIssue.md</c> S6-6）。
/// </para>
/// <para>
/// 数据流：<see cref="SoundPassRenderer"/> 在 GPU 上批量算出采样 → <see cref="AudioBuffer"/> →
/// 后端按自己的节奏把环形缓冲读空。后端在**自己的线程**上读，所以 <see cref="AudioBuffer"/>
/// 内部必须加锁。
/// </para>
/// </remarks>
public interface IAudioSink : IDisposable
{
    /// <summary>后端期望的采样率（Hz）。与 44100 不同时由 <see cref="AudioBuffer"/> 线性重采样。</summary>
    int SampleRate { get; }

    /// <summary>后端描述（日志/UI 用）。</summary>
    string Description { get; }

    /// <summary>开始播放：后端从此反复从 <paramref name="buffer"/> 取数据。</summary>
    void Start(AudioBuffer buffer);

    /// <summary>停止播放并释放设备。</summary>
    void Stop();
}

/// <summary>
/// 什么都不做的音频后端：拿不到设备（或显式静音）时用它，画面照常跑。
/// </summary>
public sealed class NullAudioSink(int sampleRate = AudioBuffer.SampleRate) : IAudioSink
{
    public int SampleRate { get; } = sampleRate;

    public string Description => "静音（没有可用的音频设备）";

    public void Start(AudioBuffer buffer)
    {
        // 不消费：环形缓冲会自动丢弃最旧的样本，不会涨到天上
    }

    public void Stop()
    {
    }

    public void Dispose() => Stop();
}
