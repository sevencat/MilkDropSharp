using System.Numerics;
using Silk.NET.OpenGLES;
using MilkDrop.ShaderToy.Channels;
using MilkDrop.ShaderToy.Compile;
using MilkDrop.ShaderToy.Pass;
using MilkDrop.ShaderToy.Renderer;

using Framebuffer = MilkDrop.Rendering.Gles3.Framebuffer;
using Sampler = MilkDrop.Rendering.Gles3.Sampler;
using Shader = MilkDrop.Rendering.Gles3.Shader;
using Texture = MilkDrop.Rendering.Gles3.Texture;

namespace MilkDrop.ShaderToy.Audio;

/// <summary>
/// Sound pass 的 GPU 渲染与回读（上游没有这个 pass，纯新增）。
/// </summary>
/// <remarks>
/// <para>
/// 语义与网页端一致：一个<b>片元</b> = 一个采样点，输出 <c>(left, right, 0, 1)</c>。
/// 一次渲染 <see cref="BatchWidth"/>×<see cref="BatchHeight"/> 个片元（默认 512×2 = 1024 个采样），
/// 再用 <c>glReadPixels</c> 读回 float 交错成立体声（S6-1 / S6-2）。
/// </para>
/// <para>
/// 回读是同步的（每批一个 GPU 同步点）。先做简单正确的版本，P9 再评估 PBO 异步回读（S6-3）。
/// </para>
/// </remarks>
public sealed class SoundPassRenderer : IDisposable
{
    /// <summary>批次宽度（一个片元一个采样点）。</summary>
    public const int BatchWidth = 512;

    /// <summary>批次高度。</summary>
    public const int BatchHeight = 2;

    /// <summary>渲染采样率：与网页端一致，固定 44100。</summary>
    public const int SampleRate = AudioBuffer.SampleRate;

    private readonly PassTarget _target;
    private readonly FullscreenQuad _quad = new();
    private readonly Shader _program;
    private readonly float[] _raw = new float[BatchWidth * BatchHeight * 4];
    private readonly GpuInput _input = new();
    private readonly PlaceholderTextures _placeholders;
    private readonly Sampler _sampler;
    private readonly Texture?[] _channelTextures = new Texture?[4];
    private bool _disposed;

    /// <param name="pass">sound pass。</param>
    /// <param name="commonCode">所有 <c>common</c> pass 拼起来的代码。</param>
    /// <param name="music">音频通道纹理（作品用 <c>music</c> 输入时绑它），可为 null。</param>
    /// <param name="texture3DSupported">后端是否支持 <c>sampler3D</c>（决定 volume 占位要不要建）。</param>
    public SoundPassRenderer(
        ShaderPass pass,
        string? commonCode,
        MusicTexture? music = null,
        bool texture3DSupported = true)
    {
        ArgumentNullException.ThrowIfNull(pass);

        ShaderToyTranspileResult result = ShaderToyTranspiler.Transpile(pass, commonCode);
        Source = result.Source;
        Notes.AddRange(result.Notes);

        _program = new Shader();
        _program.CompileProgram(ShaderToyTranspiler.FullscreenVertexSource, result.Source);

        // 音频要 float 精度：RGBA16F 的尾数只有 10 位，-60 dB 以下全是量化噪声
        _target = new PassTarget("sound", BatchWidth, BatchHeight, (int)GLEnum.Rgba32f, GLEnum.Rgba, GLEnum.Float);

        if (!_target.IsComplete())
        {
            Notes.Add("sound 目标 RGBA32F 不完整（缺 EXT_color_buffer_float？），音频可能读回 0。");
        }

        // 通道：sound pass 也会用 iChannelN（常见的是 music，少数作品拿 buffer 数据做"数据驱动的声音"）。
        // 不绑的话采样结果未定义 —— 这里按声明类型铺好占位，music 绑真实音频纹理。
        _placeholders = new PlaceholderTextures(texture3DSupported);
        _sampler = new Sampler((int)GLEnum.ClampToEdge, (int)GLEnum.Nearest);

        ShaderInputType[] types = ShaderToyTranspiler.ResolveChannelTypes(pass);
        for (var channel = 0; channel < types.Length; channel++)
        {
            _channelTextures[channel] = types[channel] switch
            {
                ShaderInputType.Music or ShaderInputType.MusicStream => music?.Texture ?? _placeholders.Black2D,
                ShaderInputType.Cubemap => _placeholders.BlackCube,
                ShaderInputType.Volume => _placeholders.Black3D,
                ShaderInputType.Buffer => null,
                _ => _placeholders.Black2D,
            };

            if (types[channel] == ShaderInputType.Buffer)
            {
                Notes.Add($"sound pass 的通道 {channel} 引用了 buffer：P6 不绑（采样结果未定义），需要时再补。");
            }
        }

        _input.Resolution = new Vector3(BatchWidth, BatchHeight, 1f);
        _input.SampleRate = SampleRate;
        _input.Date = GpuInput.MakeDate(DateTime.Now);
    }

