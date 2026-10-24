using MilkDrop.ShaderToy.Audio;
using NAudio.Wave;

namespace MilkDrop.ShaderToy.App;

/// <summary>
/// 音频输出：NAudio 的 <see cref="WaveOutEvent"/>（winmm 的托管封装）。
/// </summary>
/// <remarks>
/// <para>
/// 设备端按 44100 开：Shadertoy 的 <c>mainSound</c> 语义就是 44.1 kHz（S6-4），
/// 让系统混音器去处理设备的 48 kHz 比我们自己重采样引入的抖动更小；
/// <see cref="AudioBuffer"/> 的重采样路径仍然保留（后端报别的采样率时自动生效）。
/// </para>
/// <para>
/// 缓冲 3 段 × 20 ms（<c>DesiredLatency</c> = 60）：够吸收一帧的渲染抖动，又不至于让画面
/// 与声音差出人眼能察觉的量。
/// </para>
/// <para>
/// AOT 下不要走 WASAPI（<c>MMDeviceEnumeratorComObject..ctor()</c> 会抛 <c>InvalidProgram</c>，
/// 见 S6-6），所以这里只用 winmm 后端。拿不到设备时由调用方回落到 <see cref="NullAudioSink"/>。
/// </para>
/// </remarks>
internal sealed class WaveOutAudioSink : IAudioSink
{
    private WaveOutEvent? _device;
    private BufferProvider? _provider;

    public int SampleRate => AudioBuffer.SampleRate;

    public string Description => "NAudio WaveOutEvent (winmm, 44100 Hz 16-bit stereo)";

    public void Start(AudioBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        Stop();

        _provider = new BufferProvider(buffer);
        _device = new WaveOutEvent
        {
            DesiredLatency = 60,
            NumberOfBuffers = 3,
        };

        _device.Init(_provider);
        _device.Play();
    }

    public void Stop()
    {
        if (_device is not null)
        {
            _device.Stop();
            _device.Dispose();
            _device = null;
        }

        _provider = null;
    }

    public void Dispose() => Stop();

    /// <summary>把环形缓冲里的 float 立体声转成 16-bit PCM 交给 winmm。</summary>
    private sealed class BufferProvider(AudioBuffer buffer) : IWaveProvider
    {
        private float[] _scratch = new float[4096 * 2];

        public WaveFormat WaveFormat { get; } = new(AudioBuffer.SampleRate, 16, 2);

        public int Read(byte[] destination, int offset, int count)
        {
            int frames = count / 4;
            if (frames == 0)
            {
                return 0;
            }

            if (_scratch.Length < frames * 2)
            {
                _scratch = new float[frames * 2];
            }

            // 欠载时 Read 会自己填静音，所以这里永远能填满整块，不会让 winmm 拿到半块
            buffer.Read(_scratch.AsSpan(0, frames * 2), frames, AudioBuffer.SampleRate);

            for (var i = 0; i < frames * 2; i++)
            {
                short sample = (short)Math.Clamp((int)(_scratch[i] * short.MaxValue), short.MinValue, short.MaxValue);
                destination[offset + (i * 2)] = (byte)(sample & 0xFF);
                destination[offset + (i * 2) + 1] = (byte)((sample >> 8) & 0xFF);
            }

            return frames * 4;
        }
    }
}
