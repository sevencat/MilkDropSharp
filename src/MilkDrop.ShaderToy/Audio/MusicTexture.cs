using System.Numerics;
using Silk.NET.OpenGLES;

using Texture = MilkDrop.Rendering.Gles3.Texture;

namespace MilkDrop.ShaderToy.Audio;

/// <summary>
/// Shadertoy 的音频输入纹理（<c>music</c> / <c>musicstream</c> 通道）：512×2 RGBA8。
/// </summary>
/// <remarks>
/// <para>
/// 约定（S6-7）：<b>第 0 行 = 频谱</b>（对数频率轴）、<b>第 1 行 = 波形</b>；<c>r</c> = 左声道、
/// <c>g</c> = 右声道。作品里通常写 <c>texture(iChannel0, vec2(uv.x, 0.25))</c> 取频谱、
/// <c>0.75</c> 取波形 —— 所以 v 小的那一行必须是频谱（GL 的行 0 在下面）。
/// </para>
/// <para>
/// 这里不做"频谱平滑/衰减"，直接给当前窗口的幅度：Web Audio 端的 AnalyserNode 有
/// <c>smoothingTimeConstant</c>，而 Shadertoy 的音频纹理本身也是每帧重算的瞬时值。
/// </para>
/// </remarks>
public sealed class MusicTexture : IDisposable
{
    /// <summary>纹理宽度（= 频谱列数）。</summary>
    public const int Width = 512;

    /// <summary>纹理高度（0 = 频谱，1 = 波形）。</summary>
    public const int Height = 2;

    private const int FftSize = 512;
    private const int HistoryFrames = 1024;

    private readonly byte[] _pixels = new byte[Width * Height * 4];
    private readonly float[] _history = new float[HistoryFrames * 2];
    private readonly float[] _windowLeft = new float[FftSize];
    private readonly float[] _windowRight = new float[FftSize];
    private readonly Complex[] _fft = new Complex[FftSize];
    private readonly int[] _bitReverse = new int[FftSize];
    private readonly float[] _magnitudeLeft = new float[FftSize / 2];
    private readonly float[] _magnitudeRight = new float[FftSize / 2];

    private int _historyCount;
    private int _historyStart;