    /// <summary>转译后的 GLSL（编译失败时便于定位）。</summary>
    public string Source { get; }

    /// <summary>转译/编译提示。</summary>
    public List<string> Notes { get; } = [];

    /// <summary>一批产出多少个采样点。</summary>
    public int SamplesPerBatch => BatchWidth * BatchHeight;

    /// <summary>
    /// 渲染从 <paramref name="startSample"/> 开始的一批采样，写进交错立体声
    /// <paramref name="destination"/>（长度需 ≥ <see cref="SamplesPerBatch"/>×2）。
    /// </summary>
    /// <returns>实际产出的采样点数。</returns>
    public int RenderBatch(long startSample, Span<float> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return RenderBatch(startSample, SampleRate, destination);
    }

    /// <summary>渲染一批采样（可指定采样率，测试里会用到）。</summary>
    public int RenderBatch(long startSample, int sampleRate, Span<float> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int samples = SamplesPerBatch;
        if (destination.Length < samples * 2)
        {
            throw new ArgumentException($"目标缓冲至少需要 {samples * 2} 个 float。", nameof(destination));
        }

        var gl = MilkDrop.Rendering.Gles3.GlContext.Gl;

        _input.Time = (float)startSample / sampleRate;
        _input.SampleRate = sampleRate;
        _input.TimeDelta = (float)samples / sampleRate;
        _input.FrameRate = 1000f;

        _target.BindWrite();
        gl.Viewport(0, 0, BatchWidth, BatchHeight);
        gl.Disable(EnableCap.ScissorTest);
        gl.Disable(EnableCap.Blend);
        gl.Disable(EnableCap.DepthTest);
        gl.Disable(EnableCap.CullFace);
        gl.ColorMask(true, true, true, true);

        _program.Bind();

        // 先绑通道，再统一推 uniform（iChannelResolution/iChannelTime 依赖绑定结果）
        for (var channel = 0; channel < _channelTextures.Length; channel++)
        {
            if (_channelTextures[channel] is { } texture)
            {
                texture.Bind(channel, _sampler);
                _input.ChannelResolution[channel] = new Vector3(texture.Width, texture.Height, 1f);
            }
            else
            {
                _input.ChannelResolution[channel] = Vector3.Zero;
            }

            _input.ChannelTime[channel] = _input.Time;
        }

        _input.PushTo(_program);
        _program.SetUniformInt(MainSoundEntry.SampleIndexUniform, (int)startSample);
        _program.SetUniformInt(MainSoundEntry.BatchWidthUniform, BatchWidth);
        _program.SetUniformFloat(MainSoundEntry.SampleRateUniform, sampleRate);

        _quad.Draw();

        gl.PixelStore(GLEnum.PackAlignment, 4);
        ReadPixels(gl);

        Shader.Unbind();
        Framebuffer.Unbind();

        // RGBA 交错 → 立体声交错，顺手限幅（超范围会爆音）
        for (var i = 0; i < samples; i++)
        {
            destination[i * 2] = Clamp(_raw[i * 4]);
            destination[(i * 2) + 1] = Clamp(_raw[(i * 4) + 1]);
        }

        return samples;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _program.Dispose();
        _target.Dispose();
        _quad.Dispose();
        _sampler.Dispose();
        _placeholders.Dispose();
    }

    private unsafe void ReadPixels(GL gl)
    {
        fixed (float* pointer = _raw)
        {
            gl.ReadPixels(0, 0, BatchWidth, BatchHeight, GLEnum.Rgba, GLEnum.Float, pointer);
        }
    }

    private static float Clamp(float value) => float.IsFinite(value) ? Math.Clamp(value, -1f, 1f) : 0f;
}
