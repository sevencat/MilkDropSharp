namespace MilkDrop.Classic.Visualizer.Audio;

/// <summary>
/// 音频输入抽象。生产环境用 WASAPI loopback（M1），回归测试用 WAV 文件，
/// 差分测试用 <see cref="SilenceAudioSource"/>（见 cbplan.md §6.1）。
/// </summary>
public interface IAudioSource : IDisposable
{
    string Name { get; }

    int SampleRate { get; }

    int Channels { get; }

    void Start();

    void Stop();

    /// <summary>拉取交错 float32 样本；返回实际写入的样本数。</summary>
    int Read(Span<float> destination);
}

/// <summary>静音源：M0 阶段的占位实现，也用于"零输入"的确定性测试。</summary>
public sealed class SilenceAudioSource : IAudioSource
{
    public string Name => "silence";

    public int SampleRate => 44100;

    public int Channels => 2;

    public void Start()
    {
    }

    public void Stop()
    {
    }

    public int Read(Span<float> destination)
    {
        destination.Clear();
        return destination.Length;
    }

    public void Dispose()
    {
    }
}
