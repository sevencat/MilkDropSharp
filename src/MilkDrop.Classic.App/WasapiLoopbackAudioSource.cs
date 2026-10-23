using MilkDrop.Classic.Visualizer.Audio;
using NAudio.Wave;

namespace MilkDrop.Classic.App;

/// <summary>
/// WASAPI 回环采集（录系统输出），对应原版从 Winamp 取 PCM 的位置
/// （<c>Milkdrop2PcmVisualizer.cpp:418 GetAudioBuf</c> 的上游）。
/// </summary>
/// <remarks>
/// 采集线程把样本推入一个 float 环形队列，主线程每帧拉一小段，
/// 转成 int8 的量化误差由 <see cref="AudioRingBuffer.FltToInt"/> 负责（对齐 audiobuf.cpp）。
/// </remarks>
public sealed class WasapiLoopbackAudioSource : IAudioSource
{
    /// <summary>环形队列长度（float 个数，立体声交错），约 90ms @ 44.1kHz。</summary>
    private const int RingLength = 16384;

    private readonly WasapiLoopbackCapture _capture;
    private readonly float[] _ring = new float[RingLength];
    private readonly object _gate = new();
    private readonly bool _isFloat;
    private readonly int _bytesPerSample;

    private int _read;
    private int _count;
    private bool _started;
    private bool _disposed;

    public WasapiLoopbackAudioSource()
    {
        _capture = new WasapiLoopbackCapture();

        var format = _capture.WaveFormat;
        _bytesPerSample = format.BitsPerSample / 8;

        // 回环采集用的是设备混音格式，八成是 32-bit IEEE float；个别驱动是 16-bit PCM。
        _isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
                   || (format.Encoding == WaveFormatEncoding.Extensible && format.BitsPerSample == 32);

        if (!_isFloat && !(format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16))
        {
            _capture.Dispose();
            throw new NotSupportedException($"不支持的采集格式: {format.Encoding} {format.BitsPerSample}bit");
        }

        _capture.DataAvailable += OnDataAvailable;
    }

    public string Name => "wasapi-loopback";

    public int SampleRate => _capture.WaveFormat.SampleRate;

    /// <summary>固定按立体声输出（单声道采集会把左右都填成同一个值）。</summary>
    public int Channels => 2;

    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        _capture.StartRecording();
    }

    public void Stop()
    {
        if (!_started)
        {
            return;
        }

        _started = false;
        _capture.StopRecording();
    }

    public int Read(Span<float> destination)
    {
        lock (_gate)
        {
            var n = Math.Min(destination.Length, _count);
            n -= n % 2;   // 只交出完整的帧，否则右声道会错位

            if (n <= 0)
            {
                return 0;
            }

            for (var i = 0; i < n; i++)
            {
                destination[i] = _ring[_read];
                _read = (_read + 1) % RingLength;
            }

            _count -= n;
            return n;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var format = _capture.WaveFormat;
        var blockAlign = format.BlockAlign;
        if (blockAlign <= 0)
        {
            return;
        }

        var frames = e.BytesRecorded / blockAlign;
        var channels = format.Channels;

        lock (_gate)
        {
            for (var i = 0; i < frames; i++)
            {
                var offset = i * blockAlign;
                var left = ReadSample(e.Buffer, offset);
                var right = channels >= 2 ? ReadSample(e.Buffer, offset + _bytesPerSample) : left;
                Push(left, right);
            }
        }
    }

    private float ReadSample(byte[] buffer, int offset) => _isFloat
        ? BitConverter.ToSingle(buffer, offset)
        : BitConverter.ToInt16(buffer, offset) / 32768f;

    /// <summary>调用方必须持有 <see cref="_gate"/>。队列满时丢最老的样本，保证总是最新音频。</summary>
    private void Push(float left, float right)
    {
        if (_count + 2 > RingLength)
        {
            _read = (_read + 2) % RingLength;
            _count -= 2;
        }

        var write = (_read + _count) % RingLength;
        _ring[write] = left;
        _ring[(write + 1) % RingLength] = right;
        _count += 2;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            Stop();
        }
        catch
        {
            // 设备已被拔掉时 StopRecording 会抛，忽略即可。
        }

        _capture.DataAvailable -= OnDataAvailable;
        _capture.Dispose();
    }
}
