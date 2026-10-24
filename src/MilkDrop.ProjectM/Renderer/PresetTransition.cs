using System.Diagnostics;
using System.Numerics;
using MilkDrop.ProjectM.Audio;
using MilkDrop.Rendering.Gles3;
using Silk.NET.OpenGLES;
using Sampler = MilkDrop.Rendering.Gles3.Sampler;
using Shader = MilkDrop.Rendering.Gles3.Shader;
using Texture = MilkDrop.Rendering.Gles3.Texture;

namespace MilkDrop.ProjectM.Renderer;

/// <summary>
/// 用一张全屏 quad + 片元着色器把旧预设淡出、新预设淡入（对应 <c>Renderer/PresetTransition</c>）。
/// </summary>
public sealed class PresetTransition : RenderItem
{
    private const double Pi = 3.14159265358979323846;

    /// <summary>转场 shader 可用的噪声纹理（与 C++ 的列表一致）。</summary>
    private static readonly string[] NoiseTextureNames =
    [
        "noise_lq",
        "pw_noise_lq",
        "noise_mq",
        "pw_noise_mq",
        "noise_hq",
        "pw_noise_hq",
        "noisevol_lq",
        "pw_noisevol_lq",
        "noisevol_hq",
        "pw_noisevol_hq",
    ];

    private readonly Shader? _transitionShader;
    private readonly Sampler _presetSampler = new((int)GLEnum.ClampToEdge, (int)GLEnum.Linear);

    private readonly double _durationSeconds;
    private readonly long _transitionStartTimestamp;
    private long _lastFrameTimestamp;

    /// <summary>转场期间保持不变的 4 个随机整数。</summary>
    private readonly int[] _staticRandomValues = new int[4];

    private readonly Random _random = new();

    public PresetTransition(Shader? transitionShader, double durationSeconds)
    {
        _transitionShader = transitionShader;
        _durationSeconds = durationSeconds;

        for (var i = 0; i < 4; i++)
        {
            _staticRandomValues[i] = _random.Next();
        }

        _transitionStartTimestamp = Stopwatch.GetTimestamp();
        _lastFrameTimestamp = _transitionStartTimestamp;

        Init();
    }

    public override unsafe void InitVertexAttrib()
    {
        var gl = GlContext.Gl;

        var points = new Point[4];
        points[0] = new Point { X = -1.0f, Y = 1.0f };
        points[1] = new Point { X = 1.0f, Y = 1.0f };
        points[2] = new Point { X = -1.0f, Y = -1.0f };
        points[3] = new Point { X = 1.0f, Y = -1.0f };

        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 2, GLEnum.Float, false, 2 * sizeof(float), (void*)0);
        gl.BufferData<Point>(GLEnum.ArrayBuffer, points, GLEnum.StaticDraw);
    }

    /// <summary>转场是否已结束（时长为 0 视为立即结束）。</summary>
    public bool IsDone()
    {
        double secondsSinceStart = Stopwatch.GetElapsedTime(_transitionStartTimestamp).TotalSeconds;
        return _durationSeconds <= 0.0 || secondsSinceStart >= _durationSeconds;
    }

    /// <summary>画一帧转场（对应 <c>PresetTransition::Draw</c>）。</summary>
    public void Draw(IPreset oldPreset, IPreset newPreset, RenderContext context, FrameAudioData audioData)
    {
        ArgumentNullException.ThrowIfNull(oldPreset);
        ArgumentNullException.ThrowIfNull(newPreset);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(audioData);

        if (_transitionShader is null)
        {
            return;
        }

        double secondsSinceStart = Stopwatch.GetElapsedTime(_transitionStartTimestamp).TotalSeconds;

        // 时长为 0 时三个进度都是 1.0。
        double linearProgress = 1.0;
        double cosineProgress = 1.0;
        double bicubicProgress = 1.0;

        if (_durationSeconds > 0.0)
        {
            linearProgress = secondsSinceStart / _durationSeconds;
            cosineProgress = (-Math.Cos(linearProgress * Pi) + 1.0) * 0.5;
            bicubicProgress = linearProgress < 0.5
                ? 4.0 * linearProgress * linearProgress * linearProgress
                : 1.0 - Math.Pow(-2.0 * linearProgress + 2.0, 3.0) / 2.0;
        }

        _transitionShader.Bind();

        _transitionShader.SetUniformFloat3("iResolution",
            new Vector3(context.ViewportSizeX, context.ViewportSizeY, 0.0f));

        _transitionShader.SetUniformFloat4("durationParams",
            new Vector4((float)linearProgress, (float)cosineProgress, (float)bicubicProgress, (float)_durationSeconds));

        _transitionShader.SetUniformFloat2("timeParams",
            new Vector2((float)secondsSinceStart,
                        (float)Stopwatch.GetElapsedTime(_lastFrameTimestamp).TotalSeconds));

        _transitionShader.SetUniformInt4("iRandStatic",
            _staticRandomValues[0], _staticRandomValues[1], _staticRandomValues[2], _staticRandomValues[3]);

        _transitionShader.SetUniformInt4("iRandFrame",
            _random.Next(), _random.Next(), _random.Next(), _random.Next());

        _transitionShader.SetUniformFloat3("iBeatValues", new Vector3(audioData.Bass, audioData.Mid, audioData.Treb));
        _transitionShader.SetUniformFloat3("iBeatAttValues",
            new Vector3(audioData.BassAtt, audioData.MidAtt, audioData.TrebAtt));

        // 两张预设纹理。
        _transitionShader.SetUniformInt("iChannel0", 0);
        _transitionShader.SetUniformInt("iChannel1", 1);

        Texture? oldTexture = oldPreset.OutputTexture();
        Texture? newTexture = newPreset.OutputTexture();

        oldTexture?.Bind(0, _presetSampler);
        newTexture?.Bind(1, _presetSampler);

        // 噪声纹理从 2 号单元开始。
        int textureUnit = 2;
        var descriptors = new TextureSamplerDescriptor[NoiseTextureNames.Length];

        for (var i = 0; i < NoiseTextureNames.Length; i++)
        {
            if (context.TextureManager is null)
            {
                break;
            }

            descriptors[i] = context.TextureManager.GetTexture(NoiseTextureNames[i]);
            descriptors[i].Bind(textureUnit, _transitionShader);
            textureUnit++;
        }

        GlContext.Gl.BindVertexArray(VaoId);
        GlContext.Gl.DrawArrays(GLEnum.TriangleStrip, 0, 4);
        GlContext.Gl.BindVertexArray(0);

        oldTexture?.Unbind(0);
        newTexture?.Unbind(1);

        for (var i = 0; i < descriptors.Length; i++)
        {
            if (descriptors[i] is not null && i + 2 < textureUnit)
            {
                descriptors[i].Unbind(i + 2);
            }
        }

        Shader.Unbind();

        // 更新上一帧时间。
        _lastFrameTimestamp = Stopwatch.GetTimestamp();
    }

    public override void Dispose()
    {
        if (GlContext.IsInitialized)
        {
            _presetSampler.Dispose();
        }

        base.Dispose();
    }
}
