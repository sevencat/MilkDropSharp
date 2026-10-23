using MilkDrop.ProjectM.Audio;

namespace MilkDrop.ProjectM.App;

/// <summary>
/// 合成测试音：440 Hz 正弦 + 每 0.5 秒一次的低频脉冲。
/// </summary>
/// <remarks>
/// 无人值守验证（`--bench` / `--smoke`）时用：不上音频设备也能让 <c>bass/mid/treb</c> 有稳定数值，
/// 否则很多依赖音频的预设会一直停留在黑屏，把"没声音"误判成"渲染坏了"。
/// </remarks>
internal sealed class SyntheticAudio
{
    private const int SampleRate = 48000;
    private const int Channels = 2;

    private readonly Pcm _pcm;
    private readonly float[] _buffer = new float[2 * MaxSamplesPerFrame];

    private double _time;

    private const int MaxSamplesPerFrame = 2048;

    public SyntheticAudio(Pcm pcm)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        _pcm = pcm;
    }

    /// <summary>每帧灌入的样本数（60 FPS 时 800 个样本 ≈ 16.7 ms）。</summary>
    public int SamplesPerFrame { get; set; } = 800;

    /// <summary>按当前时间推进一帧的量。</summary>
    public void Pump()
    {
        int sampleFrames = Math.Clamp(SamplesPerFrame, 1, MaxSamplesPerFrame);

        for (var i = 0; i < sampleFrames; i++)
        {
            double t = _time + i / (double)SampleRate;

            // 每 0.5 秒一个衰减包络，喂出清晰的节拍感。
            double beatPhase = t % 0.5;
            var beatEnvelope = (float)Math.Exp(-beatPhase * 10.0);

            var value = (float)(
                0.25 * Math.Sin(2.0 * Math.PI * 440.0 * t)
                + 0.30 * Math.Sin(2.0 * Math.PI * 1000.0 * t)
                + 0.70 * beatEnvelope * Math.Sin(2.0 * Math.PI * 60.0 * t));

            value = Math.Clamp(value, -1.0f, 1.0f);

            _buffer[i * Channels] = value;
            _buffer[(i * Channels) + 1] = value;
        }

        _time += sampleFrames / (double)SampleRate;

        _pcm.Add(_buffer.AsSpan(0, sampleFrames * Channels), Channels, sampleFrames);
    }
}
