using MilkDrop.ProjectM.Audio;
using NAudio.Wave;

namespace MilkDrop.ProjectM.App;

/// <summary>
/// 用 WASAPI 环回采集系统正在播放的声音，喂给 <see cref="Pcm"/>。
/// </summary>
/// <remarks>
/// 没有音频设备（CI、无声卡）时构造不抛异常，只是 <see cref="IsRunning"/> 为 false，
/// 此时预设仍然照常渲染（静音驱动的波形）。
/// </remarks>
public sealed class AudioCapture : IDisposable
{
    private readonly Pcm _pcm;
    private WasapiLoopbackCapture? _capture;
    private float[] _floatBuffer = [];

    public AudioCapture(Pcm pcm)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        _pcm = pcm;

        try
        {
            _capture = new WasapiLoopbackCapture();
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _capture.StartRecording();

            WaveFormat format = _capture.WaveFormat;
            Console.WriteLine($"音频采集: WASAPI 环回 {format.SampleRate} Hz / {format.Channels} ch / {format.BitsPerSample} bit");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _capture?.Dispose();
            _capture = null;
            Console.WriteLine("音频采集不可用（静音渲染）: " + ex.Message);
        }
    }

    /// <summary>是否正在采集。</summary>
    public bool IsRunning => _capture is not null;

    /// <summary>启动失败原因（正常时为 null）。</summary>
    public string? LastError { get; }

    public void Dispose()
    {
        if (_capture is null)
        {
            return;
        }

        try
        {
            _capture.StopRecording();
        }
        catch (Exception)
        {
            // 设备已移除时会抛，忽略。
        }

        _capture.DataAvailable -= OnDataAvailable;
        _capture.RecordingStopped -= OnRecordingStopped;
        _capture.Dispose();
        _capture = null;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_capture is null || e.BytesRecorded == 0)
        {
            return;
        }

        WaveFormat format = _capture.WaveFormat;
        int channels = Math.Max(1, format.Channels);

        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            int sampleFrames = e.BytesRecorded / (4 * channels);

            if (_floatBuffer.Length < sampleFrames * channels)
            {
                _floatBuffer = new float[sampleFrames * channels];
            }

            Buffer.BlockCopy(e.Buffer, 0, _floatBuffer, 0, sampleFrames * channels * 4);
            _pcm.Add(_floatBuffer.AsSpan(0, sampleFrames * channels), channels, sampleFrames);
        }
        else if (format.BitsPerSample == 16)
        {
            int sampleFrames = e.BytesRecorded / (2 * channels);
            var samples = new short[sampleFrames * channels];

            Buffer.BlockCopy(e.Buffer, 0, samples, 0, sampleFrames * channels * 2);
            _pcm.Add(samples.AsSpan(0, sampleFrames * channels), channels, sampleFrames);
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