    public MusicTexture()
    {
        Texture = new Texture("music", Width, Height, (int)GLEnum.Rgba8, GLEnum.Rgba, GLEnum.UnsignedByte, false);

        var gl = MilkDrop.Rendering.Gles3.GlContext.Gl;
        Texture.Bind(0);
        gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMagFilter, (int)GLEnum.Linear);
        gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureWrapT, (int)GLEnum.ClampToEdge);
        Texture.Unbind(0);

        BuildBitReverseTable();

        // 初始内容必须是"波形在中线、频谱全黑"，否则 shader 采到的是未定义数据
        WriteWaveformRow();
        WriteSpectrumRow();
        Upload();
    }

    /// <summary>绑定 <c>music</c> 通道用的纹理。</summary>
    public Texture Texture { get; }

    /// <summary>已经喂进来的帧数（用于判断有没有音频）。</summary>
    public long TotalFrames { get; private set; }

    /// <summary>
    /// 吃一批交错立体声采样，并重算纹理（频谱行 + 波形行）后上传。
    /// </summary>
    public void Push(ReadOnlySpan<float> interleavedStereo)
    {
        int frames = interleavedStereo.Length / 2;
        if (frames == 0)
        {
            return;
        }

        // 只保留最后 HistoryFrames 帧
        if (frames >= HistoryFrames)
        {
            interleavedStereo = interleavedStereo[^((HistoryFrames) * 2)..];
            frames = HistoryFrames;
            _historyStart = 0;
            _historyCount = 0;
        }

        for (var i = 0; i < frames; i++)
        {
            int slot = (_historyStart + _historyCount) % HistoryFrames;
            _history[slot * 2] = interleavedStereo[i * 2];
            _history[(slot * 2) + 1] = interleavedStereo[(i * 2) + 1];

            if (_historyCount < HistoryFrames)
            {
                _historyCount++;
            }
            else
            {
                _historyStart = (_historyStart + 1) % HistoryFrames;
            }
        }

        TotalFrames += frames;

        WriteWaveformRow();
        WriteSpectrumRow();
        Upload();
    }

    public void Dispose() => Texture.Dispose();

    private void WriteWaveformRow()
    {
        // 波形行：把最近 Width 帧线性铺满整行（左右声道各占 r/g）
        var row = Height - 1;

        for (var x = 0; x < Width; x++)
        {
            int offset = _historyCount == 0
                ? 0
                : Math.Clamp(_historyCount - Width + x, 0, _historyCount - 1);

            int slot = (_historyStart + offset) % HistoryFrames;
            float left = _history[slot * 2];
            float right = _history[(slot * 2) + 1];

            int index = ((row * Width) + x) * 4;
            _pixels[index] = ToByte(left * 0.5f + 0.5f);
            _pixels[index + 1] = ToByte(right * 0.5f + 0.5f);
            _pixels[index + 2] = 0;
            _pixels[index + 3] = 255;
        }
    }

    private void WriteSpectrumRow()
    {
        // 频谱行：取最近 FftSize 帧（不足补 0），做一次 FFT，再按对数频率轴映射到 512 列
        for (var i = 0; i < FftSize; i++)
        {
            int offset = _historyCount - FftSize + i;
            if (offset < 0)
            {
                _windowLeft[i] = 0f;
                _windowRight[i] = 0f;
                continue;
            }

            int slot = (_historyStart + offset) % HistoryFrames;
            _windowLeft[i] = _history[slot * 2];
            _windowRight[i] = _history[(slot * 2) + 1];
        }

        int usableBins = FftSize / 2;

        // 每个声道只算一次 FFT（512 列各算一次会慢两个数量级）
        ComputeMagnitudes(_windowLeft, _magnitudeLeft);
        ComputeMagnitudes(_windowRight, _magnitudeRight);

        for (var x = 0; x < Width; x++)
        {
            // 20 Hz .. 20 kHz 对数分布（44100 → 奈奎斯特 22050）
            double frequency = 20.0 * Math.Pow(20000.0 / 20.0, (double)x / (Width - 1));
            int bin = (int)Math.Round(frequency / (AudioBuffer.SampleRate / 2.0) * usableBins);
            bin = Math.Clamp(bin, 1, usableBins - 1);

            int index = x * 4;
            _pixels[index] = ToByte(_magnitudeLeft[bin]);
            _pixels[index + 1] = ToByte(_magnitudeRight[bin]);
            _pixels[index + 2] = 0;
            _pixels[index + 3] = 255;
        }
    }

    /// <summary>512 点实数序列 → 0..1 的幅度（-60 dB..0 dB 映射）。</summary>
    private void ComputeMagnitudes(float[] samples, float[] destination)
    {
        for (var i = 0; i < FftSize; i++)
        {
            // Hann 窗：不加窗时低频会漏得到处都是
            float window = 0.5f - (0.5f * MathF.Cos(2f * MathF.PI * i / (FftSize - 1)));
            _fft[_bitReverse[i]] = new Complex(samples[i] * window, 0.0);
        }

        Transform();

        for (var bin = 0; bin < destination.Length; bin++)
        {
            double magnitude = _fft[bin].Magnitude / (FftSize * 0.25);

            // -60 dB..0 dB → 0..1，别让小音量直接看不见
            double db = 20.0 * Math.Log10(magnitude + 1e-9);
            destination[bin] = (float)Math.Clamp((db + 60.0) / 60.0, 0.0, 1.0);
        }
    }

    /// <summary>就地基 2 FFT（输入已按位反转排好）。</summary>
    private void Transform()
    {
        for (int size = 2; size <= FftSize; size <<= 1)
        {
            int half = size >> 1;
            double angleStep = -2.0 * Math.PI / size;

            for (int start = 0; start < FftSize; start += size)
            {
                for (var k = 0; k < half; k++)
                {
                    var w = new Complex(Math.Cos(angleStep * k), Math.Sin(angleStep * k));
                    Complex even = _fft[start + k];
                    Complex odd = _fft[start + k + half] * w;

                    _fft[start + k] = even + odd;
                    _fft[start + k + half] = even - odd;
                }
            }
        }
    }

    private void BuildBitReverseTable()
    {
        var bits = (int)Math.Log2(FftSize);
        for (var i = 0; i < FftSize; i++)
        {
            var reversed = 0;
            for (var b = 0; b < bits; b++)
            {
                reversed = (reversed << 1) | ((i >> b) & 1);
            }

            _bitReverse[i] = reversed;
        }
    }

    private void Upload()
    {
        var gl = MilkDrop.Rendering.Gles3.GlContext.Gl;
        Texture.Bind(0);
        gl.PixelStore(GLEnum.UnpackAlignment, 4);
        gl.TexSubImage2D(GLEnum.Texture2D, 0, 0, 0, Width, Height, GLEnum.Rgba, GLEnum.UnsignedByte, _pixels);
        Texture.Unbind(0);
    }

    private static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
}
