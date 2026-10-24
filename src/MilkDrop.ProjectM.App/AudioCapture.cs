using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MilkDrop.ProjectM.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MilkDrop.ProjectM.App;

/// <summary>
/// 用 WASAPI 环回采集系统正在播放的声音，喂给 <see cref="Pcm"/>。
/// </summary>
/// <remarks>
/// 没有音频设备（CI、无声卡）时构造不抛异常，只是 <see cref="IsRunning"/> 为 false，
/// 此时预设仍然照常渲染（静音驱动的波形）。
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class AudioCapture : IDisposable
{
    private readonly Pcm _pcm;
    private WasapiRecorder? _recorder;
    private float[] _floatBuffer = [];
    private short[] _shortBuffer = [];

    public AudioCapture(Pcm pcm)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        _pcm = pcm;

        try
        {
            // NAudio 3 把 WASAPI 的 COM 层换成了 [GeneratedComInterface] + ComWrappers，
            // 所以这里在 NativeAOT 下也能用（2.x 的 WasapiLoopbackCapture 会炸在 COM 激活上）。
            _recorder = new WasapiRecorderBuilder()
                .WithLoopbackCapture()
                .Build();

            _recorder.DataAvailable += OnDataAvailable;
            _recorder.RecordingStopped += OnRecordingStopped;
            _recorder.StartRecording();

            WaveFormat format = _recorder.WaveFormat;
            Console.WriteLine($"音频采集: WASAPI 环回 {format.SampleRate} Hz / {format.Channels} ch / {format.BitsPerSample} bit");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _recorder?.Dispose();
            _recorder = null;
            Console.WriteLine("音频采集不可用（静音渲染）: " + ex.Message);
        }
    }

    /// <summary>是否正在采集。</summary>
    public bool IsRunning => _recorder is not null;

    /// <summary>启动失败原因（正常时为 null）。</summary>
    public string? LastError { get; }

    public void Dispose()
    {
        if (_recorder is null)
        {
            return;
        }

        try
        {
            _recorder.StopRecording();
        }
        catch (Exception)
        {
            // 设备已移除时会抛，忽略。
        }

        _recorder.DataAvailable -= OnDataAvailable;
        _recorder.RecordingStopped -= OnRecordingStopped;
        _recorder.Dispose();
        _recorder = null;
    }

    private void OnDataAvailable(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        if (_recorder is null || buffer.IsEmpty)
        {
            return;
        }

        WaveFormat format = _recorder.WaveFormat;
        int channels = Math.Max(1, format.Channels);

        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            int sampleFrames = buffer.Length / (4 * channels);
            int count = sampleFrames * channels;
            if (count == 0)
            {
                return;
            }

            if (_floatBuffer.Length < count)
            {
                _floatBuffer = new float[count];
            }

            MemoryMarshal.Cast<byte, float>(buffer[..(count * 4)]).CopyTo(_floatBuffer.AsSpan(0, count));
            _pcm.Add(_floatBuffer.AsSpan(0, count), channels, sampleFrames);
        }
        else if (format.BitsPerSample == 16)
        {
            int sampleFrames = buffer.Length / (2 * channels);
            int count = sampleFrames * channels;
            if (count == 0)
            {
                return;
            }

            if (_shortBuffer.Length < count)
            {
                _shortBuffer = new short[count];
            }

            MemoryMarshal.Cast<byte, short>(buffer[..(count * 2)]).CopyTo(_shortBuffer.AsSpan(0, count));
            _pcm.Add(_shortBuffer.AsSpan(0, count), channels, sampleFrames);
        }
    }

    private static void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            Console.WriteLine("音频采集停止: " + e.Exception.Message);
        }
    }
}
